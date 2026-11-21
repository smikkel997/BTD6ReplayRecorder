using System;
using MelonLoader;
using UnityEngine;
using BTD_Mod_Helper;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppAssets.Scripts.Unity.UI_New.Pause;

[assembly: MelonInfo(typeof(BTD6ReplayRecorder.ReplayRecorderMod), "BTD6 Replay Recorder", "1.0.0", "Smikkel997")]
[assembly: MelonGame("Ninja Kiwi", "BloonsTD6")]
[assembly: MelonAuthorColor(255, 66, 135, 245)]

namespace BTD6ReplayRecorder
{
    /// <summary>
    /// Entry point for the BTD6 Replay Recorder mod.
    /// Handles mod initialization, lifecycle hooks, and global update loops.
    /// </summary>
    public class ReplayRecorderMod : BloonsTD6Mod
    {
        /// <summary>
        /// Invoked when the application starts. Initializes the replay subsystem.
        /// </summary>
        public override void OnApplicationStart()
        {
            MelonLogger.Msg("==================================================");
            MelonLogger.Msg("[BTD6 REPLAY RECORDER] v1.0.0 Initialized");
            MelonLogger.Msg("[BTD6 REPLAY RECORDER] Native In-Game Verification & Playback");
            MelonLogger.Msg("==================================================");

            ReplayManager.Initialize();
        }

        public override void OnInGameLoaded(InGame inGame)
        {
            string map = InGameData.Editable?.selectedMap ?? "Unknown";
            MelonLogger.Msg($"[BTD6 REPLAY RECORDER] Match loaded: Map={map}");
        }

        public override void OnPauseScreenOpened(PauseScreen pauseScreen)
        {
            // Attachment is handled uniquely by Harmony postfix PauseScreen_Open_Patch
        }

        public override void OnVictory()
        {
            MelonLogger.Msg("[BTD6 REPLAY RECORDER] Victory event detected!");
        }

        public override void OnMainMenu()
        {
            // Reset any leftover spectator state on return to main menu (unless actively reloading for RLR)
            if (ReplayPlaybackManager.IsSpectating && !ReplayPlaybackManager.IsReloadingForRLR && ReplayPlaybackManager.PendingReloadSave == null && ReplayPlaybackManager.PendingReplay == null)
            {
                ReplayPlaybackManager.StopSpectatorMode(null);
            }
        }

        public override void OnUpdate()
        {
            // Check if RLR reload is pending from main menu
            if (ReplayPlaybackManager.IsReloadingForRLR && InGame.instance == null && ReplayPlaybackManager.PendingReloadSave != null)
            {
                ReplayPlaybackManager.TriggerMapReload();
            }

            // Monitor spectator playback to auto-pause when replay rounds finish
            if (ReplayPlaybackManager.IsSpectating && InGame.instance != null)
            {
                ReplayPlaybackManager.UpdatePlayback(InGame.instance);
            }

            // Hotkey F8 to toggle Replay Viewer anytime from menus
            if (Input.GetKeyDown(KeyCode.F8))
            {
                ReplayViewerMenu.Toggle();
            }

            // Close Replay Viewer on Escape if it is open
            if (ReplayViewerMenu.IsOpen && Input.GetKeyDown(KeyCode.Escape))
            {
                ReplayViewerMenu.Close();
            }
        }

        public override void OnGUI()
        {
            // Draw modal Replay Viewer dialog if open
            ReplayViewerMenu.OnGUI();

            // Draw floating spectator controls if actively spectating a replay
            ReplayPlaybackManager.DrawSpectatorHUD(InGame.instance);
        }
    }
}
