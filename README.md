# BTD6 Replay Recorder

A high-fidelity, deterministic gameplay recorder and spectator viewer mod for **Bloons TD 6**, built with MelonLoader and BTD6 Mod Helper.

---

## Highlights

- **Lossless Action Recording:** Intercepts native simulation actions (placement, upgrades, targeting, hero abilities) and captures matching match configurations, RNG seeds, and metadata.
- **Reload Round (RLR / Home & Continue) Support:** Fully tracks and reproduces save-and-quit events. Mid-round resets are captured, and spectator replays reload map save state at round boundaries to reconstruct towers and weapon cooldowns without desync.
- **Native Spectator Playback:** Seamlessly drives BTD6's internal tournament verification engine (`UnityToSimulation.LoadReplay`). Replays run deterministically from tick 0.
- **Interactive Spectator HUD:** Floating in-game controls featuring playback pause/resume, multi-speed fast forward (1x, 2x, 5x, 10x), deterministic tick stepping (1 frame, 15 frames / 0.25s), and match restart.
- **In-Game Replay Browser:** Clean modal browser accessible via hotkey (`F8`) or directly from the Map Select screen to inspect, launch, and manage recorded `.btd6replay` files.
- **RNG Divergence Guard:** Actively tracks simulation consistency markers against recorded state and alerts the user if desync occurs without crashing the match.

---

## Installation

### Prerequisites
1. **Bloons TD 6** (Steam PC version)
2. **MelonLoader** (v0.6.1+ recommended for .NET 6)
3. **BTD6 Mod Helper**

### Quick Install
1. Download the latest `BTD6ReplayRecorder.dll` from the [Releases](https://github.com/Smikkel997/BTD6ReplayRecorder/releases) page.
2. Place `BTD6ReplayRecorder.dll` into your Bloons TD 6 `Mods/` folder:
   ```
   Steam/steamapps/common/BloonsTD6/Mods/
   ```
3. Start the game.

---

## How to Use

### Recording Matches
- When you begin a standard single-player game, the recorder prompts you to record the run.
- Play normally. Placements, targeting changes, upgrades, and abilities are captured tick-by-tick.
- If you exit to menu and continue later (RLR), the recorder automatically checkpoints your state and stitches the continuation into the recording.
- To export your replay, click the **TV Replay button** on the in-game Pause screen or Victory screen. Replays are saved to:
  ```
  BloonsTD6/Replays/replay_<Map>_<Difficulty>_<Mode>_R<Round>_<Timestamp>.btd6replay
  ```

### Watching Replays
- Open the Replay Viewer by pressing **`F8`** anywhere in the menus, or by clicking the **Replays** button on the Map Selection screen.
- Select any recorded replay to inspect its metadata, placed towers, and round count.
- Click **Play Full Run** to watch from start to finish, or **Play to Round X** to stop automatically at a specific milestone.

---

## Architecture & Technical Details

BTD6ReplayRecorder hooks directly into Ninja Kiwi's simulation bridge (`Il2CppAssets.Scripts.Unity.Bridge.UnityToSimulation`):

1. **State Isolation (`SaveSuppressionPatch.cs`):**
   Spectator playback suppresses profile saving and map save creation so watching replays never overwrites your live save slots or game progress.
2. **Deterministic Checkpoint Continuation (`ReplayPlaybackManager.cs`):**
   When playing an RLR session, round boundaries fire `ExecuteContinueFromCheckpoint`, dynamically deserializing the companion JSON save state and rebuilding towers in-place with frame-1 weapon ready states.
3. **Simulation Clock Decoupling (`TimeManager`):**
   Spectator speed controls modulate `TimeManager.replayTimeScaleMultiplier`, allowing 10x playback speed independent of rendering frames.

---

## Building from Source

BTD6ReplayRecorder targets `.NET 6.0` (x64) using the C# 10 compiler.

```bash
# Clone the repository
git clone https://github.com/Smikkel997/BTD6ReplayRecorder.git
cd BTD6ReplayRecorder

# Build Release binary
dotnet build BTD6ReplayRecorder.csproj -c Release
```

---

## License

This project is licensed under the [MIT License](LICENSE).
