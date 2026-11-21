using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using MelonLoader;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppAssets.Scripts;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.Network;
using Il2CppAssets.Scripts.Unity.Menu;
using Il2CppAssets.Scripts.Simulation;
using Il2CppAssets.Scripts.Models.Profile;

namespace BTD6ReplayRecorder
{
    /// <summary>
    /// Metadata descriptor for recorded .btd6replay files displayed in the in-game viewer.
    /// </summary>
    public class ReplayFileInfo
    {
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string Map { get; set; } = "Unknown";
        public string Difficulty { get; set; } = "Unknown";
        public string Mode { get; set; } = "Unknown";
        public string Hero { get; set; } = "None";
        public int ActionCount { get; set; }
        public int TargetRound { get; set; } = 0;
        public int Seed { get; set; }
        public DateTime Timestamp { get; set; }
        public long FileSizeBytes { get; set; }
    }

    /// <summary>
    /// Manages deterministic playback of recorded replays via BTD6 native spectator pipelines.
    /// </summary>
    public static class ReplayPlaybackManager
    {
        public static bool IsSpectating { get; private set; } = false;
        public static SinglePlayerReplay? OriginalReplay { get; private set; } = null;
        public static SinglePlayerReplay? ActiveReplay { get; private set; } = null;
        public static SinglePlayerReplay? PendingReplay { get; set; } = null;
        public static int PendingTargetRound { get; set; } = 0;
        public static bool IsRestartingPlayback { get; private set; } = false;

        public static bool IsCreatingCheckpointSave { get; set; } = false;
        public static bool IsReloadingForRLR { get; set; } = false;
        public static MapSaveDataModel? PendingReloadSave { get; set; } = null;
        public static int ActiveReplayActionOffset { get; set; } = 0;
        public static int PendingReplayActionOffset { get; set; } = 0;

        public static List<RLRCheckpoint> ActiveCheckpoints { get; set; } = new();
        public static HashSet<int> ExecutedCheckpointRounds { get; } = new();
        public static int CurrentCheckpointIndex { get; set; } = 0;
        public static string LastCheckpointBanner { get; set; } = "";
        public static float LastCheckpointBannerTime { get; set; } = 0f;

        public static bool HasDiverged { get; set; } = false;
        public static int DivergenceStep { get; set; } = 0;
        public static string DivergenceReason { get; set; } = "";
        public static bool HasShownDivergencePopup { get; set; } = false;

