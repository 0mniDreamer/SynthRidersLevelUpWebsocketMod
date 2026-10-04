using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MelonLoader;
using SynthRidersWebsocketMod.Harmony;

namespace SynthRidersWebsocketMod.Core;


/// WebSocket server that broadcasts all Synth Riders game events.

/// Implemented directly on top of <see cref="TcpListener"/> with a hand-rolled RFC 6455
/// handshake and frame codec. This deliberately avoids HttpListener.AcceptWebSocketAsync /
/// System.Net.WebSockets, whose managed server stack initialises the native Windows
/// websocket.dll (WebSocketProtocolComponent). That native component does not exist under
/// Proton/Wine, so on Linux every upgrade there threw "The type initializer for
/// 'System.Net.WebSockets.WebSocketProtocolComponent' threw an exception." Raw TCP + a manual
/// codec is pure Winsock, which Wine supports, and behaves identically on real Windows.

public class EventServer
{
    private const string WsMagicGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private TcpListener _tcpListener;
    private CancellationTokenSource _cts;
    private readonly ConcurrentDictionary<Guid, WsClient> _clients = new();
    private bool _isRunning;

    private string _host = "localhost";
    private int _port = 9000;

    // Current state tracking
    private string _currentScene = "";
    private bool _isInGame = false;

    // Background send queue - keeps JSON serialization + socket I/O OFF the game thread.
    // Bounded + DropOldest so a stalled client never grows memory or lags gameplay.
    private Channel<QueuedEvent> _sendChannel;

    // Cached config entries (avoids per-event MelonPreferences lookups)
    private MelonPreferences_Entry<bool> _logAllEntry;
    private MelonPreferences_Entry<bool> _logMajorEntry;

    // Reused serializer options (compact for broadcasts, indented only for HTTP status)
    private static readonly JsonSerializerOptions CompactJson = new();
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    // "Sticky" events: the latest payload is cached and replayed to any newly connected
    // client so late-joining overlays immediately reflect current state (e.g. progression).
    private static readonly HashSet<string> StickyEventTypes = new() { "ProgressionUpdate" };
    private readonly ConcurrentDictionary<string, object> _stickyEvents = new();

    public bool IsRunning => _isRunning;
    public int ClientCount => _clients.Count;

    /// True if at least one client is connected. Patches check this to skip payload work.
    public bool HasClients => !_clients.IsEmpty;

    /// Lightweight snapshot enqueued by the game thread; serialized later on the worker.
    private readonly struct QueuedEvent
    {
        public readonly string EventType;
        public readonly object Data;
        public readonly DateTime Timestamp;

        public QueuedEvent(string eventType, object data, DateTime timestamp)
        {
            EventType = eventType;
            Data = data;
            Timestamp = timestamp;
        }
    }


    /// All available event types this server can broadcast
 
    public static readonly string[] AvailableEvents = new[]
    {
        // Connection
        "Connected", "SceneChanged",

        // Gameplay - Notes & Scoring
        "NoteHit", "ComboBreak", "ComboMilestone",

        // Gameplay - Obstacles
        "WallHit", "RailCleared",

        // Gameplay - Health
        "HealthUpdate", "HealthWarning", "HealthDepleted",

        // Gameplay - Flow
        "SongStart", "SongInfo", "SongEnd", "LevelLoaded",

        // Progression - XP & Levels
        "LevelUp", "XPGained", "ProgressionUpdate",

        // Progression - Song Session
        "SongSessionComplete"
    };

  
    /// Start the WebSocket server
  
