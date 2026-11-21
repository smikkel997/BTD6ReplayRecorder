using System;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using Il2Cpp;
using Il2CppTMPro;
using Il2CppAssets.Scripts.Unity.UI_New;
using Il2CppAssets.Scripts.Unity.UI_New.Main.MapSelect;
using Il2CppAssets.Scripts.Unity.UI_New.Main.Facebook;

namespace BTD6ReplayRecorder.Patches
{
    [HarmonyPatch(typeof(MapSelectScreen), nameof(MapSelectScreen.Open))]
    public static class MapSelectScreen_Open_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(MapSelectScreen __instance)
        {
            AttachMapSelectReplayButton(__instance);
        }

        public static void AttachMapSelectReplayButton(MapSelectScreen? mapScreen)
        {
            try
            {
                var cfs = CommonForegroundScreen.instance;
                if (cfs == null) return;

                // Locate the Friends button on CommonForegroundScreen
                var friendLogin = cfs.GetComponentInChildren<FriendLoginButton>(true);
                if (friendLogin == null || friendLogin.gameObject == null)
                {
                    MelonLogger.Warning("[REPLAY RECORDER] FriendLoginButton not found on CommonForegroundScreen.");
                    return;
                }

                var parent = friendLogin.transform.parent;
                if (parent == null) return;

                // Strict deduplication check
                foreach (var t in parent.GetComponentsInChildren<Transform>(true))
                {
                    if (t != null && t.name == "MapSelectReplayStudioBtn")
                    {
                        t.gameObject.SetActive(true);
                        return;
                    }
                }

                // Clone the Friends button container (gives identical size, rounded corners, border, and text layout)
                var replayObj = UnityEngine.Object.Instantiate(friendLogin.gameObject, parent);
                replayObj.name = "MapSelectReplayStudioBtn";
                replayObj.SetActive(true);

                // Remove FriendLoginButton script from clone so it doesn't run social logic
                var flb = replayObj.GetComponent<FriendLoginButton>();
                if (flb != null)
                {
                    UnityEngine.Object.Destroy(flb);
                }

                // Hide any notification pips on the clone
                foreach (var t in replayObj.GetComponentsInChildren<Transform>(true))
                {
                    if (t != null && (t.name.ToLower().Contains("pip") || t.name.ToLower().Contains("alert")))
                    {
                        t.gameObject.SetActive(false);
                    }
                }

                // Position directly below the Friends button (Row 1, matching the Search button on the left)
                var friendsRect = friendLogin.GetComponent<RectTransform>();
                var replayRect = replayObj.GetComponent<RectTransform>();
                if (friendsRect != null && replayRect != null)
                {
                    replayRect.anchorMin = friendsRect.anchorMin;
                    replayRect.anchorMax = friendsRect.anchorMax;
                    replayRect.pivot = friendsRect.pivot;
                    replayRect.sizeDelta = friendsRect.sizeDelta;
                    // Y offset: 250 units below Friends button (comfortably clears the "FRIENDS" text label and places the button inside the designated circle)
                    replayRect.anchoredPosition = new Vector2(friendsRect.anchoredPosition.x, friendsRect.anchoredPosition.y - 250f);
                }

                // Replace child icon with custom TV Replay sprite
                var tvSprite = ReplayIconHelper.GetTvReplaySprite();
                if (tvSprite != null)
                {
                    var images = replayObj.GetComponentsInChildren<Image>(true);
                    foreach (var img in images)
                    {
                        if (img != null && img.gameObject != replayObj)
                        {
                            img.sprite = tvSprite;
                            img.overrideSprite = tvSprite;
                        }
                    }
                }

                // Update text label to "REPLAYS" and suppress auto-localization overwrite
                foreach (var nk in replayObj.GetComponentsInChildren<NK_TextMeshProUGUI>(true))
                {
                    nk.AutoLocalize = false;
                    nk.localizeKey = "";
                    nk.SetText("REPLAYS");
                    nk.text = "REPLAYS";
                }
                foreach (var tmp in replayObj.GetComponentsInChildren<TextMeshProUGUI>(true))
                {
                    tmp.text = "REPLAYS";
                }
                foreach (var txt in replayObj.GetComponentsInChildren<Text>(true))
                {
                    txt.text = "REPLAYS";
                }

                // Wire up button onClick to open the Replay Viewer
                var btn = replayObj.GetComponent<Button>();
                if (btn != null)
                {
                    btn.onClick.RemoveAllListeners();
                    btn.onClick.AddListener(new System.Action(() =>
                    {
                        MelonLogger.Msg("[REPLAY RECORDER] Replays button clicked on MapSelectScreen.");
                        ReplayViewerMenu.Toggle();
                    }));
                }

                MelonLogger.Msg("[REPLAY RECORDER] Successfully attached custom-styled TV Replays button to MapSelectScreen.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[REPLAY RECORDER] Failed to attach button to MapSelectScreen: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(MapSelectScreen), nameof(MapSelectScreen.Close))]
    public static class MapSelectScreen_Close_Patch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            try
            {
                var cfs = CommonForegroundScreen.instance;
                if (cfs != null)
                {
                    foreach (var t in cfs.GetComponentsInChildren<Transform>(true))
                    {
                        if (t != null && t.name == "MapSelectReplayStudioBtn")
                        {
                            t.gameObject.SetActive(false);
                        }
                    }
                }
            }
            catch {}
        }
    }
}