        public static void OnReplayDiverged(int step, string reason)
        {
            HasDiverged = true;
            DivergenceStep = step;
            DivergenceReason = reason;

            // ONLY pause and show popup on the VERY FIRST divergence warning
            if (!HasShownDivergencePopup)
            {
                HasShownDivergencePopup = true;

                // Pause simulation for the popup
                IsPaused = true;
                TimeManager.gamePaused = true;

                try
                {
                    var popup = Il2CppAssets.Scripts.Unity.UI_New.Popups.PopupScreen.instance;
                    if (popup != null)
                    {
                        string body = $"<b><color=#E74C3C>Replay Divergence Detected!</color></b>\n\nAt step {step}, the simulation state diverged from the recording:\n<i>{reason}</i>\n\nPress OK to resume playback. Note: Towers or bloon behavior may no longer match the recorded match.";
                        Action onOk = () =>
                        {
                            TimeManager.gamePaused = false;
                            IsPaused = false;
                            MelonLogger.Msg("[REPLAY VIEWER] Divergence popup dismissed by user. Playback resumed.");
                        };
                        popup.ShowOkPopup(body, onOk);
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[REPLAY VIEWER] Could not show divergence popup: {ex.Message}");
                }
            }
        }

        public static void ApplySpectatorLocks(InGame? inGame)
        {
            if (inGame == null) return;
            try
            {
                inGame.lockTowerPurchases = true;
                inGame.lockHeroPurchases = true;
                inGame.lockTowerUpgrades = true;
                inGame.lockPowerActivations = true;
                inGame.lockAbilities = true;
                if (inGame.hotkeys != null)
                {
                    inGame.hotkeys.suppressHotkeys = 1;
                }
            }
            catch { }
        }

        public static float SpectatorTimeScale
        {
            get => (float)TimeManager.replayTimeScaleMultiplier;
            set => TimeManager.replayTimeScaleMultiplier = value;
        }

        public static void StepSimulation(int frames)
        {
            var inGame = InGame.instance;
            var bridge = inGame?.bridge;
            if (inGame == null || bridge == null) return;

            // Step simulation ONLY from a paused state and keep it paused
            IsPaused = true;
            TimeManager.gamePaused = true;

            for (int i = 0; i < frames; i++)
            {
                TimeManager.simulatedFrames++;
                bridge.Simulate();
            }

            try
            {
                bridge.Update();
            }
            catch { }

            // Ensure we remain paused
            IsPaused = true;
            TimeManager.gamePaused = true;
        }

        public static bool IsPaused
        {
            get => TimeManager.gamePaused;
            set => TimeManager.gamePaused = value;
        }
        public static int TargetEndRound { get; set; } = 0;
        public static bool IsReplayFinished { get; set; } = false;

        public static int ResolveFileTargetRound(string filepath)
        {
            try
            {
                // 1. Try reading companion JSON sidecar
                string jsonPath = Path.ChangeExtension(filepath, ".json");
                if (File.Exists(jsonPath))
                {
                    string json = File.ReadAllText(jsonPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("dumpRound", out var rProp) && rProp.TryGetInt32(out int rd))
                    {
                        return rd;
                    }
                    if (doc.RootElement.TryGetProperty("targetRound", out var tProp) && tProp.TryGetInt32(out int trd))
                    {
                        return trd;
                    }
                }

                // 2. Try parsing filename regex: _R(\d+)
                var match = Regex.Match(Path.GetFileName(filepath), @"_R(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int roundFromFilename))
                {
                    return roundFromFilename;
                }
            }
            catch { }

            return 0; // 0 = Full run / unrestricted
        }

        public static List<ReplayFileInfo> GetAvailableReplays()
        {
            var list = new List<ReplayFileInfo>();
            try
            {
                if (!Directory.Exists(ReplayManager.ReplayDirectory))
                    return list;

                var files = Directory.GetFiles(ReplayManager.ReplayDirectory, "*.btd6replay")
                    .OrderByDescending(f => File.GetLastWriteTime(f));

                foreach (var file in files)
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        string serialized = File.ReadAllText(file);
                        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(serialized);
                        Il2CppStructArray<byte> il2cppBytes = bytes;

                        var sp = Replay.DeserializeSP(il2cppBytes);
                        if (sp != null)
                        {
                            int targetRound = ResolveFileTargetRound(file);

                            list.Add(new ReplayFileInfo
                            {
                                FileName = fi.Name,
                                FullPath = file,
                                Map = sp.map ?? "Unknown",
                                Difficulty = sp.difficulty ?? "Unknown",
                                Mode = sp.mode ?? "Unknown",
                                Hero = sp.hero ?? "None",
                                ActionCount = sp.actions?.Count ?? 0,
                                TargetRound = targetRound,
                                Seed = sp.randomSeed,
                                Timestamp = fi.LastWriteTime,
                                FileSizeBytes = fi.Length
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"[REPLAY VIEWER] Could not parse '{Path.GetFileName(file)}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[REPLAY VIEWER] Failed to list replays: {ex.Message}");
            }
            return list;
        }

        public static bool LaunchReplayFile(string filepath)
        {
            try
            {
                if (!File.Exists(filepath))
                {
                    MelonLogger.Error($"[REPLAY VIEWER] Replay file does not exist: {filepath}");
                    return false;
                }

                int targetRound = ResolveFileTargetRound(filepath);

                // Load companion JSON sidecar and populate ActiveCheckpoints
                ActiveCheckpoints.Clear();
                ExecutedCheckpointRounds.Clear();
                CurrentCheckpointIndex = 0;
                ActiveReplayActionOffset = 0;
                PendingReplayActionOffset = 0;
                IsReloadingForRLR = false;
                IsCreatingCheckpointSave = false;
                PendingReloadSave = null;

                string jsonPath = Path.ChangeExtension(filepath, ".json");
                if (File.Exists(jsonPath))
                {
                    try
                    {
                        string json = File.ReadAllText(jsonPath);
                        var meta = JsonSerializer.Deserialize<ReplayMetadata>(json);
                        if (meta?.rlrCheckpoints != null && meta.rlrCheckpoints.Count > 0)
                        {
                            ActiveCheckpoints.AddRange(meta.rlrCheckpoints);
                            MelonLogger.Msg($"[REPLAY VIEWER] Loaded {ActiveCheckpoints.Count} RLR checkpoint(s) from companion JSON sidecar.");
                        }
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"[REPLAY VIEWER] Failed to parse companion JSON sidecar for checkpoints: {ex.Message}");
                    }
                }

                string serialized = File.ReadAllText(filepath);
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(serialized);
                Il2CppStructArray<byte> il2cppBytes = bytes;

                var replay = Replay.DeserializeSP(il2cppBytes);
                if (replay == null)
                {
                    MelonLogger.Error("[REPLAY VIEWER] Failed to deserialize replay payload.");
                    return false;
                }

                return StartPlayback(replay, targetRound);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[REPLAY VIEWER] Error launching replay: {ex.Message}");
                return false;
            }
        }

        public static bool StartPlayback(SinglePlayerReplay replay, int targetRound = 0)
        {
            var inGame = InGame.instance;
            var bridge = inGame?.bridge;

            OriginalReplay = replay;
            ActiveReplay = replay;
            TargetEndRound = targetRound;

            if (inGame != null && bridge != null)
            {
                try
                {
                    // Reseed simulation RNG and reset ObjectId so the simulation matches the recording perfectly!
                    var sim = bridge.simulation;
                    if (sim != null)
                    {
                        sim.syncedRandom = new Il2CppNinjaKiwi.Common.BufferedRandom(replay.randomSeed);
                        sim.unsyncedRandom = new Il2CppNinjaKiwi.Common.BufferedRandom(replay.randomSeed);
                        sim.syncedObjectId = new ObjectId { data = 0 };
                        if (sim.model != null)
                        {
                            sim.model.randomSeed = replay.randomSeed;
                        }
                        MelonLogger.Msg($"[REPLAY VIEWER] Reseeded simulation to seed {replay.randomSeed}, ObjectId reset to 0.");
                    }

                    // Apply spectator locks immediately to disable and grey out tower purchases
                    ApplySpectatorLocks(inGame);

                    // If ActiveCheckpoints is non-empty, feed only actions up to the first checkpoint
                    if (bridge.actions != null)
                    {
                        bridge.actions.Clear();
                    }

                    if (ActiveCheckpoints != null && ActiveCheckpoints.Count > 0)
                    {
                        int firstEnd = Math.Min(ActiveCheckpoints[0].ActionStartIndex, replay.actions?.Count ?? 0);
                        MelonLogger.Msg($"[REPLAY VIEWER] In-match active (segmented): Feeding initial {firstEnd} actions (up to Checkpoint #1 @ Round {ActiveCheckpoints[0].Round})...");
                        if (replay.actions != null)
                        {
                            for (int i = 0; i < firstEnd; i++)
                            {
                                bridge.AddAction(replay.actions[i]);
                            }
                        }
                    }
                    else
                    {
                        MelonLogger.Msg($"[REPLAY VIEWER] In-match active: Injecting {replay.actions?.Count ?? 0} actions into bridge...");
                        bridge.LoadReplay(replay.Cast<Replay>());
                    }

                    IsSpectating = true;
                    IsPaused = false;
                    IsReplayFinished = false;
                    HasDiverged = false;
                    DivergenceStep = 0;
                    DivergenceReason = "";
                    HasShownDivergencePopup = false;
                    SpectatorTimeScale = 1.0f;
                    TimeManager.gamePaused = false;

                    string targetDesc = TargetEndRound > 0 ? $"Target round: {TargetEndRound}" : "Playing full run (unrestricted)";
                    MelonLogger.Msg($"[REPLAY VIEWER] Spectator mode started! {targetDesc}");
                    return true;
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"[REPLAY VIEWER] bridge.LoadReplay failed: {ex.Message}");
                    return false;
                }
            }
            else
            {
                // Stage replay and launch match
                try
                {
                    PendingReplay = replay;
                    PendingTargetRound = targetRound;
                    var editable = InGameData.Editable;
                    if (editable != null)
                    {
                        editable.selectedMap = replay.map;
                        editable.selectedDifficulty = replay.difficulty;
                        editable.selectedMode = replay.mode;
                    }

                    MelonLogger.Msg($"[REPLAY VIEWER] Replay staged for {replay.map} ({replay.difficulty} - {replay.mode}). Launching match in native Spectator Mode...");
                    var ui = Il2CppAssets.Scripts.Unity.UI_New.UI.instance;
                    if (ui != null)
                    {
                        // When ActiveCheckpoints is non-empty, pass null replay so native doesn't preload all actions; CheckPendingPlaybackOnMatchStart injects the segment.
                        var initialReplay = (ActiveCheckpoints != null && ActiveCheckpoints.Count > 0) ? null : replay.Cast<Replay>();
                        ui.LoadGame(null, initialReplay, null, false, true);
                    }
                    else
                    {
                        MenuManager.instance.OpenMenu("InGame", null, false);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"[REPLAY VIEWER] Failed to stage and launch replay: {ex.Message}");
                    return false;
                }
            }
        }

        public static void CheckPendingPlaybackOnMatchStart(InGame inGame, UnityToSimulation bridge, bool isRlrContinuation = false)
        {
            if (PendingReplay != null)
            {
                MelonLogger.Msg($"[REPLAY VIEWER] Match started with PendingReplay! Finalizing spectator setup (RLR={isRlrContinuation})...");
                var replay = PendingReplay;
                int targetRound = PendingTargetRound;
                ActiveReplayActionOffset = PendingReplayActionOffset;
                PendingReplay = null;
                PendingTargetRound = 0;
                PendingReplayActionOffset = 0;

                ActiveReplay = replay;
                if (OriginalReplay == null)
                {
                    OriginalReplay = replay;
                }
                TargetEndRound = targetRound;
                IsSpectating = true;
                IsPaused = false;
                IsReplayFinished = false;
                HasDiverged = false;
                DivergenceStep = 0;
                DivergenceReason = "";
                HasShownDivergencePopup = false;
                SpectatorTimeScale = 1.0f;
                TimeManager.gamePaused = false;

                ApplySpectatorLocks(inGame);

                // Feed actions for the current segment
                if (bridge.actions != null)
                {
                    bridge.actions.Clear();
                }

                int countToFeed = replay.actions != null ? replay.actions.Count : 0;
                if (ActiveCheckpoints != null && CurrentCheckpointIndex < ActiveCheckpoints.Count)
                {
                    int nextCheckpointStart = ActiveCheckpoints[CurrentCheckpointIndex].ActionStartIndex;
                    int currentOffset = ActiveReplayActionOffset;
                    int segmentCount = Math.Max(0, nextCheckpointStart - currentOffset);
                    countToFeed = Math.Min(segmentCount, countToFeed);
                    MelonLogger.Msg($"[REPLAY VIEWER] Segmented playback on match start: Feeding {countToFeed} actions (up to Checkpoint #{CurrentCheckpointIndex + 1} @ Round {ActiveCheckpoints[CurrentCheckpointIndex].Round})...");
                }
                else
                {
                    MelonLogger.Msg($"[REPLAY VIEWER] Feeding all {countToFeed} remaining replay actions into bridge...");
                }

                if (replay.actions != null)
                {
                    for (int i = 0; i < countToFeed; i++)
                    {
                        bridge.AddAction(replay.actions[i]);
                    }
                }

                if (!isRlrContinuation)
                {
                    // Reseed simulation RNG and ObjectId for fresh match from tick 0
                    var sim = bridge.simulation;
                    if (sim != null)
                    {
                        sim.syncedRandom = new Il2CppNinjaKiwi.Common.BufferedRandom(replay.randomSeed);
                        sim.unsyncedRandom = new Il2CppNinjaKiwi.Common.BufferedRandom(replay.randomSeed);
                        sim.syncedObjectId = new ObjectId { data = 0 };
                        if (sim.model != null)
                        {
                            sim.model.randomSeed = replay.randomSeed;
                        }
                        MelonLogger.Msg($"[REPLAY VIEWER] Simulation initialized cleanly for replay: Seed={replay.randomSeed}, ObjectId=0");
                    }
                }
                else
                {
                    MelonLogger.Msg("[REPLAY VIEWER] RLR continuation match started: Preserved loaded MapSave simulation RNG and ObjectId.");
                }

                string targetDesc = TargetEndRound > 0 ? $"Target round: {TargetEndRound}" : "Playing full run (unrestricted)";
                MelonLogger.Msg($"[REPLAY VIEWER] Native Spectator mode active! {targetDesc}");
            }
        }

        public static void RestartPlayback(InGame? inGame = null)
        {
            // Reset checkpoint pointers without clearing ActiveCheckpoints list so checkpoints persist on restart!
            CurrentCheckpointIndex = 0;
            ExecutedCheckpointRounds.Clear();
            ActiveReplayActionOffset = 0;
            PendingReplayActionOffset = 0;
            IsCreatingCheckpointSave = false;
            IsReloadingForRLR = false;
            PendingReloadSave = null;
            LastCheckpointBanner = "";
            LastCheckpointBannerTime = 0f;

            var replay = OriginalReplay ?? ActiveReplay;
            if (replay == null)
            {
                MelonLogger.Warning("[REPLAY VIEWER] RestartPlayback called but ActiveReplay is null.");
                return;
            }
            ActiveReplay = replay;

            if (inGame == null)
            {
                inGame = InGame.instance;
            }
            var bridge = inGame?.bridge;

            if (inGame == null || bridge == null)
            {
                MelonLogger.Msg("[REPLAY VIEWER] InGame or Bridge is null. Falling back to StartPlayback...");
                StartPlayback(replay, TargetEndRound);
                return;
            }

            int targetRound = TargetEndRound;
            MelonLogger.Msg($"[REPLAY VIEWER] Cleanly restarting replay via native bridge.Restart(): {replay.map} ({replay.difficulty} - {replay.mode}) from tick 0...");

            IsRestartingPlayback = true;
            try
            {
                // 1. Temporarily unset spectator mode on bridge so ClearSimulation will:
                //    - clear bridge.actions
                //    - call Factory.DestroyAllActive (destroys ALL placed towers, projectiles, bloons)
                //    - recreate simulation cleanly
                //    - allow InGame.Restart() to run its DynamicUiObject & InputManager.OnRestart() resets
                bridge._IsSpectatorMode_k__BackingField = false;

                // 2. Unpause the game before restart so TimeManager doesn't get stuck in paused state
                TimeManager.gamePaused = false;
                IsPaused = false;

                // 3. Purge map saves so restart doesn't write or load any residual save
                try
                {
                    inGame.RemoveCurrentMapSave(true);
                    string mapId = inGame.MapDataSaveId ?? InGameData.Editable?.selectedMap ?? "";
                    if (!string.IsNullOrEmpty(mapId))
                    {
                        Game.Player?.Data?.RemoveSavedMap(mapId);
                    }
                }
                catch { }

                // 4. Native restart execution!
                // ClearSimulation -> Factory.DestroyAllActive (all towers gone) -> Initialise -> InGame.Restart -> MatchReady -> CloseAllMenus
                bridge.Restart();

                // 5. Cleanly clear and reload replay actions into bridge
                if (bridge.actions != null)
                {
                    bridge.actions.Clear();
                }

                if (ActiveCheckpoints != null && ActiveCheckpoints.Count > 0)
                {
                    int firstEnd = Math.Min(ActiveCheckpoints[0].ActionStartIndex, replay.actions?.Count ?? 0);
                    MelonLogger.Msg($"[REPLAY VIEWER] RestartPlayback (segmented): Feeding initial {firstEnd} actions (up to Checkpoint #1 @ Round {ActiveCheckpoints[0].Round})...");
                    if (replay.actions != null)
                    {
                        for (int i = 0; i < firstEnd; i++)
                        {
                            bridge.AddAction(replay.actions[i]);
                        }
                    }
                }
                else
                {
                    MelonLogger.Msg($"[REPLAY VIEWER] RestartPlayback: Injecting {replay.actions?.Count ?? 0} actions into bridge...");
                    bridge.LoadReplay(replay.Cast<Replay>());
                }

                // 6. Reseed simulation RNG and ObjectId to match replay
                var sim = bridge.simulation;
                if (sim != null)
                {
                    sim.blockAutoPlay = false;
                    sim.autoPlay = true;
                    sim.syncedRandom = new Il2CppNinjaKiwi.Common.BufferedRandom(replay.randomSeed);
                    sim.unsyncedRandom = new Il2CppNinjaKiwi.Common.BufferedRandom(replay.randomSeed);
                    sim.syncedObjectId = new ObjectId { data = 0 };
                    if (sim.model != null)
                    {
                        sim.model.randomSeed = replay.randomSeed;
                    }
                    MelonLogger.Msg($"[REPLAY VIEWER] Reseeded simulation to seed {replay.randomSeed}, ObjectId reset to 0.");
                }

                // 7. Reset tick counter and spectator playback flags
                TimeManager.simulatedFrames = 0;
                SpectatorTimeScale = 1.0f;
                TimeManager.gamePaused = false;
                IsPaused = false;
                IsReplayFinished = false;
                HasDiverged = false;
                DivergenceStep = 0;
                DivergenceReason = "";
                HasShownDivergencePopup = false;
                bridge._IsSpectatorMode_k__BackingField = true;
                IsSpectating = true;
                TargetEndRound = targetRound;

                // 8. Apply spectator locks immediately
                ApplySpectatorLocks(inGame);

                MelonLogger.Msg($"[REPLAY VIEWER] Replay restart complete! Ready from tick 0. Target round: {TargetEndRound}");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[REPLAY VIEWER] Failed during RestartPlayback: {ex}");
            }
            finally
            {
                IsRestartingPlayback = false;
            }
        }

        public static void UpdatePlayback(InGame inGame)
        {
            if (!IsSpectating || inGame == null || inGame.bridge == null) return;

            // Continuously enforce spectator locks
            ApplySpectatorLocks(inGame);

            var bridge = inGame.bridge;
            int internalRound = bridge.GetCurrentRound();
            int currentRound = internalRound + 1; // 1-indexed human round
            bool roundsActive = bridge.AreRoundsActive();

            // Check for RLR Checkpoint Reload
            if (ActiveCheckpoints != null && CurrentCheckpointIndex < ActiveCheckpoints.Count)
            {
                var cp = ActiveCheckpoints[CurrentCheckpointIndex];
                if (!ExecutedCheckpointRounds.Contains(cp.Round) && (currentRound >= cp.Round || internalRound + 1 >= cp.Round) && !roundsActive)
                {
                    ExecuteCheckpointReload(inGame, bridge, cp);
                    return;
                }
            }

            // Check remaining queued replay actions
            int remainingActions = bridge.actions != null ? bridge.actions.Count : 0;

            bool isFinished = false;

            // Case A: Explicit target round reached and bloon wave cleared
            if (TargetEndRound > 0 && currentRound >= TargetEndRound && !roundsActive && !IsReplayFinished)
            {
                var sim = bridge.simulation;
                if (sim != null)
                {
                    sim.blockAutoPlay = true;
                    sim.autoPlay = false;
                }
                TimeManager.gamePaused = true;
                IsPaused = true;
                isFinished = true;
            }
            // Case B: Full run playback (all actions in queue exhausted, active round finished, and all checkpoints executed)
            else if (TargetEndRound == 0 && remainingActions == 0 && !roundsActive && !IsReplayFinished && (ActiveCheckpoints == null || CurrentCheckpointIndex >= ActiveCheckpoints.Count))
            {
                isFinished = true;
            }

            if (isFinished)
            {
                FinishReplay(inGame, bridge);
            }
        }

        public static void OnRoundBoundaryMapSave(InGame inGame, int highestCompletedRound)
        {
            if (!IsSpectating || inGame == null || inGame.bridge == null) return;

            var bridge = inGame.bridge;
            int completedRound = highestCompletedRound + 1;
            int nextRound = completedRound + 1;
            int internalRound = bridge.GetCurrentRound();
            MelonLogger.Msg($"[REPLAY VIEWER] Native round boundary: highestCompletedRound={highestCompletedRound}, completedRound={completedRound}, nextRound={nextRound}, internalRound={internalRound}, TargetEndRound={TargetEndRound}");

            // 1. Check for RLR Checkpoint Reload (must fire BEFORE nextRound starts!)
            if (ActiveCheckpoints != null && CurrentCheckpointIndex < ActiveCheckpoints.Count)
            {
                var cp = ActiveCheckpoints[CurrentCheckpointIndex];
                if (!ExecutedCheckpointRounds.Contains(cp.Round) && (nextRound == cp.Round || completedRound >= cp.Round))
                {
                    ExecuteCheckpointReload(inGame, bridge, cp);
                    return;
                }
            }

            // 2. Check for Replay Target Round completion
            if (TargetEndRound > 0 && (completedRound >= TargetEndRound || internalRound + 1 >= TargetEndRound))
            {
                var sim = bridge.simulation;
                if (sim != null)
                {
                    sim.blockAutoPlay = true;
                    sim.autoPlay = false;
                }
                TimeManager.gamePaused = true;
                IsPaused = true;
                FinishReplay(inGame, bridge);
            }
        }

        public static void FinishReplay(InGame inGame, UnityToSimulation bridge)
        {
            if (IsReplayFinished) return;
            IsReplayFinished = true;
            IsPaused = true;
            TimeManager.gamePaused = true;

            // Block auto-play from advancing into unrecorded rounds
            var sim = bridge?.simulation;
            if (sim != null)
            {
                sim.blockAutoPlay = true;
                sim.autoPlay = false;
            }

            int finalRound = (bridge?.GetCurrentRound() ?? 0) + 1;
            MelonLogger.Msg($"[REPLAY VIEWER] Replay completed! Final round {finalRound} reached and finished. Playback paused.");

            // Show native authentic BTD6 replay completion modal
            try
            {
                var popup = Il2CppAssets.Scripts.Unity.UI_New.Popups.PopupScreen.instance;
                if (popup != null)
                {
                    string body = $"<b><color=#2ECC71>Replay Completed!</color></b>\n\nPlayback finished at <b>Round {finalRound}</b>.\nAll recorded actions were executed successfully.";
                    popup.ShowOkPopup(body);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[REPLAY VIEWER] Could not show completion popup: {ex.Message}");
            }
        }

        public static void ExecuteCheckpointReload(InGame inGame, UnityToSimulation bridge, RLRCheckpoint cp)
        {
            MelonLogger.Msg($"[REPLAY VIEWER] Checkpoint Round {cp.Round} reached (index #{CurrentCheckpointIndex + 1}/{ActiveCheckpoints.Count}). Initiating RLR reload sequence...");

            // a) Directly deserialize cp.MapSaveJson (exact ground truth save from recorded session)
            MapSaveDataModel? save = null;
            if (!string.IsNullOrEmpty(cp.MapSaveJson))
            {
                try
                {
                    save = Il2CppNewtonsoft.Json.JsonConvert.DeserializeObject<MapSaveDataModel>(cp.MapSaveJson);
                    MelonLogger.Msg($"[REPLAY VIEWER] Deserialized ground truth MapSaveDataModel from companion JSON for Round {cp.Round} (round={save?.round}).");
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"[REPLAY VIEWER] Failed to deserialize cp.MapSaveJson: {ex.Message}");
                }
            }

            if (save == null)
            {
                try
                {
                    save = inGame.GetLastRoundMapSaveData();
                    if (save != null)
                    {
                        MelonLogger.Msg($"[REPLAY VIEWER] Fallback inGame.GetLastRoundMapSaveData succeeded @ round {save.round}.");
                    }
                }
                catch { }
            }

            if (save == null)
            {
                MelonLogger.Warning($"[REPLAY VIEWER] Checkpoint save for Round {cp.Round} could not be captured or deserialized. Skipping reload for this checkpoint.");
                ExecutedCheckpointRounds.Add(cp.Round);
                CurrentCheckpointIndex++;
                return;
            }

            // b) Prepare continuation replay actions:
            int nextCheckpointIndex = CurrentCheckpointIndex + 1;
            int nextCheckpointStart = (ActiveCheckpoints != null && nextCheckpointIndex < ActiveCheckpoints.Count)
                ? ActiveCheckpoints[nextCheckpointIndex].ActionStartIndex
                : -1;

            var replaySource = OriginalReplay ?? ActiveReplay;
            SinglePlayerReplay? continuation = null;
            if (replaySource != null)
            {
                try
                {
                    string serialized = replaySource.Serialize();
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(serialized);
                    Il2CppStructArray<byte> il2cppBytes = bytes;
                    continuation = Replay.DeserializeSP(il2cppBytes);

                    if (continuation?.actions != null)
                    {
                        int startIdx = Math.Clamp(cp.ActionStartIndex, 0, continuation.actions.Count);
                        for (int i = 0; i < startIdx; i++)
                        {
                            continuation.actions.RemoveAt(0);
                        }

                        if (nextCheckpointStart > cp.ActionStartIndex)
                        {
                            int allowedCount = nextCheckpointStart - cp.ActionStartIndex;
                            while (continuation.actions.Count > allowedCount)
                            {
                                continuation.actions.RemoveAt(continuation.actions.Count - 1);
                            }
                            MelonLogger.Msg($"[REPLAY VIEWER] Segmented next slice: actions {cp.ActionStartIndex}..{nextCheckpointStart} ({continuation.actions.Count} actions).");
                        }
                        else
                        {
                            MelonLogger.Msg($"[REPLAY VIEWER] Sliced remaining actions from global index {cp.ActionStartIndex}: {continuation.actions.Count} actions to inject.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"[REPLAY VIEWER] Failed to slice continuation actions: {ex.Message}");
                }
            }

            // c) Execute native in-place checkpoint continuation (exact TimeMachine pipeline)
            MelonLogger.Msg($"[REPLAY VIEWER] Checkpoint Round {cp.Round} reached: executing in-place checkpoint continuation via bridge.ExecuteContinueFromCheckpoint...");
            try
            {
                bridge.ExecuteContinueFromCheckpoint(bridge.GetInputId(), new Il2CppAssets.Scripts.Utils.KonFuze(), ref save, true, false);
                MelonLogger.Msg($"[REPLAY VIEWER] bridge.ExecuteContinueFromCheckpoint completed cleanly for Round {cp.Round}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[REPLAY VIEWER] ExecuteContinueFromCheckpoint threw exception: {ex.Message}");
            }

            // d) Re-apply spectator flags and locks immediately (guaranteed spectator mode)
            bridge._IsSpectatorMode_k__BackingField = true;
            if (inGame.bridge != null)
            {
                inGame.bridge._IsSpectatorMode_k__BackingField = true;
            }
            IsSpectating = true;
            ApplySpectatorLocks(inGame);

            // e) Inject remaining / segment actions directly into bridge
            if (bridge.actions != null)
            {
                bridge.actions.Clear();
            }

            if (continuation?.actions != null)
            {
                for (int i = 0; i < continuation.actions.Count; i++)
                {
                    bridge.AddAction(continuation.actions[i]);
                }
                MelonLogger.Msg($"[REPLAY VIEWER] Injected {continuation.actions.Count} actions into bridge for Round {cp.Round}+.");
            }

            ExecutedCheckpointRounds.Add(cp.Round);
            CurrentCheckpointIndex++;
            LastCheckpointBanner = $"RLR Checkpoint #{CurrentCheckpointIndex} @ Round {cp.Round} Reloaded!";
            LastCheckpointBannerTime = UnityEngine.Time.time + 5.0f;
            ActiveReplay = continuation;
            ActiveReplayActionOffset = cp.ActionStartIndex;
            PendingReplay = null;
            PendingReloadSave = null;
            IsReloadingForRLR = false;
        }

        public static void TriggerMapReload()
        {
            try
            {
                IsReloadingForRLR = false;
                var save = PendingReloadSave;
                if (save == null)
                {
                    MelonLogger.Warning("[REPLAY VIEWER] TriggerMapReload called but PendingReloadSave is null.");
                    return;
                }

                var editable = InGameData.Editable;
                if (editable != null)
                {
                    if (ActiveReplay != null)
                    {
                        editable.selectedMap = ActiveReplay.map;
                        editable.selectedDifficulty = ActiveReplay.difficulty;
                        editable.selectedMode = ActiveReplay.mode;
                    }
                    else if (!string.IsNullOrEmpty(save.mapName))
                    {
                        editable.selectedMap = save.mapName;
                    }
                }

                MelonLogger.Msg($"[REPLAY VIEWER] TriggerMapReload: Loading saved game @ Round {save.round} on map {editable?.selectedMap} ({editable?.selectedDifficulty} - {editable?.selectedMode}) via UI.instance.LoadGame...");

                var ui = Il2CppAssets.Scripts.Unity.UI_New.UI.instance;
                if (ui != null)
                {
                    ui.LoadGame(null, null, save, false, false);
                }
                else
                {
                    MelonLogger.Error("[REPLAY VIEWER] UI.instance is null! Cannot trigger LoadGame.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[REPLAY VIEWER] Failed to trigger map reload: {ex}");
            }
        }

        public static void StopSpectatorMode(InGame? inGame = null)
        {
            if (!IsSpectating && PendingReplay == null) return;

            OriginalReplay = null;
            ActiveReplay = null;
            ActiveCheckpoints.Clear();
            ExecutedCheckpointRounds.Clear();
            CurrentCheckpointIndex = 0;
            ActiveReplayActionOffset = 0;
            PendingReplayActionOffset = 0;
            IsCreatingCheckpointSave = false;
            IsReloadingForRLR = false;
            PendingReloadSave = null;
            LastCheckpointBanner = "";
            LastCheckpointBannerTime = 0f;

            IsSpectating = false;
            PendingReplay = null;
            PendingTargetRound = 0;
            IsPaused = false;
            IsReplayFinished = false;
            HasDiverged = false;
            DivergenceStep = 0;
            DivergenceReason = "";
            HasShownDivergencePopup = false;
            TargetEndRound = 0;
            SpectatorTimeScale = 1.0f;
            TimeManager.gamePaused = false;

            if (inGame != null)
            {
                inGame.lockTowerPurchases = false;
                inGame.lockHeroPurchases = false;
                inGame.lockTowerUpgrades = false;
                inGame.lockPowerActivations = false;
                inGame.lockAbilities = false;
                try
                {
                    if (inGame.hotkeys != null)
                    {
                        inGame.hotkeys.suppressHotkeys = 0;
                    }
                }
                catch { }

                if (inGame.bridge != null)
                {
                    inGame.bridge._IsSpectatorMode_k__BackingField = false;
                }

                try
                {
                    inGame.RemoveCurrentMapSave(true);
                }
                catch { }

                try
                {
                    string mapId = inGame.MapDataSaveId ?? InGameData.Editable?.selectedMap ?? "";
                    if (!string.IsNullOrEmpty(mapId))
                    {
                        Game.Player?.Data?.RemoveSavedMap(mapId);
                    }
                }
                catch { }
            }

            MelonLogger.Msg("[REPLAY VIEWER] Spectator mode stopped. Controls restored and residual save state purged.");
        }

        public static void DrawSpectatorHUD(InGame? inGame)
        {
            if (!IsSpectating || inGame == null) return;

            float hudW = 450;
            float hudH = HasDiverged ? 160 : 142;
            float hudX = Screen.width - hudW - 20;
            float hudY = 20;

            GUI.backgroundColor = new Color(0.1f, 0.12f, 0.18f, 0.95f);
            GUI.Box(new Rect(hudX, hudY, hudW, hudH), "");
            GUI.backgroundColor = Color.white;

            if (HasDiverged)
            {
                GUI.Label(new Rect(hudX + 12, hudY + 8, hudW - 24, 20), $"<size=13><color=#E74C3C><b>⚠ DIVERGED @ STEP {DivergenceStep} (RNG Drift)</b></color></size>");
            }
            else if (IsReplayFinished)
            {
                int endRd = inGame.bridge?.GetCurrentRound() ?? TargetEndRound;
                GUI.Label(new Rect(hudX + 12, hudY + 8, hudW - 24, 20), $"<size=13><color=#F1C40F><b>✔ REPLAY FINISHED (RD {endRd})</b></color></size>");
            }
            else if (UnityEngine.Time.time < LastCheckpointBannerTime && !string.IsNullOrEmpty(LastCheckpointBanner))
            {
                GUI.Label(new Rect(hudX + 12, hudY + 8, hudW - 24, 20), $"<size=13><color=#F39C12><b>✔ {LastCheckpointBanner}</b></color></size>");
            }
            else
            {
                GUI.Label(new Rect(hudX + 12, hudY + 8, hudW - 24, 20), "<size=13><color=#2ECC71><b>SPECTATOR REPLAY ACTIVE</b></color></size>");
            }

            string mapInfo = ActiveReplay != null ? $"{ActiveReplay.map} ({ActiveReplay.mode})" : "Live Replay";
            int count = ActiveReplay?.actions?.Count ?? 0;
            int remaining = inGame.bridge?.actions?.Count ?? 0;
            string targetStr = TargetEndRound > 0 ? $"Target: Rd {TargetEndRound}" : "Full Run";
            string cpStr = ActiveCheckpoints.Count > 0 ? $" | CP: {CurrentCheckpointIndex}/{ActiveCheckpoints.Count}" : "";
            GUI.Label(new Rect(hudX + 12, hudY + 28, hudW - 24, 18), $"• <b>Replay:</b> <color=#00D2FF>{mapInfo}</color> ({count} acts, {targetStr}{cpStr}, Rem: {remaining})");

            double curMult = TimeManager.replayTimeScaleMultiplier;
            bool isFF = TimeManager.FastForwardActive;
            double effectiveSpeed = TimeManager.gamePaused ? 0.0 : (curMult * (isFF ? 3.0 : 1.0));
            string ffTag = isFF ? " <color=#F1C40F>(FF 3x)</color>" : "";
            string pausedStatus = TimeManager.gamePaused ? "<color=#E74C3C>[PAUSED]</color>" : $"<color=#2ECC71>[{effectiveSpeed:0.0}x{ffTag}]</color>";
            int simFrames = TimeManager.simulatedFrames;
            GUI.Label(new Rect(hudX + 12, hudY + 46, hudW - 24, 18), $"• <b>Speed:</b> <color=#F1C40F>{curMult:0.0}x mult</color> {pausedStatus} | <b>Sim Tick:</b> <color=#A9DFBF>{simFrames}</color>");

            float r1Y = hudY + 68;
            float r2Y = hudY + 104;
            if (HasDiverged)
            {
                GUI.Label(new Rect(hudX + 12, hudY + 64, hudW - 24, 16), $"<size=10><color=#E57373><i>{DivergenceReason}</i></color></size>");
                r1Y += 16;
                r2Y += 16;
            }

            // Row 1: Time Controls (Pause/Resume, Multipliers 1x/2x/5x/10x, Restart, Exit)
            string pauseLabel = TimeManager.gamePaused ? "▶ Resume" : "⏸ Pause";
            if (GUI.Button(new Rect(hudX + 10, r1Y, 78, 28), pauseLabel))
            {
                TimeManager.gamePaused = !TimeManager.gamePaused;
                IsPaused = TimeManager.gamePaused;
            }

            if (GUI.Button(new Rect(hudX + 93, r1Y, 36, 28), "1x"))
            {
                TimeManager.replayTimeScaleMultiplier = 1.0;
            }
            if (GUI.Button(new Rect(hudX + 133, r1Y, 36, 28), "2x"))
            {
                TimeManager.replayTimeScaleMultiplier = 2.0;
            }
            if (GUI.Button(new Rect(hudX + 173, r1Y, 36, 28), "5x"))
            {
                TimeManager.replayTimeScaleMultiplier = 5.0;
            }
            if (GUI.Button(new Rect(hudX + 213, r1Y, 44, 28), "10x"))
            {
                TimeManager.replayTimeScaleMultiplier = 10.0;
            }

            GUI.backgroundColor = new Color(0.25f, 0.55f, 0.9f);
            if (GUI.Button(new Rect(hudX + 265, r1Y, 78, 28), "↺ Restart"))
            {
                RestartPlayback();
            }

            GUI.backgroundColor = new Color(0.85f, 0.25f, 0.25f);
            if (GUI.Button(new Rect(hudX + 350, r1Y, 78, 28), "✕ Exit"))
            {
                MelonLogger.Msg("[REPLAY VIEWER] Exit clicked in Spectator HUD. Cleaning up and quitting cleanly to menu...");
                if (inGame != null)
                {
                    if (inGame.bridge != null)
                    {
                        inGame.bridge._IsSpectatorMode_k__BackingField = true;
                    }
                    try
                    {
                        inGame.RemoveCurrentMapSave(true);
                    }
                    catch { }

                    try
                    {
                        string mapId = inGame.MapDataSaveId ?? InGameData.Editable?.selectedMap ?? "";
                        if (!string.IsNullOrEmpty(mapId))
                        {
                            Game.Player?.Data?.RemoveSavedMap(mapId);
                        }
                    }
                    catch { }

                    inGame.Quit();
                }
                StopSpectatorMode(inGame);
            }
            GUI.backgroundColor = Color.white;

            // Row 2: Precision Stepping Controls (1 frame, 15 frames, 10 seconds, Freeplay)
            if (GUI.Button(new Rect(hudX + 10, r2Y, 82, 28), "▶| 1 Frame"))
            {
                StepSimulation(1);
            }

            if (GUI.Button(new Rect(hudX + 97, r2Y, 120, 28), "▶▶| 15 F (0.25s)"))
            {
                StepSimulation(15);
            }

            if (GUI.Button(new Rect(hudX + 222, r2Y, 120, 28), "⏩| 10s (600 F)"))
            {
                StepSimulation(600);
            }

            if (IsReplayFinished)
            {
                GUI.backgroundColor = new Color(0.2f, 0.7f, 0.3f);
                if (GUI.Button(new Rect(hudX + 350, r2Y, 78, 28), "Freeplay"))
                {
                    StopSpectatorMode(inGame);
                }
                GUI.backgroundColor = Color.white;
            }
        }
    }
}
