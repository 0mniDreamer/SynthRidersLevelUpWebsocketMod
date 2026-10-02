# Changelog

All notable changes to this project are documented here.

## [2.5.0]

### Fixed
- **Score now tracks the game live.** The mod summed each note's base points, which falls
  behind the real total once the combo multiplier rises above 1x — so the score read low
  mid-song and only corrected at the end (when `SongSessionComplete` carries the final total).
  It now reads `Game_ScoreManager.currentScore` (multiplier already applied) each note, with a
  candidate-name fallback and graceful fallback to accumulation if the instance isn't ready.
- **WebSocket connections now work when the game runs under Proton/Wine (Linux).** The server
  previously used `HttpListener.AcceptWebSocketAsync`, which initialises the native Windows
  `websocket.dll` (`WebSocketProtocolComponent`). That component does not exist under Wine, so
  every upgrade there failed with *"The type initializer for
  'System.Net.WebSockets.WebSocketProtocolComponent' threw an exception"* while the HTTP status
  page still worked. The server now accepts raw TCP (`TcpListener`) and performs the RFC 6455
  handshake and frame codec itself — no native dependency, identical behaviour on Windows.

### Changed
- `Host` values are resolved explicitly: `localhost` binds loopback only, `0.0.0.0` (or `*`/`+`)
  binds all interfaces for LAN access, and any other value is parsed as a literal IP.

## [2.4.0]

### Fixed
- **Song title/author now populate reliably.** The singleton lookup for `Game_InfoProvider`
  used a field-only `s_instance` resolution that returned null on the Unity 6 branch, so song
  metadata never read. It now resolves the instance via property *or* field
  (`Instance` / `s_instance` / `_instance` / `instance`).
- **Current XP now displays.** The current-level XP field is `currentLevelXP`; the candidate
  list previously only checked `currentXP`, so the value read as 0.
- **"Last XP Gain" updates after every song.** The dedicated XP-bar hook only fires on the
  results-screen animation, which does not always run. The session-complete handler now also
  emits `XPGained` when a session awards XP.

### Added
- Progression payloads now include `xpForNextLevel` and `levelProgress` (0–1), and the bundled
  overlay uses them to render an accurate XP bar instead of a fixed-1000-XP estimate.
- Progression is read directly from `ProgressionManager`'s public properties
  (`PlayerLevel`, `TotalXP`, `CurrentLevelXP`, `XPForNextLevel`, `LevelProgress`), falling back
  to the snapshot for anything not exposed directly.

### Changed
- Song metadata is read at `OnLevelFinishedLoading` (after the game populates it) rather than at
  `Awake`, with a provisional `SongStart` fired at `Awake` to reset the overlay.
- The advertised event list was trimmed to the events that are actually emitted (see README),
  so clients no longer see events that never fire.

## [2.3.0]

### Fixed
- Hooked `GameControlManager.OnLevelFinishedLoading` for reliable song info timing.
- Hooked `Game_InfoProvider.HandleProgressionInitialized`, which fires after the remote API
  sync completes, so progression no longer reads an empty pre-sync snapshot.
- Snapshot reader drills into the nested `profile` / `careerStats` objects where level and XP
  actually live.

## [2.2.0]

### Fixed
- Health normalization and clamping for the overlay.
- Broadened song and progression field-name detection.

### Added
- `DebugLogging` config toggle that dumps raw values and game-object members for diagnosis.
- Sticky-event replay so a client that connects mid-song immediately receives the latest
  progression state.

## [2.1.0]

### Added
- Background send queue (bounded channel) so all JSON serialization and socket I/O happen off
  the game thread, keeping the VR frame loop stutter-free.

### Fixed
- Removed a latent overlapping-send bug on the shared socket.

## [2.0.0]

- Initial standalone release: WebSocket server exposing Synth Riders game events, with a
  connection race-condition fix and IL2CPP nullable-context handling.
