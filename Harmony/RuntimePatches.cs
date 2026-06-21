using HarmonyLib;
using MelonLoader;
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using SynthRidersWebsocketMod.Core;

namespace SynthRidersWebsocketMod.Harmony;

/// <summary>
/// Runtime patches for IL2CPP - uses reflection to find and patch methods
/// </summary>
public static class RuntimePatches
{
    private static HarmonyLib.Harmony _harmony;
    private static EventServer _server;

    // State tracking
    private static int _combo = 0;
    private static int _maxCombo = 0;
    private static int _score = 0;
    private static float _lastHealth = 100f;
    private static bool _isInSong = false;
    private static int _lastLevel = -1;
    private static string _currentSong = "";
    private static string _currentAuthor = "";
    private static bool _songInfoSent = false;

    // Last known progression (cached so we can re-broadcast on demand)
    private static int _curLevel = -1;
    private static int _curXP = -1;
    private static int _curTotalXP = -1;
    private static int _curXpForNext = -1;
    private static float _curLevelProgress = -1f;

    // Combo milestones — HashSet for O(1) lookup per note instead of looping the array
    private static readonly System.Collections.Generic.HashSet<int> ComboMilestones = new()
    {
        25, 50, 100, 150, 200, 250, 300, 400, 500, 750, 1000, 1500, 2000
    };

    // Cached types (found at runtime)
    private static Type _gameControlManagerType;
    private static Type _scoreManagerType;
    private static Type _infoProviderType;
    private static Type _badgeManagerType;
    private static Type _xpBarType;
    private static Type _songSessionTrackerType;
    private static Type _snapshotType;

    // Cached reflection for Game_InfoProvider song lookup (resolved once)
    private static FieldInfo _infoInstanceField;
    private static PropertyInfo _nameProperty;
    private static FieldInfo _nameField;
    private static PropertyInfo _authorProperty;
    private static FieldInfo _authorField;
    private static bool _songInfoReflectionResolved;

    // Diagnostics + song-start coordination
    private static bool _debugLogging;
    private static bool _infoProviderDumped;

    // Progression snapshot (proactive read of current level/XP)
    private static Type _progressionManagerType;
    private static volatile bool _progressionSnapshotRequested;
    private static bool _progressionManagerDumped;

    // Candidate member names for song metadata (covers naming differences across Unity branches)
    private static readonly string[] SongNameCandidates =
        { "_name", "_songName", "_title", "_trackName", "name", "songName", "title", "trackName" };
    private static readonly string[] SongAuthorCandidates =
        { "_author", "_artist", "_mapper", "_beatMapper", "author", "artist", "mapper" };
    private static bool _snapshotFieldsDumped;

    public static void Initialize(EventServer server)
    {
        _server = server;
        _harmony = new HarmonyLib.Harmony("com.synthriders.websocketmod.runtime");

        // Read debug flag once
        try
        {
            var cfg = MelonPreferences.GetCategory("SynthRidersWebsocketMod");
            var dbg = cfg?.GetEntry<bool>("DebugLogging");
            _debugLogging = dbg?.Value ?? false;
        }
        catch { _debugLogging = false; }

        MelonLogger.Msg("[RuntimePatches] Searching for game types...");

        // Find all the types we need
        FindGameTypes();

        MelonLogger.Msg("[RuntimePatches] Applying patches...");

        int patchCount = 0;

        // Patch found types
        if (_gameControlManagerType != null)
            patchCount += PatchGameControlManager();
        
        if (_scoreManagerType != null)
            patchCount += PatchScoreManager();
        
        patchCount += PatchProgression();
        patchCount += PatchSceneLoading();

        MelonLogger.Msg($"[RuntimePatches] Applied {patchCount} patches");
    }

    private static void FindGameTypes()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string asmName = asm.GetName().Name;
            
            // Skip system assemblies
            if (asmName.StartsWith("System") || asmName.StartsWith("Microsoft") || 
                asmName.StartsWith("mscorlib") || asmName.StartsWith("netstandard"))
                continue;

