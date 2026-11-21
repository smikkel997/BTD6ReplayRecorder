using System;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using Il2Cpp;
using Il2CppTMPro;
using BTD_Mod_Helper.Api;
using BTD_Mod_Helper.Api.Enums;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Unity.UI_New.GameOver;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppAssets.Scripts.Unity.UI_New.Popups;

namespace BTD6ReplayRecorder.Patches
{
    [HarmonyPatch(typeof(SummaryScreen), nameof(SummaryScreen.Open))]
    public static class SummaryScreen_Open_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(SummaryScreen __instance)
        {
            AttachVictoryDumpButton(__instance);
        }

        public static void AttachVictoryDumpButton(SummaryScreen summaryScreen)
        {
            if (summaryScreen == null || summaryScreen.gameObject == null) return;

            var inGame = InGame.instance;
            var bridge = inGame?.bridge;
            bool hasRecorder = bridge?.ReplayRecorder != null;
            int actionCount = (hasRecorder && bridge?.ReplayRecorder?.actions != null) ? bridge.ReplayRecorder.actions.Count : 0;

            // Only display dump button if this match was recorded or has recorded actions
            if (!ReplayManager.IsCurrentMatchRecorded && !hasRecorder)
            {
                foreach (var t in summaryScreen.GetComponentsInChildren<Transform>(true))
                {
                    if (t != null && t.name == "VictoryDumpReplayBtn")
                    {
                        t.gameObject.SetActive(false);
                    }
                }
                return;
            }

            try
            {
                // Strict deduplication check across all children
                GameObject? existingBtn = null;
                foreach (var t in summaryScreen.GetComponentsInChildren<Transform>(true))
                {
                    if (t != null && t.name == "VictoryDumpReplayBtn")
                    {
                        existingBtn = t.gameObject;
                        break;
                    }
                }

                if (existingBtn != null)
                {
                    existingBtn.SetActive(true);
                    ApplyTvIcon(existingBtn);
                    return;
                }

                // Choose template button: homeBtn (blue secondary action button)
                Button? templateBtn = summaryScreen.homeBtn;
                if (templateBtn == null && summaryScreen is VictoryScreen vicScreen)
                {
                    templateBtn = vicScreen.freePlayBtn;
                }

                if (templateBtn == null || templateBtn.gameObject == null) return;

                // Instantiate duplicate button inside the same parent container
                var dumpObj = UnityEngine.Object.Instantiate(templateBtn.gameObject, templateBtn.transform.parent);
                dumpObj.name = "VictoryDumpReplayBtn";
                dumpObj.SetActive(true);

                // Disable auto-localization so BTD6 doesn't overwrite our text with "Home"
                foreach (var nk in dumpObj.GetComponentsInChildren<NK_TextMeshProUGUI>(true))
                {
                    nk.AutoLocalize = false;
                    nk.localizeKey = "";
                    nk.SetText("SAVE REPLAY");
                }
                foreach (var tmp in dumpObj.GetComponentsInChildren<TextMeshProUGUI>(true))
                {
                    tmp.text = "SAVE REPLAY";
                }

                // Apply TV icon
                ApplyTvIcon(dumpObj);

                // Set button onClick listener
                var btn = dumpObj.GetComponent<Button>();
                if (btn != null)
                {
                    btn.onClick.RemoveAllListeners();
                    btn.onClick.AddListener(new System.Action(() =>
                    {
                        MelonLogger.Msg("[REPLAY RECORDER] Save Replay clicked on Victory/Summary screen.");
                        var result = ReplayManager.DumpCurrentMatchRecording("victory_screen");
                        if (result.success)
                        {
                            int acts = InGame.instance?.bridge?.ReplayRecorder?.actions?.Count ?? 0;
                            ReplayManager.ShowExportNotification(result.filename, acts);

                            // Clean up this completed run from the pending ledger
                            var readonlyData = InGameData.Editable;
                            ReplayManager.ClearRunRecord(readonlyData?.selectedMap, readonlyData?.selectedDifficulty, readonlyData?.selectedMode);
                        }
                        else
                        {
                            PopupScreen.instance?.ShowOkPopup($"Export Failed:\n{result.message}", null);
                        }
                    }));
                }

                MelonLogger.Msg($"[REPLAY RECORDER] Successfully attached styled Save Replay button with TV icon to {summaryScreen.GetIl2CppType().Name}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[REPLAY RECORDER] Failed to attach victory dump button: {ex.Message}");
            }
        }

        public static void ApplyTvIcon(GameObject buttonObj)
        {
            try
            {
                // Prioritize the pre-rendered composite sprite (identical structure to HomeBtn / RestartBtn)
                var compositeSprite = ReplayIconHelper.GetTvReplayButtonSprite();
                var rootImg = buttonObj.GetComponent<Image>();

                if (compositeSprite != null && rootImg != null)
                {
                    rootImg.sprite = compositeSprite;
                    rootImg.overrideSprite = compositeSprite;
                    rootImg.type = Image.Type.Simple;

                    // Deactivate any legacy child icon object
                    Transform? oldIcon = buttonObj.transform.Find("Icon");
                    if (oldIcon != null)
                    {
                        oldIcon.gameObject.SetActive(false);
                    }

                    MelonLogger.Msg($"[REPLAY RECORDER] Successfully applied composite ReplayTvButton sprite to {buttonObj.name}.");
                    return;
                }

                // Fallback to separate background + child icon if composite sprite fails
                var tvSprite = ReplayIconHelper.GetTvReplaySprite();
                if (tvSprite == null)
                {
                    MelonLogger.Warning("[REPLAY RECORDER] Could not load TV sprite for Victory Dump button.");
                    return;
                }

                if (rootImg != null)
                {
                    rootImg.overrideSprite = null;
                    rootImg.SetSprite(VanillaSprites.BlueBtnSquare);
                    rootImg.type = Image.Type.Simple;
                }

                Transform? iconT = buttonObj.transform.Find("Icon");
                GameObject iconObj;
                if (iconT != null)
                {
                    iconObj = iconT.gameObject;
                }
                else
                {
                    iconObj = new GameObject("Icon");
                    iconObj.transform.SetParent(buttonObj.transform, false);
                }

                iconObj.SetActive(true);

                var iconImg = iconObj.GetComponent<Image>();
                if (iconImg == null)
                {
                    iconImg = iconObj.AddComponent<Image>();
                }

                iconImg.sprite = tvSprite;
                iconImg.overrideSprite = tvSprite;
                iconImg.type = Image.Type.Simple;
                iconImg.preserveAspect = true;
                iconImg.raycastTarget = false;

                var rect = iconObj.GetComponent<RectTransform>();
                if (rect != null)
                {
                    rect.anchorMin = new Vector2(0.18f, 0.18f);
                    rect.anchorMax = new Vector2(0.82f, 0.82f);
                    rect.offsetMin = Vector2.zero;
                    rect.offsetMax = Vector2.zero;
                }

                MelonLogger.Msg($"[REPLAY RECORDER] Applied fallback BlueBtnSquare background and TV icon to {buttonObj.name}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[REPLAY RECORDER] Error applying TV icon to victory button: {ex.Message}");
            }
        }
    }
}
