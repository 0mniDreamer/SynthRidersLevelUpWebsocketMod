# Synth Riders WebSocket Events

A MelonLoader mod for **Synth Riders** that broadcasts live gameplay
events over a local WebSocket, so external apps can react to play in real time. Comes with a
ready-to-use example overlay.

Useful for stream overlays, chat bots, smart-home/LED reactions, haptics, and stat tracking etc.

<!-- Add a screenshot of the overlay at docs/overlay.png and uncomment:
![Overlay screenshot](docs/overlay.png)
-->

## Compatibility

| | |
|---|---|
| Game | Synth Riders (Kluge Interactive) |
| Runtime | IL2CPP, .NET 6 |
| MelonLoader | 0.7.2 (Open-Beta) |
| Unity | 6000.3.13f1 (current)  |

A single build loads on both Unity branches — game members are resolved by reflection at runtime
rather than against branch-specific assemblies.

## Installation

1. Install MelonLoader on Synth Riders.
2. Download `SynthRidersWebsocketMod.dll` from the [Releases](../../releases) page (or build it — see below).
3. Copy the DLL into `SynthRiders/Mods/`.
4. Launch the game. On startup the console prints `[EventServer] Started on ws://localhost:9000`.

The overlay (`SynthRidersOverlay.html`) is standalone — open it in a browser or add it as an OBS
**Browser Source** (point it at the local file). It connects to the WebSocket automatically.

## Quick start

```javascript
const ws = new WebSocket('ws://localhost:9000');
ws.onmessage = (e) => {
  const { eventType, data, timestamp } = JSON.parse(e.data);
  console.log(eventType, data);
};
```

A plain HTTP GET to `http://localhost:9000` returns a JSON status payload (handy for testing that
the server is up).

## Message format

Every message is a JSON envelope:

```json
{
  "eventType": "NoteHit",
  "data": { "...": "event-specific fields" },
  "timestamp": "2026-06-21T21:43:51.920Z"
}
```

`timestamp` is ISO 8601 (UTC) and lives on the envelope only.

On connect, the server immediately sends a `Connected` event whose `data.availableEvents` lists
every event the build can emit. `ProgressionUpdate` is *sticky*: a client that connects mid-song
receives the most recent one right away, so the progression panel is never blank.

## Events

### Connection

| Event | Data |
|-------|------|
| `Connected` | `message`, `version`, `availableEvents[]` |
| `SceneChanged` | `sceneName`, `buildIndex`, `isInGame` |

### Gameplay

| Event | Data |
|-------|------|
| `NoteHit` | `points`, `combo`, `totalScore`, `isCombo`, `isLine`, `noteType`, `hand` |
| `ComboBreak` | `lostCombo`, `reason`, `isNote`, `wasNotHandsClose`, `eventID` |
| `ComboMilestone` | `combo` |
| `WallHit` | `wallType` |
| `RailCleared` | _(none)_ |
| `HealthUpdate` | `health`, `healthPercent`, `healthRaw` |
| `HealthWarning` | _(none)_ |
| `HealthDepleted` | `finalScore`, `maxCombo` |

### Song flow

| Event | Data |
|-------|------|
| `SongStart` | `song`, `author` _(provisional; may be empty until `SongInfo`)_ |
| `SongInfo` | `song`, `author` |
| `SongEnd` | `song`, `score`, `maxCombo` |
| `LevelLoaded` | `sceneName` |

### Progression

| Event | Data |
|-------|------|
| `LevelUp` | `newLevel` |
| `XPGained` | `amount` |
| `ProgressionUpdate` | `currentLevel`, `currentXP`, `totalXP`, `xpForNextLevel`, `levelProgress` |
| `SongSessionComplete` | `score`, `perfect`, `normal`, `bad`, `fail`, `maxCombo`, `passed`, `accuracy`, `notesHit`, `totalNotes`, `mistakeCount`, `isFullCombo`, `isPerfect`, `isNoMiss`, `xpEarned` |

> **Timing notes.** Gameplay events fire only during songs. `SongStart` fires as the scene
> begins (song/author may still be empty), and `SongInfo` follows once the game has populated the
> metadata — treat `SongInfo` as the authoritative title/author. Progression values arrive after
> the game finishes its remote sync; `levelProgress` is a 0–1 fraction suitable for an XP bar.

### Example payloads

`ProgressionUpdate`:

```json
{
  "eventType": "ProgressionUpdate",
  "data": {
    "currentLevel": 82,
    "currentXP": 1840,
    "totalXP": 220485,
    "xpForNextLevel": 4200,
    "levelProgress": 0.438
  },
  "timestamp": "2026-06-21T21:43:52.001Z"
}
```

`SongSessionComplete`:

```json
{
  "eventType": "SongSessionComplete",
  "data": {
    "score": 1250000, "perfect": 450, "normal": 30, "bad": 5, "fail": 2,
    "maxCombo": 487, "passed": true, "accuracy": 97.5,
    "notesHit": 485, "totalNotes": 487, "mistakeCount": 7,
    "isFullCombo": false, "isPerfect": false, "isNoMiss": false,
    "xpEarned": 350
  },
  "timestamp": "2026-06-21T21:43:52.001Z"
}
```

## Configuration

Settings live under `[SynthRidersWebsocketMod]` in `UserData/MelonPreferences.cfg` (created on
first run):

| Key | Default | Purpose |
|-----|---------|---------|
| `Host` | `localhost` | Bind address. Use `0.0.0.0` to accept connections from other devices. |
| `Port` | `9000` | WebSocket / HTTP-status port. |
| `LogAllEvents` | `false` | Log every event to the console (very verbose). |
| `LogMajorEventsOnly` | `true` | Log only major events (SongStart/End, LevelUp, etc.). |
| `DebugLogging` | `false` | Dump raw health values and game-object members for diagnosing data issues. Leave off for normal use. |

## Building

1. Set your install path in `Directory.Build.props`:

   ```xml
   <SynthRidersPath>C:\Program Files (x86)\Steam\steamapps\common\SynthRiders</SynthRidersPath>
   ```

2. Run the game once with MelonLoader installed so the IL2CPP assemblies are generated under
   `MelonLoader/Il2CppAssemblies`.

3. Build:

   ```sh
   dotnet build -c Release
   ```

The output DLL auto-copies into the game's `Mods/` folder. References point at MelonLoader's
bundled DLLs (e.g. `0Harmony.dll`) directly rather than NuGet packages, to avoid Mono.Cecil
version conflicts.

## Troubleshooting

**Can't connect.** Confirm the console shows the server started, open `http://localhost:9000` in a
browser (it should return JSON), and check that nothing else is using port 9000 / your firewall
isn't blocking it.

**Song shows as "Unknown".** Make sure you're on a build that emits `SongInfo`; the overlay uses
that event for the title/author. Enabling `DebugLogging` will print whether `Game_InfoProvider`
resolved.

**Progression is blank at first.** It populates after the game's remote sync completes — that's
expected on a fresh launch.

**"Type not found" warnings.** A game update may have renamed members. Enable `DebugLogging`,
reproduce, and the dumps will show the current member names.

## Acknowledgements

Built on [MelonLoader](https://melonwiki.xyz/) and [HarmonyX](https://github.com/BepInEx/HarmonyX).
Not affiliated with or endorsed by Kluge Interactive.

## License

[MIT](LICENSE)
