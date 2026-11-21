using System;
using HarmonyLib;
using MelonLoader;
using Il2CppAssets.Scripts;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;

namespace BTD6ReplayRecorder.Patches
{
    [HarmonyPatch(typeof(UnityToSimulation), nameof(UnityToSimulation.ResumeFromCheckpointAction_Impl))]
    public static class ResumeFromCheckpointAction_Impl_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(UnityToSimulation __instance, ObjectId syncedObjectId, bool doFakeLoad, int mmCost, bool isRetryLastRound)
        {
            if (__instance == null || __instance.simulation == null) return false;

            var sim = __instance.simulation;
            bool isSpectator = ReplayPlaybackManager.IsSpectating || __instance.IsSpectatorMode || ReplayPlaybackManager.PendingReplay != null;

            if (isSpectator)
            {
                var replay = ReplayPlaybackManager.ActiveReplay ?? ReplayPlaybackManager.PendingReplay;
                int seed = replay != null ? replay.randomSeed : (sim.model != null ? sim.model.randomSeed : 0);

                // 1. Properly seed syncedRandom and unsyncedRandom to the replay's random seed
                if (seed != 0)
                {
                    sim.syncedRandom = new Il2CppNinjaKiwi.Common.BufferedRandom(seed);
                    sim.unsyncedRandom = new Il2CppNinjaKiwi.Common.BufferedRandom(seed);
                    if (sim.model != null) sim.model.randomSeed = seed;
                }

                // 2. Correctly set the starting syncedObjectId recorded in the replay (e.g. 414)
                sim.syncedObjectId = syncedObjectId;

                MelonLogger.Msg($"[REPLAY VIEWER] Checkpoint synchronized! SyncedObjectId={syncedObjectId.data}, Seed={seed}");

                // Return false to BYPASS the broken native CreateMapSaveData call that throws NullReferenceException!
                return false;
            }

            // Even outside spectator mode, bypass broken native CreateMapSaveData to eliminate fatal NRE
            try
            {
                sim.syncedObjectId = syncedObjectId;
            }
            catch { }

            return false;
        }
    }

    [HarmonyPatch(typeof(UnityToSimulation), nameof(UnityToSimulation.ReplayMarker_Impl))]
    public static class ReplayMarker_Impl_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(UnityToSimulation __instance, int syncedRNGNext, ObjectId syncedObjectId)
        {
            if (__instance == null || __instance.simulation == null) return false;

            // If actively recording a run, let native logic run normally
            if (__instance.ReplayRecorder != null) return true;

            // Compare RNG and ObjectId for divergence
            var sim = __instance.simulation;
            int actualRNG = 0;
            try
            {
                if (sim.syncedRandom != null)
                {
                    actualRNG = sim.syncedRandom.next;
                }
            }
            catch { }

            uint actualObjId = sim.syncedObjectId.data;
            bool rngDiverged = (syncedRNGNext != actualRNG);
            bool objDiverged = (syncedObjectId.data != actualObjId);

            if (rngDiverged || objDiverged)
            {
                string reason = rngDiverged
                    ? $"Synced Random has diverged! (replay: {syncedRNGNext}, actual: {actualRNG})"
                    : $"Synced ObjectId has diverged! (replay: {syncedObjectId.data}, actual: {actualObjId})";

                int step = __instance.ElapsedTime;
                MelonLogger.Warning($"[REPLAY VIEWER] Divergence detected @ step {step}: {reason}");

                if (ReplayPlaybackManager.IsSpectating)
                {
                    ReplayPlaybackManager.OnReplayDiverged(step, reason);
                }
            }

            // Return false to SUPPRESS the native UnityToSimulationException!
            // This prevents the game simulation loop from crashing while warning the user.
            return false;
        }
    }

    [HarmonyPatch(typeof(UnityToSimulation), nameof(UnityToSimulation.UpgradeTower_Impl))]
    public static class UpgradeTower_Impl_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(UnityToSimulation __instance, ObjectId id, int pathIndex, int callbackId, int inputId, double nonUpgradeCashInvestment)
        {
            if (__instance == null || __instance.simulation == null) return true;

            bool isSpectator = ReplayPlaybackManager.IsSpectating || __instance.IsSpectatorMode || ReplayPlaybackManager.PendingReplay != null;
            if (!isSpectator) return true;

            var sim = __instance.simulation;
            var tm = sim.towerManager;
            if (tm == null) return true;

            var tower = tm.GetTowerById(id);
            if (tower == null)
            {
                MelonLogger.Warning($"[REPLAY VIEWER] Suppressed UpgradeTower_Impl: Tower ID {id.data} not found in simulation during replay playback.");
                return false;
            }

            var model = tower.towerModel;
            if (model == null || model.tiers == null) return true;

            if (pathIndex < 0 || pathIndex >= model.tiers.Length)
            {
                MelonLogger.Warning($"[REPLAY VIEWER] Suppressed UpgradeTower_Impl: Invalid pathIndex {pathIndex} on Tower ID {id.data}.");
                return false;
            }

            int t0 = model.tiers[0] + (pathIndex == 0 ? 1 : 0);
            int t1 = model.tiers[1] + (pathIndex == 1 ? 1 : 0);
            int t2 = model.tiers[2] + (pathIndex == 2 ? 1 : 0);

            var gameModel = sim.model;
            if (gameModel != null)
            {
                var targetModel = gameModel.GetTower(model.baseId, t0, t1, t2);
                if (targetModel == null)
                {
                    MelonLogger.Warning($"[REPLAY VIEWER] Suppressed redundant/invalid UpgradeTower_Impl for Tower ID {id.data} ({model.baseId} -> {t0}-{t1}-{t2}) during replay playback.");
                    return false;
                }
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(UnityToSimulation), nameof(UnityToSimulation.SellTower_Impl))]
    public static class SellTower_Impl_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(UnityToSimulation __instance, ObjectId id, int inputId)
        {
            if (__instance == null || __instance.simulation == null) return true;

            bool isSpectator = ReplayPlaybackManager.IsSpectating || __instance.IsSpectatorMode || ReplayPlaybackManager.PendingReplay != null;
            if (!isSpectator) return true;

            if (__instance.simulation.towerManager?.GetTowerById(id) == null)
            {
                MelonLogger.Warning($"[REPLAY VIEWER] Suppressed SellTower_Impl: Tower ID {id.data} not found during replay playback.");
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(UnityToSimulation), nameof(UnityToSimulation.SetNextTowerTargetType_Impl))]
    public static class SetNextTowerTargetType_Impl_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(UnityToSimulation __instance, ObjectId id, bool prev)
        {
            if (__instance == null || __instance.simulation == null) return true;

            bool isSpectator = ReplayPlaybackManager.IsSpectating || __instance.IsSpectatorMode || ReplayPlaybackManager.PendingReplay != null;
            if (!isSpectator) return true;

            if (__instance.simulation.towerManager?.GetTowerById(id) == null)
            {
                MelonLogger.Warning($"[REPLAY VIEWER] Suppressed SetNextTowerTargetType_Impl: Tower ID {id.data} not found during replay playback.");
                return false;
            }

            return true;
        }
    }
}
