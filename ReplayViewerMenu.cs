using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using MelonLoader;
using UnityEngine;

namespace BTD6ReplayRecorder
{
    /// <summary>
    /// In-game modal UI for browsing, inspecting, launching, and managing recorded replays.
    /// </summary>
    public static class ReplayViewerMenu
    {
        public static bool IsOpen { get; private set; } = false;

        private static List<ReplayFileInfo> cachedReplays = new();
        private static int selectedIndex = -1;
        private static int currentPage = 0;
        private const int ItemsPerPage = 6;

        public static void Open()
        {
            IsOpen = true;
            RefreshReplayList();
            MelonLogger.Msg("[REPLAY VIEWER] Replay Viewer menu opened.");
        }

        public static void Close()
        {
            IsOpen = false;
            MelonLogger.Msg("[REPLAY VIEWER] Replay Viewer menu closed.");
        }

        public static void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }

        public static void RefreshReplayList()
        {
            try
            {
                cachedReplays = ReplayPlaybackManager.GetAvailableReplays();
                if (cachedReplays.Count > 0 && (selectedIndex < 0 || selectedIndex >= cachedReplays.Count))
                {
                    selectedIndex = 0;
                }
                else if (cachedReplays.Count == 0)
                {
                    selectedIndex = -1;
                }

                int totalPages = Math.Max(1, (int)Math.Ceiling(cachedReplays.Count / (double)ItemsPerPage));
                if (currentPage >= totalPages) currentPage = totalPages - 1;
                if (currentPage < 0) currentPage = 0;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[REPLAY VIEWER] Failed to refresh replay list: {ex.Message}");
            }
        }