            try
            {
                foreach (var type in asm.GetTypes())
                {
                    string fullName = type.FullName ?? "";
                    string name = type.Name;

                    // GameControlManager
                    if (_gameControlManagerType == null && name == "GameControlManager")
                    {
                        _gameControlManagerType = type;
                        MelonLogger.Msg($"[RuntimePatches] Found GameControlManager in {asmName}");
                    }

                    // Game_ScoreManager
                    if (_scoreManagerType == null && name == "Game_ScoreManager")
                    {
                        _scoreManagerType = type;
                        MelonLogger.Msg($"[RuntimePatches] Found Game_ScoreManager in {asmName}");
                    }

                    // Game_InfoProvider
                    if (_infoProviderType == null && name == "Game_InfoProvider")
                    {
                        _infoProviderType = type;
                        MelonLogger.Msg($"[RuntimePatches] Found Game_InfoProvider in {asmName}");
                    }

                    // BadgeManager
                    if (_badgeManagerType == null && name == "BadgeManager" && fullName.Contains("Progression"))
                    {
                        _badgeManagerType = type;
                        MelonLogger.Msg($"[RuntimePatches] Found BadgeManager in {asmName}");
                    }

                    // ProgressionManager (source of current level/XP snapshot)
                    if (_progressionManagerType == null && name == "ProgressionManager")
                    {
                        _progressionManagerType = type;
                        MelonLogger.Msg($"[RuntimePatches] Found ProgressionManager in {asmName}");
                    }

                    // ProgressionXPBar
                    if (_xpBarType == null && name == "ProgressionXPBar")
                    {
                        _xpBarType = type;
                        MelonLogger.Msg($"[RuntimePatches] Found ProgressionXPBar in {asmName}");
                    }

                    // SongSessionTracker
                    if (_songSessionTrackerType == null && name == "SongSessionTracker")
                    {
                        _songSessionTrackerType = type;
                        MelonLogger.Msg($"[RuntimePatches] Found SongSessionTracker in {asmName}");
                    }

                    // PlayerDataSnapshot (carries current level/XP)
                    if (_snapshotType == null && name == "PlayerDataSnapshot")
                    {
                        _snapshotType = type;
                        MelonLogger.Msg($"[RuntimePatches] Found PlayerDataSnapshot in {asmName}");
                    }
                }
            }
            catch (ReflectionTypeLoadException)
            {
                // Some assemblies can't be fully loaded, skip them
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RuntimePatches] Error scanning {asmName}: {ex.Message}");
            }
        }

        // Report what we found/didn't find
        if (_gameControlManagerType == null) MelonLogger.Warning("[RuntimePatches] GameControlManager not found!");
        if (_scoreManagerType == null) MelonLogger.Warning("[RuntimePatches] Game_ScoreManager not found!");
        if (_infoProviderType == null) MelonLogger.Warning("[RuntimePatches] Game_InfoProvider not found!");
    }

    private static void Broadcast(string eventType, object data)
    {
        _server?.BroadcastEvent(eventType, data);
    }

    public static void ResetState()
    {
        _combo = 0;
        _maxCombo = 0;
        _score = 0;
        _lastHealth = 100f;
        _isInSong = false;
        _songInfoSent = false;
    }

    #region GameControlManager Patches

    private static int PatchGameControlManager()
    {
        int count = 0;
        var gcm = _gameControlManagerType;

        // OnScore
        var onScore = gcm.GetMethod("OnScore", BindingFlags.Public | BindingFlags.Instance);
        if (onScore != null)
        {
            try
            {
                _harmony.Patch(onScore, postfix: new HarmonyMethod(typeof(RuntimePatches), nameof(OnScore_Postfix)));
                MelonLogger.Msg("[RuntimePatches] ✓ Patched OnScore");
                count++;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RuntimePatches] ✗ OnScore patch failed: {ex.Message}");
            }
        }
        else
        {
            MelonLogger.Warning("[RuntimePatches] ✗ OnScore method not found");
        }

        // OnComboBreak
        var onComboBreak = gcm.GetMethod("OnComboBreak", BindingFlags.Public | BindingFlags.Instance);
        if (onComboBreak != null)
        {
            try
            {
                _harmony.Patch(onComboBreak, postfix: new HarmonyMethod(typeof(RuntimePatches), nameof(OnComboBreak_Postfix)));
                MelonLogger.Msg("[RuntimePatches] ✓ Patched OnComboBreak");
                count++;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RuntimePatches] ✗ OnComboBreak patch failed: {ex.Message}");
            }
        }

        // OnWallHit
        var onWallHit = gcm.GetMethod("OnWallHit", BindingFlags.Public | BindingFlags.Instance);
        if (onWallHit != null)
        {
            try
            {
                _harmony.Patch(onWallHit, postfix: new HarmonyMethod(typeof(RuntimePatches), nameof(OnWallHit_Postfix)));
                MelonLogger.Msg("[RuntimePatches] ✓ Patched OnWallHit");
                count++;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RuntimePatches] ✗ OnWallHit patch failed: {ex.Message}");
            }
        }

        // OnRailCleared
        var onRailCleared = gcm.GetMethod("OnRailCleared", BindingFlags.Public | BindingFlags.Instance);
        if (onRailCleared != null)
        {
            try
            {
                _harmony.Patch(onRailCleared, postfix: new HarmonyMethod(typeof(RuntimePatches), nameof(OnRailCleared_Postfix)));
                MelonLogger.Msg("[RuntimePatches] ✓ Patched OnRailCleared");
                count++;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RuntimePatches] ✗ OnRailCleared patch failed: {ex.Message}");
            }
        }

        // Awake - for song start
        var awake = gcm.GetMethod("Awake", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (awake != null)
        {
            try
            {
                _harmony.Patch(awake, postfix: new HarmonyMethod(typeof(RuntimePatches), nameof(GCM_Awake_Postfix)));
                MelonLogger.Msg("[RuntimePatches] ✓ Patched GameControlManager.Awake");
                count++;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RuntimePatches] ✗ Awake patch failed: {ex.Message}");
            }
        }

        // ReturnToMenu - for song end
        var returnToMenu = gcm.GetMethod("ReturnToMenu", BindingFlags.Public | BindingFlags.Instance);
        if (returnToMenu != null)
        {
            try
            {
                _harmony.Patch(returnToMenu, prefix: new HarmonyMethod(typeof(RuntimePatches), nameof(ReturnToMenu_Prefix)));
                MelonLogger.Msg("[RuntimePatches] ✓ Patched ReturnToMenu");
                count++;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RuntimePatches] ✗ ReturnToMenu patch failed: {ex.Message}");
            }
        }

        // OnLevelFinishedLoading - song metadata (_name/_author) is populated by
        // SetSongStatusData() around here, so this is the reliable point to read it.
        // (At Awake the values are still empty/stale.)
        var onLevelLoaded = gcm.GetMethod("OnLevelFinishedLoading", BindingFlags.Public | BindingFlags.Instance);
        if (onLevelLoaded != null)
        {
            try
            {
                _harmony.Patch(onLevelLoaded, postfix: new HarmonyMethod(typeof(RuntimePatches), nameof(OnLevelFinishedLoading_Postfix)));
                MelonLogger.Msg("[RuntimePatches] ✓ Patched OnLevelFinishedLoading");
                count++;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RuntimePatches] ✗ OnLevelFinishedLoading patch failed: {ex.Message}");
            }
        }
        else
        {
            MelonLogger.Warning("[RuntimePatches] OnLevelFinishedLoading not found on GameControlManager");
        }

        return count;
    }

    // Postfix for OnScore - Args: controllerType, points, isCombo, controller, cstate, isLine
    public static void OnScore_Postfix(object __0, int __1, bool __2, object __3, object __4, bool __5)
    {
        try
        {
            // Update state FIRST so stats stay correct even when no client is listening.
            _isInSong = true;
            _score += __1;

            bool isMilestone = false;
            if (__2)
            {
                _combo++;
                if (_combo > _maxCombo) _maxCombo = _combo;
                isMilestone = ComboMilestones.Contains(_combo);
            }

            // No client connected → skip all payload allocation (ToString + anon objects).
            if (_server == null || !_server.HasClients) return;

            // If song metadata wasn't ready at Awake, backfill it now.
            if (!_songInfoSent) TryBackfillSongInfo();

            string noteType = __0?.ToString() ?? "unknown";
            string hand = noteType.Contains("Left") ? "left"
                        : noteType.Contains("Right") ? "right"
                        : "unknown";

            Broadcast("NoteHit", new
            {
                points = __1,
                combo = _combo,
                totalScore = _score,
                isCombo = __2,
                isLine = __5,
                noteType,
                hand
            });

            if (isMilestone)
            {
                Broadcast("ComboMilestone", new { combo = _combo });
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[RuntimePatches] OnScore error: {ex.Message}");
        }
    }

    // Args: wasNotHandsClose, eventID, isNote
    public static void OnComboBreak_Postfix(bool __0, int __1, bool __2)
    {
        try
        {
            int lostCombo = _combo;
            _combo = 0; // must always reset, regardless of clients

            if (_server == null || !_server.HasClients) return;

            string reason = __2 ? "missed_note" : (__0 ? "hands_apart" : "obstacle");

            Broadcast("ComboBreak", new
            {
                lostCombo,
                reason,
                isNote = __2,
                wasNotHandsClose = __0,
                eventID = __1
            });
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[RuntimePatches] OnComboBreak error: {ex.Message}");
        }
    }

    public static void OnWallHit_Postfix(object __0)
    {
        try
        {
            string wallType = __0?.GetType().Name ?? "unknown";
            Broadcast("WallHit", new { wallType });
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[RuntimePatches] OnWallHit error: {ex.Message}");
        }
    }

    public static void OnRailCleared_Postfix()
    {
        try
        {
            Broadcast("RailCleared", new { });
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[RuntimePatches] OnRailCleared error: {ex.Message}");
        }
    }

    public static void GCM_Awake_Postfix(object __instance)
    {
        try
        {
            ResetState();
            _isInSong = true;

            // Clear stale metadata from the previous song so we never report the wrong one.
            _currentSong = "";
            _currentAuthor = "";
            _songInfoSent = false;

            // Metadata is NOT ready at Awake — SetSongStatusData() runs later, around
            // OnLevelFinishedLoading. We fire a provisional SongStart so the overlay resets
            // and flips to "playing"; OnLevelFinishedLoading then sends the real song/author.
            Broadcast("SongStart", new
            {
                song = _currentSong,
                author = _currentAuthor
            });

            // Re-broadcast last known progression so the panel isn't blank at song start.
            BroadcastProgression();
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[RuntimePatches] GCM_Awake error: {ex.Message}");
        }
    }

    /// <summary>
    /// Fired once the gameplay scene finishes loading. By now SetSongStatusData() has
    /// populated Game_InfoProvider._name/_author, so this is the reliable read point.
    /// </summary>
    public static void OnLevelFinishedLoading_Postfix()
    {
        try
        {
            GetSongInfo();

            if (!string.IsNullOrEmpty(_currentSong) || !string.IsNullOrEmpty(_currentAuthor))
            {
                _songInfoSent = true;
                Broadcast("SongInfo", new
                {
                    song = _currentSong,
                    author = _currentAuthor
                });
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[RuntimePatches] OnLevelFinishedLoading error: {ex.Message}");
        }
    }

    /// <summary>If song metadata wasn't ready at Awake, retry and emit a SongInfo update.</summary>
    private static void TryBackfillSongInfo()
    {
        if (_songInfoSent) return;

        GetSongInfo();
        if (!string.IsNullOrEmpty(_currentSong) || !string.IsNullOrEmpty(_currentAuthor))
        {
            _songInfoSent = true;
            Broadcast("SongInfo", new
            {
                song = _currentSong,
                author = _currentAuthor
            });
        }
    }

    public static void ReturnToMenu_Prefix(object __instance)
    {
        try
        {
            if (_isInSong)
            {
                GetSongInfo();

                Broadcast("SongEnd", new
                {
                    song = _currentSong,
                    score = _score,
                    maxCombo = _maxCombo
                });

                _isInSong = false;
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[RuntimePatches] ReturnToMenu error: {ex.Message}");
        }
    }

    private static void ResolveSongInfoReflection()
    {
        if (_songInfoReflectionResolved) return;
        _songInfoReflectionResolved = true;

        if (_infoProviderType == null) return;

        _infoInstanceField = _infoProviderType.GetField("s_instance",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        _nameProperty = _infoProviderType.GetProperty("_name");
        _nameField = _infoProviderType.GetField("_name",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _authorProperty = _infoProviderType.GetProperty("_author");
        _authorField = _infoProviderType.GetField("_author",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    }

    private static void GetSongInfo()
    {
        try
        {
            if (_infoProviderType == null) return;
            ResolveSongInfoReflection();

            // Resolve the singleton via property OR field (s_instance/Instance) — the
            // field-only lookup failed on this Unity branch, which is why song was blank.
            var instance = GetSingletonInstance(_infoProviderType);
            if (instance == null)
            {
                if (_debugLogging && !_infoProviderDumped)
                {
                    _infoProviderDumped = true;
                    MelonLogger.Msg("[RuntimePatches][debug] Game_InfoProvider instance is null (not ready yet)");
                }
                return;
            }

            // One-time dump of Game_InfoProvider members so we can confirm exact field names.
            if (_debugLogging && !_infoProviderDumped)
            {
                _infoProviderDumped = true;
                var t = instance.GetType();
                MelonLogger.Msg($"[RuntimePatches][debug] Game_InfoProvider type: {t.FullName}");
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    MelonLogger.Msg($"[RuntimePatches][debug]   prop {p.PropertyType.Name} {p.Name}");
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    MelonLogger.Msg($"[RuntimePatches][debug]   field {f.FieldType.Name} {f.Name}");
            }

            // Primary cached accessors (resolved from the exact _name/_author members)
            string song = null, author = null;

            if (_nameProperty != null) song = _nameProperty.GetValue(instance)?.ToString();
            else if (_nameField != null) song = _nameField.GetValue(instance)?.ToString();

            if (_authorProperty != null) author = _authorProperty.GetValue(instance)?.ToString();
            else if (_authorField != null) author = _authorField.GetValue(instance)?.ToString();

            // Fallback: scan candidate member names if the primary lookups came back empty.
            if (string.IsNullOrEmpty(song))
                song = ReadStringMember(instance, SongNameCandidates);
            if (string.IsNullOrEmpty(author))
                author = ReadStringMember(instance, SongAuthorCandidates);

            if (!string.IsNullOrEmpty(song)) _currentSong = song;
            if (!string.IsNullOrEmpty(author)) _currentAuthor = author;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[RuntimePatches] GetSongInfo error: {ex.Message}");
        }
    }

    /// <summary>Reads the first non-empty string property/field from candidate names.</summary>
    private static string ReadStringMember(object obj, string[] names)
    {
        var type = obj.GetType();
        foreach (var n in names)
        {
            try
            {
                var prop = type.GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.PropertyType == typeof(string))
                {
                    var v = prop.GetValue(obj)?.ToString();
                    if (!string.IsNullOrEmpty(v)) return v;
                }

                var field = type.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null && field.FieldType == typeof(string))
                {
                    var v = field.GetValue(obj)?.ToString();
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch { }
        }
        return null;
    }

    #endregion

    #region Score Manager Patches

    private static int PatchScoreManager()
    {
        int count = 0;
        var sm = _scoreManagerType;

        // UpdateHealthBar
        var updateHealth = sm.GetMethod("UpdateHealthBar", BindingFlags.Public | BindingFlags.Instance);
        if (updateHealth != null)
        {
            try
            {
                _harmony.Patch(updateHealth, postfix: new HarmonyMethod(typeof(RuntimePatches), nameof(UpdateHealthBar_Postfix)));
                MelonLogger.Msg("[RuntimePatches] ✓ Patched UpdateHealthBar");
                count++;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RuntimePatches] ✗ UpdateHealthBar patch failed: {ex.Message}");
            }
        }

        // OnHealthDepleated (note: typo in original game code)
        var onHealthDepleted = sm.GetMethod("OnHealthDepleated", BindingFlags.Public | BindingFlags.Instance);
        if (onHealthDepleted != null)
        {
            try
            {
                _harmony.Patch(onHealthDepleted, postfix: new HarmonyMethod(typeof(RuntimePatches), nameof(OnHealthDepleted_Postfix)));
                MelonLogger.Msg("[RuntimePatches] ✓ Patched OnHealthDepleated");
                count++;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RuntimePatches] ✗ OnHealthDepleated patch failed: {ex.Message}");
            }
        }

        // OnHealthBackgroundBlink
        var onHealthBlink = sm.GetMethod("OnHealthBackgroundBlink", BindingFlags.Public | BindingFlags.Instance);
        if (onHealthBlink != null)
        {
            try
            {
                _harmony.Patch(onHealthBlink, postfix: new HarmonyMethod(typeof(RuntimePatches), nameof(OnHealthWarning_Postfix)));
                MelonLogger.Msg("[RuntimePatches] ✓ Patched OnHealthBackgroundBlink");
                count++;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RuntimePatches] ✗ OnHealthBackgroundBlink patch failed: {ex.Message}");
            }
        }

        return count;
    }

    public static void UpdateHealthBar_Postfix(float __0)
    {
        try
        {
            // Health can fire every frame — skip entirely when nobody is listening.
            if (_server == null || !_server.HasClients) return;

            // The game passes health as a normalized 0..1 fraction (full = 1.0), and it can
            // momentarily underflow below 0 at death. Convert to 0..100 and clamp.
            float raw = __0;

            if (_debugLogging)
                MelonLogger.Msg($"[RuntimePatches][debug] UpdateHealthBar raw={raw:0.0000}");

            float healthPercent = raw <= 1.5f ? raw * 100f : raw;
            if (healthPercent < 0f) healthPercent = 0f;
            else if (healthPercent > 100f) healthPercent = 100f;

            // Only broadcast on a meaningful change (in percent space now)
            if (Math.Abs(healthPercent - _lastHealth) > 0.5f)
            {
                _lastHealth = healthPercent;
                Broadcast("HealthUpdate", new
                {
                    health = healthPercent,
                    healthPercent,
                    healthRaw = raw
                });
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[RuntimePatches] UpdateHealthBar error: {ex.Message}");
        }
    }

    public static void OnHealthDepleted_Postfix()
    {
        try
        {
            Broadcast("HealthDepleted", new
            {
                finalScore = _score,
                maxCombo = _maxCombo
            });
            _isInSong = false;
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[RuntimePatches] OnHealthDepleted error: {ex.Message}");
        }
    }

    public static void OnHealthWarning_Postfix()
    {
        try
        {
            Broadcast("HealthWarning", new { });
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[RuntimePatches] OnHealthWarning error: {ex.Message}");
        }
    }

    #endregion

    #region Progression Patches

    private static int PatchProgression()
    {
        int count = 0;

        // BadgeManager.HandleLevelUp
        if (_badgeManagerType != null)
        {
            var handleLevelUp = _badgeManagerType.GetMethod("HandleLevelUp", BindingFlags.Public | BindingFlags.Instance);
            if (handleLevelUp != null)
            {
                try
                {
                    _harmony.Patch(handleLevelUp, postfix: new HarmonyMethod(typeof(RuntimePatches), nameof(HandleLevelUp_Postfix)));
                    MelonLogger.Msg("[RuntimePatches] ✓ Patched HandleLevelUp");
                    count++;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[RuntimePatches] ✗ HandleLevelUp patch failed: {ex.Message}");
                }
            }
        }

        // ProgressionXPBar.HandleXPGained
        if (_xpBarType != null)
        {
            var handleXP = _xpBarType.GetMethod("HandleXPGained", BindingFlags.Public | BindingFlags.Instance);
            if (handleXP != null)
            {
                try
                {
                    _harmony.Patch(handleXP, postfix: new HarmonyMethod(typeof(RuntimePatches), nameof(HandleXPGained_Postfix)));
                    MelonLogger.Msg("[RuntimePatches] ✓ Patched HandleXPGained");
                    count++;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[RuntimePatches] ✗ HandleXPGained patch failed: {ex.Message}");
                }
            }
        }

        // SongSessionTracker.CompleteSongSession
        if (_songSessionTrackerType != null)
        {
            var completeSong = _songSessionTrackerType.GetMethod("CompleteSongSession", BindingFlags.Public | BindingFlags.Instance);
            if (completeSong != null)
            {
                try
                {
                    _harmony.Patch(completeSong, postfix: new HarmonyMethod(typeof(RuntimePatches), nameof(CompleteSongSession_Postfix)));
                    MelonLogger.Msg("[RuntimePatches] ✓ Patched CompleteSongSession");
                    count++;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[RuntimePatches] ✗ CompleteSongSession patch failed: {ex.Message}");
                }
            }
        }

        // BadgeManager.HandleSnapshotUpdated(PlayerDataSnapshot) — carries current level/XP.
        // This fires early (profile/menu load), so it populates progression before any level-up.
        if (_badgeManagerType != null)
        {
            var handleSnapshot = _badgeManagerType.GetMethod("HandleSnapshotUpdated", BindingFlags.Public | BindingFlags.Instance);
            if (handleSnapshot != null)
            {
                try
                {
                    _harmony.Patch(handleSnapshot, postfix: new HarmonyMethod(typeof(RuntimePatches), nameof(HandleSnapshotUpdated_Postfix)));
                    MelonLogger.Msg("[RuntimePatches] ✓ Patched HandleSnapshotUpdated");
                    count++;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[RuntimePatches] ✗ HandleSnapshotUpdated patch failed: {ex.Message}");
                }
            }
            else
            {
                MelonLogger.Warning("[RuntimePatches] HandleSnapshotUpdated not found on BadgeManager");
            }
        }

        // Game_InfoProvider.HandleProgressionInitialized(PlayerDataSnapshot) — fires AFTER the
        // remote API sync completes (the user observed level/XP are blank until this happens).
        // This is the authoritative source for current progression.
        if (_infoProviderType != null)
        {
            var handleProgInit = _infoProviderType.GetMethod("HandleProgressionInitialized", BindingFlags.Public | BindingFlags.Instance);
            if (handleProgInit != null)
            {
                try
                {
                    _harmony.Patch(handleProgInit, postfix: new HarmonyMethod(typeof(RuntimePatches), nameof(HandleSnapshotUpdated_Postfix)));
                    MelonLogger.Msg("[RuntimePatches] ✓ Patched HandleProgressionInitialized");
                    count++;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[RuntimePatches] ✗ HandleProgressionInitialized patch failed: {ex.Message}");
                }
            }
            else
            {
                MelonLogger.Warning("[RuntimePatches] HandleProgressionInitialized not found on Game_InfoProvider");
            }
        }

        return count;
    }

    // Candidate member names for snapshot fields (covers naming variations across branches)
    private static readonly string[] LevelNames = { "level", "PlayerLevel", "currentLevel", "playerLevel", "Level", "CurrentLevel", "playerLevelValue" };
    private static readonly string[] XPNames = { "currentLevelXP", "CurrentLevelXP", "currentXP", "xp", "currentExperience", "experience", "CurrentXP", "Xp", "currentXp", "experiencePoints" };
    private static readonly string[] TotalXPNames = { "totalXP", "TotalXP", "totalExperience", "lifetimeXP", "totalXp", "TotalXp", "totalExperiencePoints" };
    private static readonly string[] XpForNextNames = { "xpForNextLevel", "XPForNextLevel", "xpForNext", "nextLevelXP" };
    private static readonly string[] LevelProgressNames = { "LevelProgress", "CurrentLevelProgress", "levelProgress", "currentLevelProgress" };

    // Nested objects on PlayerDataSnapshot that hold the actual progression numbers.
    private static readonly string[] NestedProgressionMembers = { "profile", "careerStats", "Profile", "CareerStats" };

    public static void HandleSnapshotUpdated_Postfix(object __0)
    {
        try
        {
            var snapshot = __0;
            if (snapshot == null) return;

            var snapType = snapshot.GetType();

            // One-time dump of the snapshot + its nested progression objects so we can
            // confirm exact field names (level/XP live inside profile/careerStats, not top-level).
            if (_debugLogging && !_snapshotFieldsDumped)
            {
                _snapshotFieldsDumped = true;
                DumpMembers("PlayerDataSnapshot", snapshot, snapType);
                foreach (var nested in NestedProgressionMembers)
                {
                    var obj = ReadMemberObject(snapshot, snapType, nested);
                    if (obj != null) DumpMembers($"PlayerDataSnapshot.{nested}", obj, obj.GetType());
                }
            }

            // Search top-level first, then drill into nested profile/careerStats.
            int level = ReadProgressionInt(snapshot, snapType, LevelNames, _curLevel);
            int xp = ReadProgressionInt(snapshot, snapType, XPNames, _curXP);
            int totalXP = ReadProgressionInt(snapshot, snapType, TotalXPNames, _curTotalXP);
            int xpForNext = ReadProgressionInt(snapshot, snapType, XpForNextNames, _curXpForNext);
            float progress = ReadProgressionFloat(snapshot, snapType, LevelProgressNames, _curLevelProgress);

            // Only broadcast when something changed
            if (level != _curLevel || xp != _curXP || totalXP != _curTotalXP ||
                xpForNext != _curXpForNext || Math.Abs(progress - _curLevelProgress) > 0.0001f)
            {
                _curLevel = level;
                _curXP = xp;
                _curTotalXP = totalXP;
                _curXpForNext = xpForNext;
                _curLevelProgress = progress;
                if (level > 0) _lastLevel = level;
                BroadcastProgression();
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[RuntimePatches] HandleSnapshotUpdated error: {ex.Message}");
        }
    }

    /// <summary>Reads an int from the object directly, or from its nested profile/careerStats members.</summary>
    private static int ReadProgressionInt(object snapshot, Type snapType, string[] names, int fallback)
    {
        // Try top-level
        int top = ReadIntMember(snapshot, snapType, names, int.MinValue);
        if (top != int.MinValue) return top;

        // Drill into nested progression objects
        foreach (var nested in NestedProgressionMembers)
        {
            var obj = ReadMemberObject(snapshot, snapType, nested);
            if (obj == null) continue;
            int v = ReadIntMember(obj, obj.GetType(), names, int.MinValue);
            if (v != int.MinValue) return v;
        }
        return fallback;
    }

    /// <summary>Reads a float from the object directly, or from its nested profile/careerStats members.</summary>
    private static float ReadProgressionFloat(object snapshot, Type snapType, string[] names, float fallback)
    {
        float top = ReadFloatMember(snapshot, snapType, names, float.NaN);
        if (!float.IsNaN(top)) return top;

        foreach (var nested in NestedProgressionMembers)
        {
            var obj = ReadMemberObject(snapshot, snapType, nested);
            if (obj == null) continue;
            float v = ReadFloatMember(obj, obj.GetType(), names, float.NaN);
            if (!float.IsNaN(v)) return v;
        }
        return fallback;
    }

    private static float ReadFloatMember(object obj, Type type, string[] names, float fallback)
    {
        foreach (var n in names)
        {
            try
            {
                var prop = type.GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && (prop.PropertyType == typeof(float) || prop.PropertyType == typeof(double)))
                    return Convert.ToSingle(prop.GetValue(obj));

                var field = type.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null && (field.FieldType == typeof(float) || field.FieldType == typeof(double)))
                    return Convert.ToSingle(field.GetValue(obj));
            }
            catch { }
        }
        return fallback;
    }

    /// <summary>Reads a reference-typed member (property or field) by name.</summary>
    private static object ReadMemberObject(object obj, Type type, string name)
    {
        try
        {
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null) return prop.GetValue(obj);
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) return field.GetValue(obj);
        }
        catch { }
        return null;
    }

    private static void DumpMembers(string label, object obj, Type type)
    {
        MelonLogger.Msg($"[RuntimePatches][debug] {label} type: {type.FullName}");
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            MelonLogger.Msg($"[RuntimePatches][debug]   prop {p.PropertyType.Name} {p.Name}");
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            MelonLogger.Msg($"[RuntimePatches][debug]   field {f.FieldType.Name} {f.Name}");
    }

    /// <summary>Reads the first matching int-like property/field from candidate names.</summary>
    private static int ReadIntMember(object obj, Type type, string[] names, int fallback)
    {
        foreach (var n in names)
        {
            try
            {
                var prop = type.GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && IsIntLike(prop.PropertyType))
                    return Convert.ToInt32(prop.GetValue(obj));

                var field = type.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null && IsIntLike(field.FieldType))
                    return Convert.ToInt32(field.GetValue(obj));
            }
            catch { }
        }
        return fallback;
    }

    private static bool IsIntLike(Type t)
        => t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(short) || t == typeof(float) || t == typeof(double);

    /// <summary>Broadcasts the cached progression snapshot (level / XP / total XP).</summary>
    private static void BroadcastProgression()
    {
        if (_curLevel < 0 && _curXP < 0 && _curTotalXP < 0) return; // nothing known yet

        Broadcast("ProgressionUpdate", new
        {
            currentLevel = _curLevel < 0 ? 0 : _curLevel,
            currentXP = _curXP < 0 ? 0 : _curXP,
            totalXP = _curTotalXP < 0 ? 0 : _curTotalXP,
            xpForNextLevel = _curXpForNext < 0 ? 0 : _curXpForNext,
            levelProgress = _curLevelProgress < 0f ? 0f : _curLevelProgress
        });
    }

    /// <summary>
    /// Called by the server (via a flag) when a client connects, so the overlay's
    /// progression panel populates immediately instead of waiting for a level-up.
    /// </summary>
    public static void RequestProgressionSnapshot()
    {
        _progressionSnapshotRequested = true;
    }

    /// <summary>
    /// Main-thread pump (driven from Main.OnUpdate). Drains pending snapshot requests.
    /// IL2CPP member reads must happen on the Unity thread, so we never read from the
    /// socket thread directly.
    /// </summary>
    public static void Tick()
    {
        if (!_progressionSnapshotRequested) return;
        _progressionSnapshotRequested = false;

        TryReadProgressionSnapshot();

        // Even if we couldn't resolve current values, push whatever we have so the
        // overlay leaves its default "--" state.
        if (_curLevel < 0 && _curXP < 0 && _curTotalXP < 0)
        {
            Broadcast("ProgressionUpdate", new { currentLevel = 0, currentXP = 0, totalXP = 0 });
        }
        else
        {
            BroadcastProgression();
        }
    }

    /// <summary>
    /// Proactively reads the current progression from ProgressionManager.Instance by locating
    /// a PlayerDataSnapshot-like member and pulling level/XP from it. Updates the cached values.
    /// </summary>
    private static void TryReadProgressionSnapshot()
    {
        try
        {
            if (_progressionManagerType == null) return;

            object mgr = GetSingletonInstance(_progressionManagerType);
            if (mgr == null) return;

            var mgrType = mgr.GetType();

            // One-time dump so we can confirm the real member names if our guesses miss.
            if (_debugLogging && !_progressionManagerDumped)
            {
                _progressionManagerDumped = true;
                MelonLogger.Msg($"[RuntimePatches][debug] ProgressionManager type: {mgrType.FullName}");
                foreach (var p in mgrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    MelonLogger.Msg($"[RuntimePatches][debug]   prop {p.PropertyType.Name} {p.Name}");
                foreach (var f in mgrType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    MelonLogger.Msg($"[RuntimePatches][debug]   field {f.FieldType.Name} {f.Name}");
            }

            // Read directly from the manager's clean properties first
            // (PlayerLevel / TotalXP / CurrentLevelXP / XPForNextLevel / LevelProgress).
            int level = ReadProgressionInt(mgr, mgrType, LevelNames, int.MinValue);
            int xp = ReadProgressionInt(mgr, mgrType, XPNames, int.MinValue);
            int totalXP = ReadProgressionInt(mgr, mgrType, TotalXPNames, int.MinValue);
            int xpForNext = ReadProgressionInt(mgr, mgrType, XpForNextNames, int.MinValue);
            float progress = ReadProgressionFloat(mgr, mgrType, LevelProgressNames, float.NaN);

            // Fall back to the snapshot for anything the manager didn't expose directly.
            if (level == int.MinValue || xp == int.MinValue || totalXP == int.MinValue)
            {
                object snapshot = FindSnapshotMember(mgr, mgrType);
                if (snapshot != null)
                {
                    var snapType = snapshot.GetType();
                    if (level == int.MinValue) level = ReadProgressionInt(snapshot, snapType, LevelNames, int.MinValue);
                    if (xp == int.MinValue) xp = ReadProgressionInt(snapshot, snapType, XPNames, int.MinValue);
                    if (totalXP == int.MinValue) totalXP = ReadProgressionInt(snapshot, snapType, TotalXPNames, int.MinValue);
                    if (xpForNext == int.MinValue) xpForNext = ReadProgressionInt(snapshot, snapType, XpForNextNames, int.MinValue);
                    if (float.IsNaN(progress)) progress = ReadProgressionFloat(snapshot, snapType, LevelProgressNames, float.NaN);
                }
            }

            if (level != int.MinValue && level >= 0) _curLevel = level;
            if (xp != int.MinValue && xp >= 0) _curXP = xp;
            if (totalXP != int.MinValue && totalXP >= 0) _curTotalXP = totalXP;
            if (xpForNext != int.MinValue && xpForNext >= 0) _curXpForNext = xpForNext;
            if (!float.IsNaN(progress) && progress >= 0f) _curLevelProgress = progress;
            if (_curLevel > 0) _lastLevel = _curLevel;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[RuntimePatches] TryReadProgressionSnapshot error: {ex.Message}");
        }
    }

    /// <summary>Gets a singleton instance via common patterns (Instance/s_instance, property or field).</summary>
    private static object GetSingletonInstance(Type type)
    {
        string[] names = { "Instance", "s_instance", "_instance", "instance" };
        foreach (var n in names)
        {
            try
            {
                var prop = type.GetProperty(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (prop != null)
                {
                    var v = prop.GetValue(null);
                    if (v != null) return v;
                }

                var field = type.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (field != null)
                {
                    var v = field.GetValue(null);
                    if (v != null) return v;
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>Scans an object's members for a PlayerDataSnapshot-like instance.</summary>
    private static object FindSnapshotMember(object obj, Type type)
    {
        try
        {
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var tn = p.PropertyType.Name;
                if (tn.Contains("Snapshot") || tn.Contains("PlayerData"))
                {
                    try { var v = p.GetValue(obj); if (v != null) return v; } catch { }
                }
            }
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var tn = f.FieldType.Name;
                if (tn.Contains("Snapshot") || tn.Contains("PlayerData"))
                {
                    try { var v = f.GetValue(obj); if (v != null) return v; } catch { }
                }
            }
        }
        catch { }
        return null;
    }

    public static void HandleLevelUp_Postfix(int __0)
    {
        try
        {
            int newLevel = __0;
            if (newLevel != _lastLevel && newLevel > 0)
            {
                _lastLevel = newLevel;
                _curLevel = newLevel;
                Broadcast("LevelUp", new { newLevel });
                BroadcastProgression();
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[RuntimePatches] HandleLevelUp error: {ex.Message}");
        }
    }

    public static void HandleXPGained_Postfix(int __0)
    {
        try
        {
            int xpAmount = __0;
            if (xpAmount > 0)
            {
                Broadcast("XPGained", new { amount = xpAmount });
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[RuntimePatches] HandleXPGained error: {ex.Message}");
        }
    }

    // Args: score, perfect, normal, bad, fail, maxCombo, passed, accuracy, notesHit, totalNotes, mistakeCount, isFullCombo, isPerfect, isNoMiss, xpEarned
    public static void CompleteSongSession_Postfix(
        int __0, int __1, int __2, int __3, int __4, int __5,
        bool __6, float __7, int __8, int __9, int __10,
        bool __11, bool __12, bool __13, int __14)
    {
        try
        {
            Broadcast("SongSessionComplete", new
            {
                score = __0,
                perfect = __1,
                normal = __2,
                bad = __3,
                fail = __4,
                maxCombo = __5,
                passed = __6,
                accuracy = __7,
                notesHit = __8,
                totalNotes = __9,
                mistakeCount = __10,
                isFullCombo = __11,
                isPerfect = __12,
                isNoMiss = __13,
                xpEarned = __14
            });

            // The dedicated XP-bar hook (HandleXPGained) only fires on the results screen
            // animation, which doesn't always run. Emit XPGained here too so the overlay's
            // "Last XP Gain" updates reliably whenever a session awards XP.
            if (__14 > 0)
            {
                Broadcast("XPGained", new { amount = __14 });
            }

            // Refresh progression after the session so level/XP/bar reflect the new totals.
            TryReadProgressionSnapshot();
            BroadcastProgression();
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[RuntimePatches] CompleteSongSession error: {ex.Message}");
        }
    }

    #endregion

    #region Scene Loading

    private static int PatchSceneLoading()
    {
        int count = 0;

        try
        {
            // Use Unity's SceneManager event
            SceneManager.sceneLoaded += (Action<Scene, LoadSceneMode>)OnSceneLoaded;
            MelonLogger.Msg("[RuntimePatches] ✓ Registered SceneManager.sceneLoaded");
            count++;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[RuntimePatches] ✗ Scene handler failed: {ex.Message}");
        }

        return count;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        try
        {
            bool isGame = scene.name.Contains("Game") || scene.name.Contains("Stage") || scene.name.Contains("Play");

            Broadcast("SceneChanged", new
            {
                sceneName = scene.name,
                buildIndex = scene.buildIndex,
                isInGame = isGame
            });

            if (isGame)
            {
                ResetState();
                Broadcast("LevelLoaded", new { sceneName = scene.name });
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[RuntimePatches] OnSceneLoaded error: {ex.Message}");
        }
    }

    #endregion
}
