using System;
using HarmonyLib;
using MelonLoader;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppAssets.Scripts.Unity.UI_New.Pause;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Models.Profile;

namespace BTD6ReplayRecorder.Patches
{
    /// <summary>
    /// Suppresses unintended map save creation and profile persistence during replay playback.
    /// Ensures spectator mode remains strictly read-only with zero trace left in saved game slots.
    /// Also routes native PauseScreen restart directly into ReplayPlaybackManager.RestartPlayback().
    /// Snapshots round boundary map saves and captures match exit events for RLR replay recording.
    /// </summary>
    [HarmonyPatch(typeof(InGame), nameof(InGame.CreateMapSave))]
    public static class InGame_CreateMapSave_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(InGame __instance, int completedRound, int highestCompletedRound)
        {
            if (ReplayPlaybackManager.IsCreatingCheckpointSave)
            {
                return true;
            }

            if (ReplayPlaybackManager.IsSpectating || ReplayPlaybackManager.IsRestartingPlayback || ReplayPlaybackManager.PendingReplay != null || __instance.bridge?.IsSpectatorMode == true)
            {
                ReplayPlaybackManager.OnRoundBoundaryMapSave(__instance, highestCompletedRound);
                return false;
            }
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(InGame __instance)
        {
            if (ReplayManager.IsCurrentMatchRecorded)
            {
                ReplayManager.SnapshotRoundBoundary(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(InGame), nameof(InGame.CreateCurrentMapSave))]
    public static class InGame_CreateCurrentMapSave_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(InGame __instance, int highestCompletedRound, string mapSaveId, ref MapSaveDataModel __result)
        {
            if (ReplayPlaybackManager.IsCreatingCheckpointSave)
            {
                return true;
            }

            if (ReplayPlaybackManager.IsSpectating || ReplayPlaybackManager.IsRestartingPlayback || ReplayPlaybackManager.PendingReplay != null || __instance.bridge?.IsSpectatorMode == true)
            {
                ReplayPlaybackManager.OnRoundBoundaryMapSave(__instance, highestCompletedRound);
                __result = null!;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.CheckMapDataAndSave))]
    public static class Game_CheckMapDataAndSave_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (ReplayPlaybackManager.IsSpectating || ReplayPlaybackManager.IsRestartingPlayback || ReplayPlaybackManager.PendingReplay != null || InGame.instance?.bridge?.IsSpectatorMode == true)
            {
                MelonLogger.Msg("[REPLAY VIEWER] Suppressed Game.CheckMapDataAndSave during replay playback.");
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(InGame), nameof(InGame.Quit))]
    public static class InGame_Quit_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(InGame __instance)
        {
            if (ReplayManager.IsCurrentMatchRecorded)
            {
                ReplayManager.OnMatchExited(__instance);
            }

            if (ReplayPlaybackManager.IsReloadingForRLR)
            {
                MelonLogger.Msg("[REPLAY VIEWER] InGame.Quit intercepted during RLR reload. Preserving map save state for UI.instance.LoadGame reload.");
                return;
            }

            if (ReplayPlaybackManager.IsSpectating || __instance.bridge?.IsSpectatorMode == true)
            {
                MelonLogger.Msg("[REPLAY VIEWER] InGame.Quit intercepted during spectator mode. Purging residual map save state...");

                // Ensure bridge is flagged as spectator mode so native InGame.Quit skips player save logic (LABEL_98)
                if (__instance.bridge != null)
                {
                    __instance.bridge._IsSpectatorMode_k__BackingField = true;
                }

                // Delete any map save created or loaded on disk and in profile
                try
                {
                    __instance.RemoveCurrentMapSave(true);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[REPLAY VIEWER] Error in RemoveCurrentMapSave: {ex.Message}");
                }

                try
                {
                    var player = Game.Player;
                    var data = player?.Data;
                    string mapId = __instance.MapDataSaveId ?? InGameData.Editable?.selectedMap ?? "";
                    if (data != null && !string.IsNullOrEmpty(mapId))
                    {
                        data.RemoveSavedMap(mapId);
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[REPLAY VIEWER] Error removing map from profile data: {ex.Message}");
                }
            }
        }
    }

    [HarmonyPatch(typeof(UnityToSimulation), nameof(UnityToSimulation.Restart))]
    public static class UnityToSimulation_Restart_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(UnityToSimulation __instance)
        {
            if (ReplayPlaybackManager.IsRestartingPlayback)
            {
                // Internal restart execution in progress: allow native bridge.Restart to run!
                return true;
            }

            if (ReplayPlaybackManager.IsSpectating && ReplayPlaybackManager.ActiveReplay != null)
            {
                MelonLogger.Msg("[REPLAY VIEWER] UnityToSimulation.Restart intercepted during replay spectator mode! Delegating to RestartPlayback()...");
                ReplayPlaybackManager.RestartPlayback();
                return false;
            }

            return true;
        }
    }
}
