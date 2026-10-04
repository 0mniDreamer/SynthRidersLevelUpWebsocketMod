using MelonLoader;

namespace SynthRidersWebsocketMod.Core;

/// Configuration for the WebSocket server

public class Config
{
    private MelonPreferences_Category _category;
    private MelonPreferences_Entry<string> _host;
    private MelonPreferences_Entry<int> _port;
    private MelonPreferences_Entry<bool> _logAllEvents;
    private MelonPreferences_Entry<bool> _logMajorEventsOnly;
    private MelonPreferences_Entry<bool> _debugLogging;

    public string Host => _host?.Value ?? "localhost";
    public int Port => _port?.Value ?? 9000;
    public bool LogAllEvents => _logAllEvents?.Value ?? false;
    public bool LogMajorEventsOnly => _logMajorEventsOnly?.Value ?? true;
    public bool DebugLogging => _debugLogging?.Value ?? false;

    public void Initialize()
    {
        _category = MelonPreferences.CreateCategory("SynthRidersWebsocketMod", "WebSocket Events Settings");

        _host = _category.CreateEntry(
            "Host",
            "localhost",
            "WebSocket Host",
            "Host address for the WebSocket server (use 0.0.0.0 for all interfaces)"
        );

        _port = _category.CreateEntry(
            "Port",
            9000,
            "WebSocket Port",
            "Port number for the WebSocket server"
        );

        _logAllEvents = _category.CreateEntry(
            "LogAllEvents",
            false,
            "Log All Events",
            "Log every event to the console (very verbose)"
        );

        _logMajorEventsOnly = _category.CreateEntry(
            "LogMajorEventsOnly",
            true,
            "Log Major Events",
            "Log only major events like LevelUp, SongEnd, etc."
        );

        _debugLogging = _category.CreateEntry(
            "DebugLogging",
            false,
            "Debug Logging",
            "Dump raw health values and game object members to help diagnose data issues (verbose)"
        );
    }
}