        public static void OnGUI()
        {
            if (!IsOpen) return;

            try
            {
                // Screen dimensions
                float screenW = Screen.width;
                float screenH = Screen.height;

                // Main modal dialog dimensions
                float w = 780;
                float h = 530;
                float x = (screenW - w) / 2f;
                float y = (screenH - h) / 2f;

                // Main dialog window frame (uses unstripped GUI.Box)
                GUI.backgroundColor = new Color(0.10f, 0.14f, 0.20f, 0.98f);
                GUI.Box(new Rect(x, y, w, h), "");
                GUI.backgroundColor = Color.white;

                // Header Banner
                GUI.backgroundColor = new Color(0.18f, 0.52f, 0.88f);
                GUI.Box(new Rect(x + 10, y + 10, w - 20, 44), "");
                GUI.backgroundColor = Color.white;
                GUI.Label(new Rect(x + 25, y + 20, 500, 26), "<size=16><b><color=#FFFFFF>REPLAY VIEWER & SPECTATOR STUDIO</color></b></size>");

                // Close button (top right)
                GUI.backgroundColor = new Color(0.85f, 0.25f, 0.25f);
                if (GUI.Button(new Rect(x + w - 48, y + 15, 34, 34), "<b>✕</b>"))
                {
                    Close();
                }
                GUI.backgroundColor = Color.white;

                // Left Column: Replay Files List
                float listW = 390;
                float listH = h - 74;
                GUI.Box(new Rect(x + 10, y + 64, listW, listH), "");

                int totalItems = cachedReplays.Count;
                int totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)ItemsPerPage));
                if (currentPage >= totalPages) currentPage = totalPages - 1;

                GUI.Label(new Rect(x + 22, y + 72, listW - 40, 22), $"<b>Available Runs ({totalItems}):</b>");

                int startIndex = currentPage * ItemsPerPage;
                int endIndex = Math.Min(startIndex + ItemsPerPage, totalItems);

                float itemY = y + 100;
                for (int i = startIndex; i < endIndex; i++)
                {
                    var rep = cachedReplays[i];
                    bool isSelected = i == selectedIndex;

                    GUI.backgroundColor = isSelected ? new Color(0.15f, 0.65f, 0.40f) : new Color(0.20f, 0.26f, 0.36f);
                    if (GUI.Button(new Rect(x + 20, itemY, listW - 40, 52), ""))
                    {
                        selectedIndex = i;
                    }
                    GUI.backgroundColor = Color.white;

                    string mapTitle = $"<b>{rep.Map}</b> ({rep.Mode})";
                    string subText = $"{rep.Timestamp:yyyy-MM-dd HH:mm} • {rep.ActionCount} actions";

                    GUI.Label(new Rect(x + 30, itemY + 6, listW - 60, 20), mapTitle);
                    GUI.Label(new Rect(x + 30, itemY + 28, listW - 60, 18), $"<size=11><color=#BDC3C7>{subText}</color></size>");

                    itemY += 58;
                }

                if (totalItems == 0)
                {
                    GUI.Label(new Rect(x + 25, y + 120, listW - 50, 80), "<color=#BDC3C7>No .btd6replay files found in Replays folder.\n\nStart a match and select 'Yes' to record, or use 'DUMP REPLAY' in the Pause menu!</color>");
                }

                // Pagination Controls
                float pageControlsY = y + h - 56;
                GUI.backgroundColor = new Color(0.25f, 0.35f, 0.48f);
                if (GUI.Button(new Rect(x + 20, pageControlsY, 80, 28), "◀ Prev"))
                {
                    if (currentPage > 0) currentPage--;
                }

                GUI.Label(new Rect(x + 110, pageControlsY + 4, listW - 220, 24), $"<size=12><color=#FFFFFF>Page {currentPage + 1} of {totalPages}</color></size>");

                if (GUI.Button(new Rect(x + listW - 100, pageControlsY, 80, 28), "Next ▶"))
                {
                    if (currentPage < totalPages - 1) currentPage++;
                }
                GUI.backgroundColor = Color.white;

                // Right Column: Selected Replay Details & Spectate Launcher
                float rightX = x + listW + 20;
                float rightW = w - listW - 30;
                float rightH = h - 74;

                GUI.Box(new Rect(rightX, y + 64, rightW, rightH), "");

                if (selectedIndex >= 0 && selectedIndex < cachedReplays.Count)
                {
                    var rep = cachedReplays[selectedIndex];

                    GUI.Label(new Rect(rightX + 18, y + 75, rightW - 36, 25), "<size=14><color=#00D2FF><b>RUN METADATA & SPECS</b></color></size>");

                    float metaY = y + 108;
                    float spacing = 26;

                    GUI.Label(new Rect(rightX + 18, metaY, rightW - 36, 22), $"• <b>Map:</b> <color=#F1C40F>{rep.Map}</color>"); metaY += spacing;
                    GUI.Label(new Rect(rightX + 18, metaY, rightW - 36, 22), $"• <b>Difficulty:</b> {rep.Difficulty}"); metaY += spacing;
                    GUI.Label(new Rect(rightX + 18, metaY, rightW - 36, 22), $"• <b>Mode:</b> {rep.Mode}"); metaY += spacing;
                    GUI.Label(new Rect(rightX + 18, metaY, rightW - 36, 22), $"• <b>Hero:</b> {rep.Hero}"); metaY += spacing;
                    GUI.Label(new Rect(rightX + 18, metaY, rightW - 36, 22), $"• <b>Actions Recorded:</b> <color=#2ECC71>{rep.ActionCount}</color>"); metaY += spacing;
                    GUI.Label(new Rect(rightX + 18, metaY, rightW - 36, 22), $"• <b>RNG Seed:</b> {rep.Seed}"); metaY += spacing;
                    GUI.Label(new Rect(rightX + 18, metaY, rightW - 36, 22), $"• <b>Recorded At:</b> {rep.Timestamp:yyyy-MM-dd HH:mm:ss}"); metaY += spacing;
                    GUI.Label(new Rect(rightX + 18, metaY, rightW - 36, 22), $"• <b>File Size:</b> {rep.FileSizeBytes / 1024} KB"); metaY += spacing;

                    // Open Folder Button
                    GUI.backgroundColor = new Color(0.35f, 0.45f, 0.55f);
                    if (GUI.Button(new Rect(rightX + 18, y + h - 110, rightW - 36, 30), "📁 Open Replays Folder in Explorer"))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = ReplayManager.ReplayDirectory,
                                UseShellExecute = true
                            });
                        }
                        catch (Exception ex)
                        {
                            MelonLogger.Error($"[REPLAY VIEWER] Could not open folder: {ex.Message}");
                        }
                    }

                    // Prominent Green Spin Up & Spectate Run Button
                    GUI.backgroundColor = new Color(0.15f, 0.75f, 0.35f);
                    if (GUI.Button(new Rect(rightX + 18, y + h - 72, rightW - 36, 48), "<size=15><b>▶ SPIN UP & SPECTATE RUN</b></size>"))
                    {
                        MelonLogger.Msg($"[REPLAY VIEWER] Spinning up replay file: {rep.FileName}");
                        Close();
                        ReplayPlaybackManager.LaunchReplayFile(rep.FullPath);
                    }
                    GUI.backgroundColor = Color.white;
                }
                else
                {
                    GUI.Label(new Rect(rightX + 20, y + 160, rightW - 40, 80), "<color=#BDC3C7>Select a replay run on the left to inspect metadata or launch spectator playback.</color>");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[REPLAY VIEWER] Error in OnGUI: {ex.Message}");
            }
        }
    }
}
