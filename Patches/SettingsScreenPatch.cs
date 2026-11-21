using System;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using Il2Cpp;
using Il2CppTMPro;
using Il2CppAssets.Scripts.Unity.UI_New.Settings;

namespace BTD6ReplayRecorder.Patches
{
    [HarmonyPatch(typeof(SettingsScreen), nameof(SettingsScreen.Open))]
    public static class SettingsScreen_Open_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(SettingsScreen __instance)
        {
            AttachReplayStudioButton(__instance);
        }

        public static void AttachReplayStudioButton(SettingsScreen settingsScreen)
        {
            if (settingsScreen == null || settingsScreen.gameObject == null) return;

            try
            {
                // Strict deep child deduplication
                foreach (var t in settingsScreen.GetComponentsInChildren<Transform>(true))
                {
                    if (t != null && t.name == "ReplayStudioBtn")
                    {
                        t.gameObject.SetActive(true);
                        return;
                    }
                }

                // Reference template: find extraSettingsBtn, jukeBoxBtn, hotkeysBtn, or any active button
                Button? template = null;
                var btns = settingsScreen.GetComponentsInChildren<Button>(true);
                foreach (var b in btns)
                {
                    if (b != null && (b.name.Contains("Extra") || b.name.Contains("Juke") || b.name.Contains("Hotkeys") || b.name.Contains("Language")))
                    {
                        template = b;
                        break;
                    }
                }

                if (template == null && btns.Length > 0)
                {
                    template = btns[0];
                }

                if (template == null || template.gameObject == null) return;

                // Clone template button
                var studioObj = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
                studioObj.name = "ReplayStudioBtn";
                studioObj.SetActive(true);

                // Update text and suppress auto-localization overwrite
                foreach (var nk in studioObj.GetComponentsInChildren<NK_TextMeshProUGUI>(true))
                {
                    nk.AutoLocalize = false;
                    nk.localizeKey = "";
                    nk.SetText("REPLAYS");
                    nk.text = "REPLAYS";
                }
                foreach (var tmp in studioObj.GetComponentsInChildren<TextMeshProUGUI>(true))
                {
                    tmp.text = "REPLAYS";
                }
                foreach (var txt in studioObj.GetComponentsInChildren<Text>(true))
                {
                    txt.text = "REPLAYS";
                }

                // Update child icon to custom TV Replay sprite if present
                var tvSprite = ReplayIconHelper.GetTvReplaySprite();
                if (tvSprite != null)
                {
                    var images = studioObj.GetComponentsInChildren<Image>(true);
                    foreach (var img in images)
                    {
                        if (img != null && img.gameObject != studioObj)
                        {
                            img.sprite = tvSprite;
                            img.overrideSprite = tvSprite;
                        }
                    }
                }

                // Sibling order in layout
                studioObj.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);

                // Wire up click event
                var btn = studioObj.GetComponent<Button>();
                if (btn != null)
                {
                    btn.onClick.RemoveAllListeners();
                    btn.onClick.AddListener(new System.Action(() =>
                    {
                        MelonLogger.Msg("[REPLAY RECORDER] Replays button clicked from Settings screen.");
                        ReplayViewerMenu.Toggle();
                    }));
                }

                MelonLogger.Msg("[REPLAY RECORDER] Attached native-styled 'REPLAYS' button to SettingsScreen.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[REPLAY RECORDER] Failed to attach button to SettingsScreen: {ex.Message}");
            }
        }
    }
}
