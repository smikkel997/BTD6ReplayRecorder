using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MelonLoader;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppAssets.Scripts.Unity.UI_New.Popups;
using Il2CppAssets.Scripts.Unity.Network;
using Il2CppAssets.Scripts.Models.Profile;

namespace BTD6ReplayRecorder
{
    /// <summary>
    /// Tracks user opt-in status and match parameters for recorded runs.
    /// </summary>
    public class RecordedRunEntry
    {
        public bool IsRecorded { get; set; } = true;
        public string? Map { get; set; }
        public string? Difficulty { get; set; }
        public string? Mode { get; set; }
        public string? StartedAt { get; set; }
        public int RandomSeed { get; set; }
    }

    /// <summary>
    /// Represents a saved game boundary snapshot for Reload Round (RLR) session continuation.
    /// </summary>
    public class RLRCheckpoint
    {
        public int Round { get; set; }
        public int ElapsedTime { get; set; }
        public string? MapSaveJson { get; set; }
        public int ActionStartIndex { get; set; }
    }

    /// <summary>
    /// Maintains active recording session state across match re-entries and round resets.
    /// </summary>
    public class ActiveSessionState
    {
        public string RunKey { get; set; } = "";
        public string Map { get; set; } = "";
        public string Difficulty { get; set; } = "";
        public string Mode { get; set; } = "";
        public int RandomSeed { get; set; }
        public uint SyncedObjectId { get; set; }
        public string? PreviousActionsSerialized { get; set; }

        public int LastCheckpointRound { get; set; } = 0;
        public int LastCheckpointActionCount { get; set; } = 0;
        public int LastCheckpointElapsedTime { get; set; } = 0;
        public string? LastCheckpointMapSaveJson { get; set; }

        public List<RLRCheckpoint> Checkpoints { get; set; } = new();
    }

    /// <summary>
    /// Companion JSON metadata sidecar schema stored alongside .btd6replay files.
    /// </summary>
    public class ReplayMetadata
    {
        public string map { get; set; } = "";
        public string difficulty { get; set; } = "";
        public string mode { get; set; } = "";
        public int dumpRound { get; set; }
        public int seed { get; set; }
        public uint syncedObjectId { get; set; }
        public int actionCount { get; set; }
        public string timestamp { get; set; } = "";
        public List<RLRCheckpoint> rlrCheckpoints { get; set; } = new();
    }

    /// <summary>
    /// Core coordinator for recording single-player matches and exporting replay bundles.
    /// </summary>
    public static class ReplayManager
    {
        public static string ReplayDirectory => Path.Combine(Directory.GetCurrentDirectory(), "Replays");
        public static string TempDirectory => Path.Combine(ReplayDirectory, "Temp");
        public static string LedgerPath => Path.Combine(Directory.GetCurrentDirectory(), "RecordedRuns.json");

        // Staging flags for upcoming matches
        public static bool PendingDecisionRequired { get; set; } = false;
        public static bool ShouldRecordUpcomingMatch { get; set; } = false;
        public static string? PendingMap { get; set; }
        public static string? PendingDifficulty { get; set; }
        public static string? PendingMode { get; set; }

        // Active match state
        public static bool IsCurrentMatchRecorded { get; set; } = false;
        public static string? ActiveMatchKey { get; set; }
        public static ActiveSessionState? CurrentSession { get; set; } = null;

        private static readonly object FileLock = new();

        public static void Initialize()
        {
            try
            {
                if (!Directory.Exists(ReplayDirectory))
                {
                    Directory.CreateDirectory(ReplayDirectory);
                }
                if (!Directory.Exists(TempDirectory))
                {
                    Directory.CreateDirectory(TempDirectory);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[REPLAY RECORDER] Failed to create directories: {ex.Message}");
            }
        }

        public static string GetRunKey(string? map, string? difficulty, string? mode)
        {
            string m = (map ?? "Unknown").Trim();
            string d = (difficulty ?? "Unknown").Trim();
            string md = (mode ?? "Unknown").Trim();
            return $"{m}_{d}_{md}";
        }

        public static string GetSessionFilePath(string runKey)
        {
            return Path.Combine(TempDirectory, $"{runKey}_session.json");
        }

        #region Session Persistence

        public static void SaveSessionState(ActiveSessionState state)
        {
            lock (FileLock)
            {
                try
                {
                    Directory.CreateDirectory(TempDirectory);
                    string path = GetSessionFilePath(state.RunKey);
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    string json = JsonSerializer.Serialize(state, options);
                    File.WriteAllText(path, json);
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"[REPLAY RECORDER] Failed to save session state: {ex.Message}");
                }
            }
        }

