using System;
using HarmonyLib;
using MelonLoader;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppAssets.Scripts.Unity.UI_New.Main.ModeSelect;
using Il2CppAssets.Scripts.Models.Profile;
using UnityEngine;

namespace BTD6ReplayRecorder.Patches
{
    [HarmonyPatch(typeof(ModeButton), nameof(ModeButton.ButtonClicked))]
    public static class ModeButton_ButtonClicked_Patch
    {
        private static bool isBypassing = false;

        [HarmonyPrefix]
        public static bool Prefix(ModeButton __instance)
        {
            if (isBypassing)
            {
                isBypassing = false;
                return true;
            }

            var player = Game.Player;
            var data = player?.Data;
            var editable = InGameData.Editable;

            if (data == null || editable == null) return true;

            // Check if an existing saved game exists on this map
            bool hasSavedMap = data.HasSavedMap(editable.selectedMap);
            if (hasSavedMap)
            {
                // Has save: let game's native Overwrite popup trigger first
                return true;
            }

            // Brand new game with NO prior save: prompt the player now!
            ReplayManager.ShowRecordPrompt(
                onConfirm: () =>
                {
                    isBypassing = true;
                    __instance.ButtonClicked();
                },
                onDecline: () =>
                {
                    isBypassing = true;
                    __instance.ButtonClicked();
                }
            );

            // Suppress original until user answers Yes or No
            return false;
        }
    }

    [HarmonyPatch(typeof(ModeButton), "OverrideSavedGame")]
    public static class ModeButton_OverrideSavedGame_Patch
    {
        private static bool isBypassing = false;

        [HarmonyPrefix]
        public static bool Prefix(ModeButton __instance)
        {
            if (isBypassing)
            {
                isBypassing = false;
                return true;
            }

            // Player confirmed overwriting existing save: prompt for recording the new run!
            ReplayManager.ShowRecordPrompt(
                onConfirm: () =>
                {
                    isBypassing = true;
                    // Trigger native OverrideSavedGame
                    try
                    {
                        var method = typeof(ModeButton).GetMethod("OverrideSavedGame", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (method != null)
                        {
                            method.Invoke(__instance, null);
                        }
                        else
                        {
                            __instance.OverrideSavedGame();
                        }
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Error($"[REPLAY RECORDER] Failed to invoke OverrideSavedGame: {ex.Message}");
                    }
                },
                onDecline: () =>
                {
                    isBypassing = true;
                    try
                    {
                        var method = typeof(ModeButton).GetMethod("OverrideSavedGame", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (method != null)
                        {
                            method.Invoke(__instance, null);
                        }
                        else
                        {
                            __instance.OverrideSavedGame();
                        }
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Error($"[REPLAY RECORDER] Failed to invoke OverrideSavedGame: {ex.Message}");
                    }
                }
            );

            return false;
        }
    }


    [HarmonyPatch(typeof(InGame), nameof(InGame.StartMatch))]
    public static class InGame_StartMatch_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(InGame __instance, ref MapSaveDataModel mapSaveData, ref bool wasSaveOverwritten)
        {
            if (ReplayPlaybackManager.PendingReloadSave != null)
            {
                mapSaveData = ReplayPlaybackManager.PendingReloadSave;
                wasSaveOverwritten = false;
                MelonLogger.Msg($"[REPLAY VIEWER] Injecting PendingReloadSave into StartMatch for RLR continuation @ Round {mapSaveData.round}.");
            }
            else if (ReplayPlaybackManager.IsSpectating || ReplayPlaybackManager.PendingReplay != null)
            {
                // Force ignore any existing map save so it NEVER overrides RNG or simulation state
                mapSaveData = null!;
                wasSaveOverwritten = true;
                MelonLogger.Msg("[REPLAY VIEWER] Suppressed active map save to ensure replay plays from tick 0 without RNG desync.");
            }
        }

        [HarmonyPostfix]
        public static void Postfix(InGame __instance, MapSaveDataModel mapSaveData, bool wasSaveOverwritten)
        {
            bool wasReloadingRLR = ReplayPlaybackManager.IsReloadingForRLR || ReplayPlaybackManager.PendingReloadSave != null;
            ReplayPlaybackManager.PendingReloadSave = null;
            ReplayPlaybackManager.IsReloadingForRLR = false;

            bool wasSaveLoaded = mapSaveData != null && !wasSaveOverwritten;
            var bridge = __instance.bridge;

            if (bridge != null)
            {
                if (!wasReloadingRLR && !ReplayPlaybackManager.IsSpectating && ReplayPlaybackManager.PendingReplay == null)
                {
                    ReplayManager.OnMatchStarted(__instance, bridge, wasSaveLoaded, mapSaveData);
                }

                ReplayPlaybackManager.CheckPendingPlaybackOnMatchStart(__instance, bridge, wasReloadingRLR);

                if (wasReloadingRLR || ReplayPlaybackManager.IsSpectating)
                {
                    bridge._IsSpectatorMode_k__BackingField = true;
                    ReplayPlaybackManager.ApplySpectatorLocks(__instance);
                    MelonLogger.Msg("[REPLAY VIEWER] RLR continuation match initialized with spectator locks and bridge spectator mode active.");
                }
            }
        }
    }
}
