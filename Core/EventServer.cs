using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MelonLoader;
using SynthRidersWebsocketMod.Harmony;

namespace SynthRidersWebsocketMod.Core;

/// <summary>
/// WebSocket server that broadcasts all Synth Riders game events.
/// </summary>
public class EventServer
{
    private HttpListener _httpListener;
    private CancellationTokenSource _cts;
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
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

    /// <summary>True if at least one client is connected. Patches check this to skip payload work.</summary>
    public bool HasClients => !_clients.IsEmpty;

    /// <summary>Lightweight snapshot enqueued by the game thread; serialized later on the worker.</summary>
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

    /// <summary>
    /// All available event types this server can broadcast
    /// </summary>
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

    /// <summary>
    /// Start the WebSocket server
    /// </summary>
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

            // Start HTTP listener for WebSocket upgrade
            _cts = new CancellationTokenSource();
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://{_host}:{_port}/");
            _httpListener.Start();
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

    /// <summary>
    /// Stop the WebSocket server
    /// </summary>
    public void Stop()
    {
        if (!_isRunning) return;

        try
        {
            _cts?.Cancel();
            _sendChannel?.Writer.TryComplete();

            // Close all client connections
            foreach (var kvp in _clients)
            {
                try
                {
                    var client = kvp.Value;
                    if (client.State == WebSocketState.Open)
                    {
                        client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server stopping", CancellationToken.None).Wait(1000);
                    }
                }
                catch { }
            }
            _clients.Clear();

            _httpListener?.Stop();
            _httpListener?.Close();
            _isRunning = false;

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
                var context = await _httpListener.GetContextAsync();

                if (context.Request.IsWebSocketRequest)
                {
                    _ = HandleWebSocketConnection(context);
                }
                else
                {
                    // HTTP request - return status JSON
                    await SendHttpStatus(context);
                }
            }
            catch (Exception ex) when (!_cts.Token.IsCancellationRequested)
            {
                MelonLogger.Warning($"[EventServer] Accept error: {ex.Message}");
            }
        }
    }

    private async Task SendHttpStatus(HttpListenerContext context)
    {
        var response = context.Response;
        
        try
        {
            var status = new
            {
                name = "Synth Riders WebSocket Events",
                version = "2.4.0",
                status = "running",
                clients = _clients.Count,
                currentScene = _currentScene,
                isInGame = _isInGame,
                websocketUrl = $"ws://{_host}:{_port}",
                events = AvailableEvents
            };

            var json = JsonSerializer.Serialize(status, IndentedJson);
            var buffer = Encoding.UTF8.GetBytes(json);

            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }
        finally
        {
            response.Close();
        }
    }

    private async Task HandleWebSocketConnection(HttpListenerContext context)
    {
        WebSocket ws = null;
        var clientId = Guid.NewGuid();

        try
        {
            // Accept the WebSocket connection with a small delay to ensure stability
            var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
            ws = wsContext.WebSocket;

            // Verify connection is actually open before proceeding
            if (ws.State != WebSocketState.Open)
            {
                MelonLogger.Warning($"[EventServer] WebSocket not in Open state after accept: {ws.State}");
                return;
            }

            _clients.TryAdd(clientId, ws);
            MelonLogger.Msg($"[EventServer] Client connected ({_clients.Count} total)");

            // Small delay before sending welcome to let connection stabilize
            await Task.Delay(50);

            // Send welcome message with error handling
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    await SendWelcomeMessage(ws);

                    // Replay cached sticky events (e.g. current progression) so the
                    // overlay populates immediately instead of waiting for the next change.
                    foreach (var kvp in _stickyEvents)
                    {
                        if (ws.State != WebSocketState.Open) break;
                        await SendToClient(ws, kvp.Key, kvp.Value);
                    }

                    // Also trigger a fresh proactive read on the next Unity frame, in case
                    // we have no cached snapshot yet (e.g. connected while still in the menu).
                    RuntimePatches.RequestProgressionSnapshot();
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[EventServer] Failed to send welcome: {ex.Message}");
                // Continue anyway - client is still connected
            }

            // Handle messages until disconnected
            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // Respond to close handshake
                        if (ws.State == WebSocketState.CloseReceived)
                        {
                            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        }
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        await HandleClientMessage(clientId, ws, message);
                    }
                }
                catch (WebSocketException wsEx)
                {
                    // WebSocket-specific errors - client probably disconnected
                    if (wsEx.WebSocketErrorCode != WebSocketError.ConnectionClosedPrematurely)
                    {
                        MelonLogger.Warning($"[EventServer] WebSocket error: {wsEx.WebSocketErrorCode}");
                    }
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (WebSocketException wsEx)
        {
            // Don't log connection closed errors as warnings
            if (wsEx.WebSocketErrorCode != WebSocketError.ConnectionClosedPrematurely &&
                wsEx.WebSocketErrorCode != WebSocketError.InvalidState)
            {
                MelonLogger.Warning($"[EventServer] Client WebSocket error: {wsEx.WebSocketErrorCode} - {wsEx.Message}");
            }
        }
        catch (Exception ex)
        {
            // Only log unexpected errors
            if (!ex.Message.Contains("closed") && !ex.Message.Contains("aborted"))
            {
                MelonLogger.Warning($"[EventServer] Client error: {ex.Message}");
            }
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            MelonLogger.Msg($"[EventServer] Client disconnected ({_clients.Count} remaining)");

            if (ws != null)
            {
                try 
                { 
                    if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    }
                    ws.Dispose(); 
                } 
                catch { }
            }
        }
    }

    private async Task SendWelcomeMessage(WebSocket ws)
    {
        var welcome = new
        {
            eventType = "Connected",
            data = new
            {
                message = "Connected to Synth Riders WebSocket Events",
                version = "2.4.0",
                currentScene = _currentScene,
                isInGame = _isInGame,
                availableEvents = AvailableEvents
            },
            timestamp = DateTime.UtcNow.ToString("o")
        };

        var json = JsonSerializer.Serialize(welcome, CompactJson);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task HandleClientMessage(Guid clientId, WebSocket ws, string message)
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
                        await SendToClient(ws, "Pong", new { timestamp = DateTime.UtcNow.ToString("o") });
                        break;

                    case "status":
                        await SendToClient(ws, "Status", new
                        {
                            clients = _clients.Count,
                            currentScene = _currentScene,
                            isInGame = _isInGame
                        });
                        break;

                    case "events":
                        await SendToClient(ws, "EventList", new { events = AvailableEvents });
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[EventServer] Message parse error: {ex.Message}");
        }
    }

    private async Task SendToClient(WebSocket ws, string eventType, object data)
    {
        if (ws.State != WebSocketState.Open) return;

        try
        {
            var message = new { eventType, data, timestamp = DateTime.UtcNow.ToString("o") };
            var json = JsonSerializer.Serialize(message, CompactJson);
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { }
    }

    /// <summary>
    /// Called when scene changes
    /// </summary>
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

    /// <summary>
    /// Broadcast an event to all connected clients. Called by Harmony patches on the game thread.
    /// This only enqueues — all JSON serialization and socket I/O happen on the worker thread,
    /// keeping the VR game thread free of allocation and network stalls.
    /// </summary>
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

    /// <summary>
    /// Worker loop: drains the queue, serializes each event once, and sends to all clients.
    /// Sequential awaited sends per client provide natural backpressure (off the game thread)
    /// and prevent overlapping sends on a single socket.
    /// </summary>
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

                    byte[] bytes;
                    try
                    {
                        var message = new
                        {
                            eventType = evt.EventType,
                            data = evt.Data,
                            timestamp = evt.Timestamp.ToString("o")
                        };
                        bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, CompactJson));
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Error($"[EventServer] Serialize error: {ex.Message}");
                        continue;
                    }

                    var segment = new ArraySegment<byte>(bytes);
                    int sent = 0;

                    foreach (var kvp in _clients)
                    {
                        var client = kvp.Value;
                        if (client.State != WebSocketState.Open) continue;

                        try
                        {
                            await client.SendAsync(segment, WebSocketMessageType.Text, true, _cts.Token);
                            sent++;
                        }
                        catch
                        {
                            // Send failed — drop the broken client so we stop trying.
                            _clients.TryRemove(kvp.Key, out _);
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
}