    public void Start(string host = "localhost", int port = 9000)
    {
        if (_isRunning)
        {
            MelonLogger.Warning("[EventServer] Already running");
            return;
        }

        _host = host;
        _port = port;

        try
        {
            // Initialize IL2CPP-compatible Harmony patches
            RuntimePatches.Initialize(this);

            // Cache config entries once (avoids per-event preference lookups)
            CacheConfigEntries();

            // Create the background send queue (bounded; drop oldest under backpressure)
            _sendChannel = Channel.CreateBounded<QueuedEvent>(new BoundedChannelOptions(2048)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            // Start raw TCP listener (we do the WebSocket upgrade + framing ourselves).
            _cts = new CancellationTokenSource();
            var bindAddress = ResolveBindAddress(_host);
            _tcpListener = new TcpListener(bindAddress, _port);
            _tcpListener.Start();
            _isRunning = true;

            // Accept connections + process send queue in background
            Task.Run(AcceptConnectionsLoop);
            Task.Run(ProcessSendQueue);

            MelonLogger.Msg($"[EventServer] Started on ws://{_host}:{_port}");
            MelonLogger.Msg($"[EventServer] {AvailableEvents.Length} event types available");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[EventServer] Failed to start: {ex.Message}");
            _isRunning = false;
        }
    }

  
    /// Resolve the configured host string to an <see cref="IPAddress"/> to bind.
    /// "localhost" binds loopback only; "0.0.0.0"/"*"/"+" bind all interfaces (LAN access);
    /// anything else is parsed as a literal IP, falling back to all interfaces.
   
    private static IPAddress ResolveBindAddress(string host)
    {
        if (string.IsNullOrEmpty(host)) return IPAddress.Loopback;

        var h = host.Trim();
        if (string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        if (h == "0.0.0.0" || h == "*" || h == "+") return IPAddress.Any;

        IPAddress parsed;
        if (IPAddress.TryParse(h, out parsed)) return parsed;

        MelonLogger.Warning($"[EventServer] Could not parse host '{host}'; binding all interfaces.");
        return IPAddress.Any;
    }

  
    /// Stop the WebSocket server

    public void Stop()
    {
        if (!_isRunning) return;

        try
        {
            _isRunning = false;
            _cts?.Cancel();
            _sendChannel?.Writer.TryComplete();

            // Close all client connections
            foreach (var kvp in _clients)
            {
                try { kvp.Value.Dispose(); }
                catch { }
            }
            _clients.Clear();

            // Stopping the listener unblocks the pending AcceptTcpClientAsync.
            try { _tcpListener?.Stop(); }
            catch { }

            MelonLogger.Msg("[EventServer] Stopped");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[EventServer] Error stopping: {ex.Message}");
        }
    }

    private async Task AcceptConnectionsLoop()
    {
        while (_isRunning && !_cts.Token.IsCancellationRequested)
        {
            try
            {
                var tcp = await _tcpListener.AcceptTcpClientAsync();
                _ = HandleConnection(tcp);
            }
            catch (ObjectDisposedException)
            {
                break; // listener stopped
            }
            catch (Exception ex) when (!_cts.Token.IsCancellationRequested)
            {
                MelonLogger.Warning($"[EventServer] Accept error: {ex.Message}");
            }
        }
    }

    /// Reads the incoming HTTP request line + headers and either upgrades to WebSocket
    /// (Upgrade: websocket + Sec-WebSocket-Key present) or answers the status JSON for a
    /// plain GET, then hands off to the per-client read loop.

    private async Task HandleConnection(TcpClient tcp)
    {
        NetworkStream stream = null;
        try
        {
            tcp.NoDelay = true;
            stream = tcp.GetStream();

            var requestHeader = await ReadHttpHeaderAsync(stream, _cts.Token);
            if (requestHeader == null)
            {
                try { tcp.Close(); } catch { }
                return;
            }

            var headers = ParseHeaders(requestHeader);

            string upgrade;
            headers.TryGetValue("upgrade", out upgrade);
            string wsKey;
            headers.TryGetValue("sec-websocket-key", out wsKey);

            bool isWebSocket = upgrade != null &&
                               upgrade.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) >= 0 &&
                               !string.IsNullOrEmpty(wsKey);

            if (isWebSocket)
            {
                await CompleteHandshake(stream, wsKey, _cts.Token);
                await RunClient(tcp, stream);
            }
            else
            {
                await SendHttpStatus(stream, _cts.Token);
                try { tcp.Close(); } catch { }
            }
        }
        catch (Exception ex)
        {
            if (!_cts.Token.IsCancellationRequested)
                MelonLogger.Warning($"[EventServer] Connection error: {ex.Message}");
            try { tcp.Close(); } catch { }
        }
    }


    private static async Task<string> ReadHttpHeaderAsync(NetworkStream stream, CancellationToken token)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        int total = 0;

        while (total < 16384)
        {
            int n = await stream.ReadAsync(one, 0, 1, token);
            if (n == 0) return total == 0 ? null : sb.ToString(); // EOF
            sb.Append((char)one[0]);
            total++;

            int len = sb.Length;
            if (len >= 4 &&
                sb[len - 1] == '\n' && sb[len - 2] == '\r' &&
                sb[len - 3] == '\n' && sb[len - 4] == '\r')
            {
                break;
            }
        }

        return sb.ToString();
    }