        public static ActiveSessionState? LoadSessionState(string runKey)
        {
            lock (FileLock)
            {
                try
                {
                    string path = GetSessionFilePath(runKey);
                    if (File.Exists(path))
                    {
                        string json = File.ReadAllText(path);
                        return JsonSerializer.Deserialize<ActiveSessionState>(json);
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[REPLAY RECORDER] Could not load session state for '{runKey}': {ex.Message}");
                }
                return null;
            }
        }

        public static void DeleteSessionState(string runKey)
        {
            lock (FileLock)
            {
                try
                {
                    string path = GetSessionFilePath(runKey);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        MelonLogger.Msg($"[REPLAY RECORDER] Cleaned up session file '{path}'.");
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[REPLAY RECORDER] Failed to delete session file for '{runKey}': {ex.Message}");
                }
            }
        }

        #endregion

        #region Persistent Ledger (RecordedRuns.json)

        private static Dictionary<string, RecordedRunEntry> LoadLedger()
        {
            lock (FileLock)
            {
                try
                {
                    if (File.Exists(LedgerPath))
                    {
                        string json = File.ReadAllText(LedgerPath);
                        var data = JsonSerializer.Deserialize<Dictionary<string, RecordedRunEntry>>(json);
                        if (data != null) return data;
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[REPLAY RECORDER] Failed to read ledger: {ex.Message}. Starting fresh.");
                }
                return new Dictionary<string, RecordedRunEntry>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void SaveLedger(Dictionary<string, RecordedRunEntry> ledger)
        {
            lock (FileLock)
            {
                try
                {
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    string json = JsonSerializer.Serialize(ledger, options);
                    File.WriteAllText(LedgerPath, json);
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"[REPLAY RECORDER] Failed to save ledger: {ex.Message}");
                }
            }
        }

        public static bool IsSaveMarkedRecorded(string? map, string? difficulty, string? mode)
        {
            if (string.IsNullOrEmpty(map)) return false;
            string key = GetRunKey(map, difficulty, mode);
            var ledger = LoadLedger();
            return ledger.TryGetValue(key, out var entry) && entry.IsRecorded;
        }

        public static void MarkRunRecorded(string? map, string? difficulty, string? mode, bool recorded, int seed = 0)
        {
            if (string.IsNullOrEmpty(map)) return;
            string key = GetRunKey(map, difficulty, mode);
            var ledger = LoadLedger();

            if (recorded)
            {
                ledger[key] = new RecordedRunEntry
                {
                    IsRecorded = true,
                    Map = map,
                    Difficulty = difficulty,
                    Mode = mode,
                    StartedAt = DateTime.UtcNow.ToString("o"),
                    RandomSeed = seed
                };
            }
            else
            {
                ledger.Remove(key);
            }

            SaveLedger(ledger);
            MelonLogger.Msg($"[REPLAY RECORDER] Updated run ledger for '{key}': Recorded={recorded}");
        }

        public static void ClearRunRecord(string? map, string? difficulty, string? mode)
        {
            if (string.IsNullOrEmpty(map)) return;
            string key = GetRunKey(map, difficulty, mode);
            var ledger = LoadLedger();
            if (ledger.Remove(key))
            {
                SaveLedger(ledger);
                MelonLogger.Msg($"[REPLAY RECORDER] Cleared finished run from ledger: '{key}'");
            }
            DeleteSessionState(key);
        }

        #endregion

        #region Match Lifecycle & Recording Injection

        public static void OnMatchStarted(InGame inGame, UnityToSimulation bridge, bool wasMapSaveLoaded, MapSaveDataModel? loadedSave = null)
        {
            var readonlyData = InGameData.Editable;
            string? map = readonlyData?.selectedMap;
            string? diff = readonlyData?.selectedDifficulty;
            string? mode = readonlyData?.selectedMode;
            string key = GetRunKey(map, diff, mode);
            ActiveMatchKey = key;

            if (wasMapSaveLoaded)
            {
                // Continuing an existing save: check if this save was originally marked for recording or has a temp session
                bool wasRecorded = IsSaveMarkedRecorded(map, diff, mode);
                string sessionFile = GetSessionFilePath(key);
                bool hasSession = File.Exists(sessionFile);

                if (wasRecorded || hasSession)
                {
                    IsCurrentMatchRecorded = true;
                    CurrentSession = LoadSessionState(key) ?? new ActiveSessionState
                    {
                        RunKey = key,
                        Map = map ?? "",
                        Difficulty = diff ?? "",
                        Mode = mode ?? ""
                    };

                    int round = loadedSave?.round ?? CurrentSession.LastCheckpointRound;
                    int elapsed = bridge != null ? bridge.ElapsedTime : CurrentSession.LastCheckpointElapsedTime;

                    string? mapSaveJson = null;
                    if (loadedSave != null)
                    {
                        try { mapSaveJson = Il2CppNewtonsoft.Json.JsonConvert.SerializeObject(loadedSave); } catch { }
                    }
                    if (string.IsNullOrEmpty(mapSaveJson))
                    {
                        mapSaveJson = CurrentSession.LastCheckpointMapSaveJson;
                    }

                    int actionStartIndex = 0;
                    if (!string.IsNullOrEmpty(CurrentSession.PreviousActionsSerialized))
                    {
                        try
                        {
                            var prevBytes = (Il2CppStructArray<byte>)System.Text.Encoding.UTF8.GetBytes(CurrentSession.PreviousActionsSerialized);
                            var prevSp = Replay.DeserializeSP(prevBytes);
                            actionStartIndex = prevSp?.actions?.Count ?? 0;
                        }
                        catch (Exception ex)
                        {
                            MelonLogger.Warning($"[REPLAY RECORDER] Could not compute actionStartIndex: {ex.Message}");
                        }
                    }

                    // Record RLRCheckpoint for this continuation
                    var checkpoint = new RLRCheckpoint
                    {
                        Round = round,
                        ElapsedTime = elapsed,
                        MapSaveJson = mapSaveJson,
                        ActionStartIndex = actionStartIndex
                    };
                    CurrentSession.Checkpoints.Add(checkpoint);

                    // Reset per-segment action count tracking for new bridge recorder
                    CurrentSession.LastCheckpointActionCount = 0;
                    CurrentSession.LastCheckpointRound = round;
                    CurrentSession.LastCheckpointElapsedTime = elapsed;
                    CurrentSession.LastCheckpointMapSaveJson = mapSaveJson;

                    SaveSessionState(CurrentSession);

                    MelonLogger.Msg($"[REPLAY RECORDER] Continued saved game '{key}'. Recorded RLR Checkpoint #{CurrentSession.Checkpoints.Count} @ Round {round}. Resuming action recording...");
                    StartEngineRecorder(bridge);
                }
                else
                {
                    IsCurrentMatchRecorded = false;
                    CurrentSession = null;
                    MelonLogger.Msg($"[REPLAY RECORDER] Continued saved game '{key}' was NOT marked for recording. Skipping recorder.");
                }
            }
            else
            {
                // Fresh new match: delete any leftover temp session from previous runs on this map
                DeleteSessionState(key);

                // Fresh new match: use decision made at prompt
                IsCurrentMatchRecorded = ShouldRecordUpcomingMatch;
                if (ShouldRecordUpcomingMatch)
                {
                    int seed = bridge?.simulation?.model?.randomSeed ?? 0;
                    MarkRunRecorded(map, diff, mode, true, seed);

                    CurrentSession = new ActiveSessionState
                    {
                        RunKey = key,
                        Map = map ?? "",
                        Difficulty = diff ?? "",
                        Mode = mode ?? "",
                        RandomSeed = seed,
                        SyncedObjectId = bridge?.simulation?.syncedObjectId.data ?? 0
                    };

                    MelonLogger.Msg($"[REPLAY RECORDER] New match '{key}' marked for recording (Seed={seed}). Starting action recorder...");
                    StartEngineRecorder(bridge);
                }
                else
                {
                    CurrentSession = null;
                    MarkRunRecorded(map, diff, mode, false);
                    MelonLogger.Msg($"[REPLAY RECORDER] New match '{key}' started without recording (player opted out).");
                }
            }

            // Reset staging flag
            ShouldRecordUpcomingMatch = false;
        }

        private static void StartEngineRecorder(UnityToSimulation? bridge)
        {
            if (bridge == null) return;
            try
            {
                if (bridge.ReplayRecorder == null)
                {
                    bridge.Record(null);
                    MelonLogger.Msg("[REPLAY RECORDER] Native engine recorder initialized via bridge.Record(null).");
                }
                else
                {
                    MelonLogger.Msg("[REPLAY RECORDER] Engine recorder already active on bridge.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[REPLAY RECORDER] Failed to start engine recorder: {ex.Message}");
            }
        }

        public static void SnapshotRoundBoundary(InGame inGame)
        {
            if (!IsCurrentMatchRecorded || CurrentSession == null || inGame == null) return;
            try
            {
                var bridge = inGame.bridge;
                if (bridge == null) return;

                int currentRound = bridge.GetCurrentRound();
                int actionCount = bridge.ReplayRecorder?.actions?.Count ?? 0;
                int elapsed = bridge.ElapsedTime;

                var mapSave = inGame.GetLastRoundMapSaveData();
                string? mapSaveJson = null;
                if (mapSave != null)
                {
                    mapSaveJson = Il2CppNewtonsoft.Json.JsonConvert.SerializeObject(mapSave);
                }

                CurrentSession.LastCheckpointRound = currentRound;
                CurrentSession.LastCheckpointActionCount = actionCount;
                CurrentSession.LastCheckpointElapsedTime = elapsed;
                if (!string.IsNullOrEmpty(mapSaveJson))
                {
                    CurrentSession.LastCheckpointMapSaveJson = mapSaveJson;
                }

                MelonLogger.Msg($"[REPLAY RECORDER] Round {currentRound} boundary snapshotted: ActionCount={actionCount}, ElapsedTime={elapsed}, HasMapSave={!string.IsNullOrEmpty(mapSaveJson)}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[REPLAY RECORDER] Error snapshotting round boundary: {ex.Message}");
            }
        }

        public static void OnMatchExited(InGame inGame)
        {
            if (!IsCurrentMatchRecorded || inGame == null) return;

            try
            {
                var bridge = inGame.bridge;
                string key = ActiveMatchKey ?? GetRunKey(InGameData.Editable?.selectedMap, InGameData.Editable?.selectedDifficulty, InGameData.Editable?.selectedMode);

                if (CurrentSession == null)
                {
                    CurrentSession = LoadSessionState(key) ?? new ActiveSessionState
                    {
                        RunKey = key,
                        Map = InGameData.Editable?.selectedMap ?? "",
                        Difficulty = InGameData.Editable?.selectedDifficulty ?? "",
                        Mode = InGameData.Editable?.selectedMode ?? "",
                        RandomSeed = bridge?.simulation?.model?.randomSeed ?? 0
                    };
                }

                if (bridge?.ReplayRecorder != null)
                {
                    string curSer = bridge.ReplayRecorder.Serialize();
                    if (!string.IsNullOrWhiteSpace(curSer))
                    {
                        var curBytes = (Il2CppStructArray<byte>)System.Text.Encoding.UTF8.GetBytes(curSer);
                        var curSp = Replay.DeserializeSP(curBytes);

                        if (curSp?.actions != null)
                        {
                            // If a round boundary was snapshotted in this session, trim abandoned mid-round actions
                            if (CurrentSession.LastCheckpointActionCount > 0 && curSp.actions.Count > CurrentSession.LastCheckpointActionCount)
                            {
                                int trimmed = curSp.actions.Count - CurrentSession.LastCheckpointActionCount;
                                MelonLogger.Msg($"[REPLAY RECORDER] Trimming {trimmed} abandoned mid-round actions after checkpoint Round {CurrentSession.LastCheckpointRound}.");
                                while (curSp.actions.Count > CurrentSession.LastCheckpointActionCount)
                                {
                                    curSp.actions.RemoveAt(curSp.actions.Count - 1);
                                }
                            }

                            // Merge with any previous session actions
                            if (!string.IsNullOrEmpty(CurrentSession.PreviousActionsSerialized))
                            {
                                var prevBytes = (Il2CppStructArray<byte>)System.Text.Encoding.UTF8.GetBytes(CurrentSession.PreviousActionsSerialized);
                                var prevSp = Replay.DeserializeSP(prevBytes);
                                if (prevSp?.actions != null)
                                {
                                    for (int i = 0; i < curSp.actions.Count; i++)
                                    {
                                        prevSp.actions.Add(curSp.actions[i]);
                                    }
                                    CurrentSession.PreviousActionsSerialized = prevSp.Serialize();
                                }
                                else
                                {
                                    CurrentSession.PreviousActionsSerialized = curSp.Serialize();
                                }
                            }
                            else
                            {
                                CurrentSession.PreviousActionsSerialized = curSp.Serialize();
                            }
                        }
                    }
                }

                // If map save wasn't snapshotted yet, try to capture it now
                if (string.IsNullOrEmpty(CurrentSession.LastCheckpointMapSaveJson))
                {
                    try
                    {
                        var save = inGame.GetLastRoundMapSaveData();
                        if (save != null)
                        {
                            CurrentSession.LastCheckpointMapSaveJson = Il2CppNewtonsoft.Json.JsonConvert.SerializeObject(save);
                            CurrentSession.LastCheckpointRound = save.round;
                        }
                    }
                    catch { }
                }

                SaveSessionState(CurrentSession);
                MelonLogger.Msg($"[REPLAY RECORDER] Session state successfully persisted to Temp/{key}_session.json on match exit. Total RLR Checkpoints so far: {CurrentSession.Checkpoints.Count}");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[REPLAY RECORDER] Failed to persist session state on match exit: {ex.Message}");
            }
        }

        #endregion

        #region Export / Dump Replay

        public static (bool success, string filename, string fullPath, string message) DumpCurrentMatchRecording(string reason = "manual")
        {
            var inGame = InGame.instance;
            var bridge = inGame?.bridge;

            if (bridge == null || bridge.ReplayRecorder == null)
            {
                string err = "No active replay recorder buffer found for this match.";
                MelonLogger.Warning($"[REPLAY RECORDER] Cannot export: {err}");
                return (false, "", "", err);
            }

            try
            {
                string curSerialized = bridge.ReplayRecorder.Serialize();
                if (string.IsNullOrWhiteSpace(curSerialized) && string.IsNullOrEmpty(CurrentSession?.PreviousActionsSerialized))
                {
                    string err = "Replay buffer is empty (no simulation actions captured).";
                    MelonLogger.Warning($"[REPLAY RECORDER] Cannot export: {err}");
                    return (false, "", "", err);
                }

                Directory.CreateDirectory(ReplayDirectory);

                var readonlyData = InGameData.Editable;
                string map = readonlyData?.selectedMap ?? (CurrentSession?.Map ?? "UnknownMap");
                string diff = readonlyData?.selectedDifficulty ?? (CurrentSession?.Difficulty ?? "Normal");
                string mode = readonlyData?.selectedMode ?? (CurrentSession?.Mode ?? "Standard");
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                int currentRound = bridge.GetCurrentRound();

                string filename = $"replay_{map}_{diff}_{mode}_R{currentRound}_{timestamp}.btd6replay";
                string fullPath = Path.Combine(ReplayDirectory, filename);

                // Stitch previous actions + current bridge actions together
                string finalSerialized;
                int totalActions = 0;

                if (CurrentSession != null && !string.IsNullOrEmpty(CurrentSession.PreviousActionsSerialized))
                {
                    var prevBytes = (Il2CppStructArray<byte>)System.Text.Encoding.UTF8.GetBytes(CurrentSession.PreviousActionsSerialized);
                    var finalSp = Replay.DeserializeSP(prevBytes);

                    if (!string.IsNullOrWhiteSpace(curSerialized))
                    {
                        var curBytes = (Il2CppStructArray<byte>)System.Text.Encoding.UTF8.GetBytes(curSerialized);
                        var curSp = Replay.DeserializeSP(curBytes);
                        if (curSp?.actions != null && finalSp?.actions != null)
                        {
                            for (int i = 0; i < curSp.actions.Count; i++)
                            {
                                finalSp.actions.Add(curSp.actions[i]);
                            }
                        }
                    }

                    finalSerialized = finalSp != null ? finalSp.Serialize() : curSerialized;
                    totalActions = finalSp?.actions?.Count ?? (bridge.ReplayRecorder.actions?.Count ?? 0);
                    MelonLogger.Msg($"[REPLAY RECORDER] Stitched {totalActions} actions across session segments (including {CurrentSession.Checkpoints.Count} RLR checkpoints).");
                }
                else
                {
                    finalSerialized = curSerialized;
                    totalActions = bridge.ReplayRecorder.actions?.Count ?? 0;
                }

                // Ensure each checkpoint's ActionStartIndex correctly tracks where its segment starts in the stitched actions list
                if (CurrentSession?.Checkpoints != null)
                {
                    for (int i = 0; i < CurrentSession.Checkpoints.Count; i++)
                    {
                        var cp = CurrentSession.Checkpoints[i];
                        if (cp.ActionStartIndex > totalActions)
                        {
                            cp.ActionStartIndex = totalActions;
                        }
                        MelonLogger.Msg($"[REPLAY RECORDER] Checkpoint #{i + 1} @ Round {cp.Round}: ActionStartIndex={cp.ActionStartIndex}, ElapsedTime={cp.ElapsedTime}");
                    }
                }

                File.WriteAllText(fullPath, finalSerialized);

                // Write companion metadata JSON for robust round, seed, and run tracking
                try
                {
                    int seed = bridge.simulation?.model?.randomSeed ?? (CurrentSession?.RandomSeed ?? 0);
                    uint objId = bridge.simulation?.syncedObjectId.data ?? (CurrentSession?.SyncedObjectId ?? 0);
                    string metaPath = Path.ChangeExtension(fullPath, ".json");

                    var meta = new ReplayMetadata
                    {
                        map = map,
                        difficulty = diff,
                        mode = mode,
                        dumpRound = currentRound,
                        seed = seed,
                        syncedObjectId = objId,
                        actionCount = totalActions,
                        timestamp = DateTime.Now.ToString("O"),
                        rlrCheckpoints = CurrentSession?.Checkpoints ?? new List<RLRCheckpoint>()
                    };

                    var options = new JsonSerializerOptions { WriteIndented = true };
                    string metaJson = JsonSerializer.Serialize(meta, options);
                    File.WriteAllText(metaPath, metaJson);
                }
                catch (Exception metaEx)
                {
                    MelonLogger.Warning($"[REPLAY RECORDER] Could not write metadata sidecar: {metaEx.Message}");
                }

                // Clean up temp session files
                string runKey = ActiveMatchKey ?? GetRunKey(map, diff, mode);
                DeleteSessionState(runKey);
                CurrentSession = null;

                long kb = new FileInfo(fullPath).Length / 1024;
                string msg = $"Saved {totalActions} actions (Round {currentRound}) to Replays/{filename} ({kb} KB)";
                MelonLogger.Msg($"[REPLAY RECORDER] Successfully dumped replay ({reason}): {msg}");

                return (true, filename, fullPath, msg);
            }
            catch (Exception ex)
            {
                string err = $"Failed to export replay: {ex.Message}";
                MelonLogger.Error($"[REPLAY RECORDER] {err}");
                return (false, "", "", err);
            }
        }

        #endregion

        #region UI Prompt & Toasts

        public static void ShowRecordPrompt(Action onConfirm, Action? onDecline = null)
        {
            var popup = PopupScreen.instance;
            if (popup == null)
            {
                MelonLogger.Warning("[REPLAY RECORDER] PopupScreen.instance is null, defaulting to recording = false");
                onDecline?.Invoke();
                return;
            }

            string title = "Record Run?";
            string body = "Would you like to record this run for replay playback and tournament/challenge verification?\n\nIf yes, all match actions will be saved.";

            var okAction = (System.Action)(() =>
            {
                MelonLogger.Msg("[REPLAY RECORDER] Player chose YES to record run.");
                ShouldRecordUpcomingMatch = true;
                onConfirm?.Invoke();
            });

            var cancelAction = (System.Action)(() =>
            {
                MelonLogger.Msg("[REPLAY RECORDER] Player chose NO to record run.");
                ShouldRecordUpcomingMatch = false;
                onDecline?.Invoke();
            });

            popup.ShowPopup(
                PopupScreen.Placement.menuCenter,
                title,
                body,
                okAction,
                "Yes",
                cancelAction,
                "No",
                Popup.TransitionAnim.Scale,
                PopupScreen.BackGround.Grey,
                false,
                false
            );
        }

        public static void ShowExportNotification(string filename, int actionCount)
        {
            var popup = PopupScreen.instance;
            if (popup != null)
            {
                string body = $"Replay successfully exported with {actionCount} actions!\n\nFile: Replays/{filename}\n\nReady for tournament/challenge verification.";
                popup.ShowOkPopup(body, null);
            }
        }

        #endregion
    }
}
