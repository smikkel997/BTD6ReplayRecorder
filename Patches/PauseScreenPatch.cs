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
using Il2CppAssets.Scripts.Unity.UI_New.Pause;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppAssets.Scripts.Unity.UI_New.Popups;

namespace BTD6ReplayRecorder.Patches
{
    [HarmonyPatch(typeof(PauseScreen), nameof(PauseScreen.Open))]
    public static class PauseScreen_Open_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(PauseScreen __instance)
        {
            AttachDumpButton(__instance);
        }

        [HarmonyFinalizer]
        public static Exception? Finalizer(Exception? __exception)
        {
            if (__exception != null)
            {
                // Native PauseScreen.Open throws a non-fatal NullReferenceException in certain game modes
                // (e.g. tutorial / spectator where Rogue/CT panels or continuous play toggles are uninitialized).
                // Suppressing it prevents the red IL2CPP stack trace from dirtying the console.
                return null;
            }
            return null;
        }

        public static void AttachDumpButton(PauseScreen pauseScreen)
        {
            if (pauseScreen == null || pauseScreen.gameObject == null) return;

            // Only show dump button if this run is being recorded or has actions captured
            var inGame = InGame.instance;
            var bridge = inGame?.bridge;
            bool hasRecorder = bridge?.ReplayRecorder != null;
            int actionCount = (hasRecorder && bridge?.ReplayRecorder?.actions != null) ? bridge.ReplayRecorder.actions.Count : 0;

            if (!ReplayManager.IsCurrentMatchRecorded && !hasRecorder)
            {
                // Not recording: if a previous button exists, ensure it is hidden
                foreach (var t in pauseScreen.GetComponentsInChildren<Transform>(true))
                {
                    if (t != null && t.name == "DumpReplayBtn")
                    {
                        t.gameObject.SetActive(false);
                    }
                }
                return;
            }

            try
            {
                // Strict deep child deduplication check across all descendants
                GameObject? existingBtn = null;
                foreach (var t in pauseScreen.GetComponentsInChildren<Transform>(true))
                {
                    if (t != null && t.name == "DumpReplayBtn")
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

                // Reference template: prefer homeButton (blue button), fallback to restartButton
                Button? templateBtn = pauseScreen.homeButton;
                if (templateBtn == null || templateBtn.gameObject == null)
                {
                    templateBtn = pauseScreen.restartButton;
                }

                if (templateBtn == null || templateBtn.gameObject == null) return;

                // Instantiate single clone inside the same parent container
                var dumpObj = UnityEngine.Object.Instantiate(templateBtn.gameObject, templateBtn.transform.parent);
                dumpObj.name = "DumpReplayBtn";
                dumpObj.SetActive(true);

                // Disable auto-localization so BTD6 does not overwrite text back to "Home" or "Restart"
                foreach (var nk in dumpObj.GetComponentsInChildren<NK_TextMeshProUGUI>(true))
                {
                    nk.AutoLocalize = false;
                    nk.localizeKey = "";
                    nk.SetText("DUMP REPLAY");
                    nk.text = "DUMP REPLAY";
                }
                foreach (var tmp in dumpObj.GetComponentsInChildren<TextMeshProUGUI>(true))
                {
                    tmp.text = "DUMP REPLAY";
                }

                // Apply TV icon
                ApplyTvIcon(dumpObj);

                // Layout positioning: if parent has a LayoutGroup, let layout place it next to templateBtn
                var parentLayout = templateBtn.transform.parent.GetComponent<LayoutGroup>();
                if (parentLayout != null)
                {
                    dumpObj.transform.SetSiblingIndex(templateBtn.transform.GetSiblingIndex() + 1);
                }
                else
                {
                    var rect = dumpObj.GetComponent<RectTransform>();
                    var origRect = templateBtn.GetComponent<RectTransform>();
                    if (rect != null && origRect != null)
                    {
                        rect.anchoredPosition = new Vector2(origRect.anchoredPosition.x, origRect.anchoredPosition.y - 120);
                    }
                }

                // Wire up onClick
                var btn = dumpObj.GetComponent<Button>();
                if (btn != null)
                {
                    btn.onClick.RemoveAllListeners();
                    btn.onClick.AddListener(new System.Action(() =>
                    {
                        MelonLogger.Msg("[REPLAY RECORDER] Dump Replay clicked from InGame Pause menu.");
                        var result = ReplayManager.DumpCurrentMatchRecording("pause_screen");
                        if (result.success)
                        {
                            int acts = InGame.instance?.bridge?.ReplayRecorder?.actions?.Count ?? 0;
                            ReplayManager.ShowExportNotification(result.filename, acts);
                        }
                        else
                        {
                            PopupScreen.instance?.ShowOkPopup($"Export Failed:\n{result.message}", null);
                        }
                    }));
                }

                MelonLogger.Msg("[REPLAY RECORDER] Successfully attached single styled Dump Replay button with TV icon to PauseScreen.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[REPLAY RECORDER] Failed to attach dump button to PauseScreen: {ex.Message}");
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
                    MelonLogger.Warning("[REPLAY RECORDER] Could not load TV sprite for Dump button.");
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
                MelonLogger.Warning($"[REPLAY RECORDER] Error applying TV icon to button: {ex.Message}");
            }
        }
    }
}