    private static Dictionary<string, string> ParseHeaders(string header)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = header.Split('\n');

        for (int i = 1; i < lines.Length; i++) // skip request line
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var key = line.Substring(0, colon).Trim().ToLowerInvariant();
            var value = line.Substring(colon + 1).Trim();
            map[key] = value;
        }

        return map;
    }

  
    private static async Task CompleteHandshake(NetworkStream stream, string wsKey, CancellationToken token)
    {
        string accept;
        using (var sha1 = SHA1.Create())
        {
            var hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(wsKey + WsMagicGuid));
            accept = Convert.ToBase64String(hash);
        }

        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 101 Switching Protocols\r\n");
        sb.Append("Upgrade: websocket\r\n");
        sb.Append("Connection: Upgrade\r\n");
        sb.Append("Sec-WebSocket-Accept: ").Append(accept).Append("\r\n");
        sb.Append("\r\n");

        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(bytes, 0, bytes.Length, token);
        await stream.FlushAsync(token);
    }


    private async Task RunClient(TcpClient tcp, NetworkStream stream)
    {
        var client = new WsClient(Guid.NewGuid(), tcp, stream);
        _clients.TryAdd(client.Id, client);
        MelonLogger.Msg($"[EventServer] Client connected ({_clients.Count} total)");

        try
        {
            await SendWelcomeMessage(client);

            // Replay cached sticky events (e.g. current progression) so the overlay
            // populates immediately instead of waiting for the next change.
            foreach (var kvp in _stickyEvents)
            {
                if (!client.IsOpen) break;
                await SendToClient(client, kvp.Key, kvp.Value);
            }

            // Also trigger a fresh proactive read on the next Unity frame, in case we have
            // no cached snapshot yet (e.g. connected while still in the menu).
            RuntimePatches.RequestProgressionSnapshot();

            await ReadLoop(client);
        }
        catch (Exception ex)
        {
            if (!ex.Message.Contains("closed") && !ex.Message.Contains("aborted"))
                MelonLogger.Warning($"[EventServer] Client error: {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            client.Dispose();
            MelonLogger.Msg($"[EventServer] Client disconnected ({_clients.Count} remaining)");
        }
    }

    /// Per-client frame read loop: dispatches text messages, answers pings, honours close.
    private async Task ReadLoop(WsClient client)
    {
        var stream = client.Stream;
        var token = _cts.Token;

        var fragment = new List<byte>();
        int fragmentOpcode = 0;
        var header = new byte[2];

        while (client.IsOpen && !token.IsCancellationRequested)
        {
            if (!await ReadExactAsync(stream, header, 0, 2, token)) break;

            bool fin = (header[0] & 0x80) != 0;
            int opcode = header[0] & 0x0F;
            bool masked = (header[1] & 0x80) != 0;
            long payloadLen = header[1] & 0x7F;

            if (payloadLen == 126)
            {
                var ext = new byte[2];
                if (!await ReadExactAsync(stream, ext, 0, 2, token)) break;
                payloadLen = (ext[0] << 8) | ext[1];
            }
            else if (payloadLen == 127)
            {
                var ext = new byte[8];
                if (!await ReadExactAsync(stream, ext, 0, 8, token)) break;
                payloadLen = 0;
                for (int i = 0; i < 8; i++) payloadLen = (payloadLen << 8) | ext[i];
            }

            // Per RFC 6455 a client frame MUST be masked; reject absurd sizes defensively.
            if (payloadLen < 0 || payloadLen > 10_000_000) break;

            byte[] mask = null;
            if (masked)
            {
                mask = new byte[4];
                if (!await ReadExactAsync(stream, mask, 0, 4, token)) break;
            }

            var payload = new byte[payloadLen];
            if (payloadLen > 0 && !await ReadExactAsync(stream, payload, 0, (int)payloadLen, token)) break;

            if (masked && mask != null)
            {
                for (int i = 0; i < payload.Length; i++)
                    payload[i] = (byte)(payload[i] ^ mask[i & 3]);
            }

            if (opcode == 0x8) // close
            {
                await client.SendFrameAsync(0x8, Array.Empty<byte>(), token);
                break;
            }
            else if (opcode == 0x9) // ping -> pong
            {
                await client.SendFrameAsync(0xA, payload, token);
            }
            else if (opcode == 0xA) // pong -> ignore
            {
            }
            else if (opcode == 0x0 || opcode == 0x1 || opcode == 0x2) // continuation / text / binary
            {
                if (opcode != 0x0)
                {
                    fragmentOpcode = opcode;
                    fragment.Clear();
                }

                if (payload.Length > 0) fragment.AddRange(payload);

                if (fin)
                {
                    if (fragmentOpcode == 0x1) // only text carries client commands
                    {
                        var message = Encoding.UTF8.GetString(fragment.ToArray());
                        await HandleClientMessage(client, message);
                    }
                    fragment.Clear();
                    fragmentOpcode = 0;
                }
            }
            // reserved opcodes: ignored
        }
    }

    /// Reads exactly <paramref name="count"/> bytes or returns false on EOF.
    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken token)
    {
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer, offset + read, count - read, token);
            if (n == 0) return false; // EOF
            read += n;
        }
        return true;
    }

    /// Builds a single unmasked server frame (FIN set). Server-to-client frames are never
    /// masked per RFC 6455. Supports 7-bit, 16-bit and 64-bit payload lengths.

    private static byte[] BuildFrame(byte opcode, byte[] payload)
    {
        int len = payload.Length;
        int headerLen;
        if (len <= 125) headerLen = 2;
        else if (len <= 65535) headerLen = 4;
        else headerLen = 10;

        var frame = new byte[headerLen + len];
        frame[0] = (byte)(0x80 | (opcode & 0x0F)); // FIN + opcode

        if (len <= 125)
        {
            frame[1] = (byte)len;
        }
        else if (len <= 65535)
        {
            frame[1] = 126;
            frame[2] = (byte)((len >> 8) & 0xFF);
            frame[3] = (byte)(len & 0xFF);
        }
        else
        {
            frame[1] = 127;
            // High four bytes are always zero: payloads are capped well under 4 GiB.
            frame[2] = 0; frame[3] = 0; frame[4] = 0; frame[5] = 0;
            frame[6] = (byte)((len >> 24) & 0xFF);
            frame[7] = (byte)((len >> 16) & 0xFF);
            frame[8] = (byte)((len >> 8) & 0xFF);
            frame[9] = (byte)(len & 0xFF);
        }

        if (len > 0) Buffer.BlockCopy(payload, 0, frame, headerLen, len);
        return frame;
    }

    private async Task SendHttpStatus(NetworkStream stream, CancellationToken token)
    {
        try
        {
            var status = new
            {
                name = "Synth Riders WebSocket Events",
                version = "2.5.0",
                status = "running",
                clients = _clients.Count,
                currentScene = _currentScene,
                isInGame = _isInGame,
                websocketUrl = $"ws://{_host}:{_port}",
                events = AvailableEvents
            };

            var json = JsonSerializer.Serialize(status, IndentedJson);
            var body = Encoding.UTF8.GetBytes(json);

            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 200 OK\r\n");
            sb.Append("Content-Type: application/json\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("\r\n");
            var head = Encoding.ASCII.GetBytes(sb.ToString());

            await stream.WriteAsync(head, 0, head.Length, token);
            await stream.WriteAsync(body, 0, body.Length, token);
            await stream.FlushAsync(token);
        }
        catch { }
    }

    private async Task SendWelcomeMessage(WsClient client)
    {
        var welcome = new
        {
            eventType = "Connected",
            data = new
            {
                message = "Connected to Synth Riders WebSocket Events",
                version = "2.5.0",
                currentScene = _currentScene,
                isInGame = _isInGame,
                availableEvents = AvailableEvents
            },
            timestamp = DateTime.UtcNow.ToString("o")
        };

        var json = JsonSerializer.Serialize(welcome, CompactJson);
        await client.SendTextAsync(json, CancellationToken.None);
    }

    private async Task HandleClientMessage(WsClient client, string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            if (root.TryGetProperty("command", out var cmdElement))
            {
                var command = cmdElement.GetString()?.ToLower();

                switch (command)
                {
                    case "ping":
                        await SendToClient(client, "Pong", new { timestamp = DateTime.UtcNow.ToString("o") });
                        break;

                    case "status":
                        await SendToClient(client, "Status", new
                        {
                            clients = _clients.Count,
                            currentScene = _currentScene,
                            isInGame = _isInGame
                        });
                        break;

                    case "events":
                        await SendToClient(client, "EventList", new { events = AvailableEvents });
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[EventServer] Message parse error: {ex.Message}");
        }
    }

    private async Task SendToClient(WsClient client, string eventType, object data)
    {
        if (!client.IsOpen) return;

        try
        {
            var message = new { eventType, data, timestamp = DateTime.UtcNow.ToString("o") };
            var json = JsonSerializer.Serialize(message, CompactJson);
            await client.SendTextAsync(json, CancellationToken.None);
        }
        catch { }
    }


    /// Called when scene changes

    public void OnSceneChanged(string sceneName, int buildIndex)
    {
        _currentScene = sceneName;
        _isInGame = sceneName.Contains("Game") || sceneName.Contains("Stage") || sceneName.Contains("Play");

        BroadcastEvent("SceneChanged", new
        {
            sceneName = sceneName,
            buildIndex = buildIndex,
            isInGame = _isInGame
        });
    }


    /// Broadcast an event to all connected clients. Called by Harmony patches on the game thread.
    /// This only enqueues — all JSON serialization and socket I/O happen on the worker thread,
    /// keeping the VR game thread free of allocation and network stalls.
  
    public void BroadcastEvent(string eventType, object data)
    {
        // Cache sticky events even if no client is connected, so the next client to
        // connect can be brought up to date immediately.
        if (StickyEventTypes.Contains(eventType))
            _stickyEvents[eventType] = data;

        if (_clients.IsEmpty) return;

        // Capture timestamp cheaply (struct, no string alloc); format later on the worker.
        _sendChannel?.Writer.TryWrite(new QueuedEvent(eventType, data, DateTime.UtcNow));
    }


    /// Worker loop: drains the queue, serializes + frames each event once, and sends to all
    /// clients. The frame bytes are built a single time and shared (server frames are unmasked,
    /// so the same buffer is valid for every client). Each client's write is serialized by its
    /// own send lock, preventing overlapping writes on one socket.

    private async Task ProcessSendQueue()
    {
        var reader = _sendChannel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token))
            {
                while (reader.TryRead(out var evt))
                {
                    if (_clients.IsEmpty) continue;

                    byte[] frame;
                    try
                    {
                        var message = new
                        {
                            eventType = evt.EventType,
                            data = evt.Data,
                            timestamp = evt.Timestamp.ToString("o")
                        };
                        var json = JsonSerializer.Serialize(message, CompactJson);
                        frame = BuildFrame(0x1, Encoding.UTF8.GetBytes(json));
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Error($"[EventServer] Serialize error: {ex.Message}");
                        continue;
                    }

                    int sent = 0;
                    foreach (var kvp in _clients)
                    {
                        var client = kvp.Value;
                        if (!client.IsOpen) continue;

                        try
                        {
                            await client.SendRawFrameAsync(frame, _cts.Token);
                            sent++;
                        }
                        catch
                        {
                            // Send failed — drop the broken client so we stop trying.
                            _clients.TryRemove(kvp.Key, out _);
                            client.Dispose();
                        }
                    }

                    LogEvent(evt.EventType, sent);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal on shutdown
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[EventServer] Send queue error: {ex.Message}");
        }
    }

    private static readonly HashSet<string> MajorEvents = new()
    {
        "SongStart", "SongEnd", "LevelUp", "XPGained",
        "HealthDepleted", "SongSessionComplete", "ComboMilestone", "LevelLoaded"
    };

    private void CacheConfigEntries()
    {
        try
        {
            var config = MelonPreferences.GetCategory("SynthRidersWebsocketMod");
            if (config != null)
            {
                _logAllEntry = config.GetEntry<bool>("LogAllEvents");
                _logMajorEntry = config.GetEntry<bool>("LogMajorEventsOnly");
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[EventServer] Could not cache config entries: {ex.Message}");
        }
    }

    private void LogEvent(string eventType, int clientCount)
    {
        if (clientCount == 0) return;

        // Cached entries — no per-event preference lookups.
        if (_logAllEntry?.Value ?? false)
        {
            MelonLogger.Msg($"[Event] {eventType} → {clientCount} client(s)");
            return;
        }

        if ((_logMajorEntry?.Value ?? true) && MajorEvents.Contains(eventType))
        {
            MelonLogger.Msg($"[Event] {eventType} → {clientCount} client(s)");
        }
    }

    /// A single connected client: the TCP socket, its stream, and a send lock that serializes
    /// all writes (welcome/sticky sends can otherwise race the broadcast worker on one socket).
  
    private sealed class WsClient
    {
        public readonly Guid Id;
        public readonly NetworkStream Stream;

        private readonly TcpClient _tcp;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private volatile bool _open = true;

        public WsClient(Guid id, TcpClient tcp, NetworkStream stream)
        {
            Id = id;
            _tcp = tcp;
            Stream = stream;
        }

        public bool IsOpen => _open && _tcp.Connected;

        public Task SendTextAsync(string text, CancellationToken token)
        {
            var frame = BuildFrame(0x1, Encoding.UTF8.GetBytes(text));
            return SendRawFrameAsync(frame, token);
        }

        public Task SendFrameAsync(byte opcode, byte[] payload, CancellationToken token)
        {
            var frame = BuildFrame(opcode, payload);
            return SendRawFrameAsync(frame, token);
        }

        public async Task SendRawFrameAsync(byte[] frame, CancellationToken token)
        {
            if (!_open) return;

            await _sendLock.WaitAsync(token);
            try
            {
                await Stream.WriteAsync(frame, 0, frame.Length, token);
                await Stream.FlushAsync(token);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            _open = false;
            try { Stream?.Dispose(); } catch { }
            try { _tcp?.Close(); } catch { }
            try { _sendLock?.Dispose(); } catch { }
        }
    }
}
