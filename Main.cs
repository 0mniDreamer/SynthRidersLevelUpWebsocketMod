using MelonLoader;
using SynthRidersWebsocketMod.Core;

[assembly: MelonInfo(typeof(SynthRidersWebsocketMod.Main), "Synth Riders WebSocket Events", "2.5.0", "OmniDreamer")]
[assembly: MelonGame("Kluge Interactive", "SynthRiders")]

namespace SynthRidersWebsocketMod;

/// Synth Riders WebSocket Events Mod
/// 
/// Exposes all game events via WebSocket for external integrations.
/// Connect to ws://localhost:9000 to receive events.
/// 
/// Events include:
/// - Gameplay: NoteHit, ComboBreak, WallHit, RailCleared, HealthDepleted, etc.
/// - Progression: LevelUp, XPGained, BadgeUnlocked, MissionCompleted, etc.
/// - Song: SongStart, SongEnd, SongSessionComplete, etc.

public class Main : MelonMod
{
    public static Main Instance { get; private set; }
    
    private EventServer _eventServer;
    private Config _config;

    public override void OnInitializeMelon()
    {
        Instance = this;
        
        // Initialize config
        _config = new Config();
        _config.Initialize();

        MelonLogger.Msg("╔══════════════════════════════════════════════════════════╗");
        MelonLogger.Msg("║     SYNTH RIDERS WEBSOCKET EVENTS v2.5.0                 ║");
        MelonLogger.Msg("╠══════════════════════════════════════════════════════════╣");
        MelonLogger.Msg("║  Exposing game events via WebSocket                      ║");
        MelonLogger.Msg($"║  Connect to: ws://{_config.Host}:{_config.Port,-24} ║");
        MelonLogger.Msg("╚══════════════════════════════════════════════════════════╝");

        // Start the WebSocket server
        _eventServer = new EventServer();
        _eventServer.Start(_config.Host, _config.Port);
    }

    public override void OnApplicationQuit()
    {
        _eventServer?.Stop();
        MelonLogger.Msg("WebSocket server stopped");
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        // Notify server of scene changes
        _eventServer?.OnSceneChanged(sceneName, buildIndex);
    }

    public override void OnUpdate()
    {
        // Main-thread pump for deferred work (e.g. progression snapshot on client connect).
        SynthRidersWebsocketMod.Harmony.RuntimePatches.Tick();
    }

   
    /// Get the event server instance (for other mods to use)
   
    public static EventServer GetEventServer()
    {
        return Instance?._eventServer;
    }
}
