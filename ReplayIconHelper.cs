using System;
using System.IO;
using System.Reflection;
using MelonLoader;
using UnityEngine;
using BTD_Mod_Helper.Api;
using BTD_Mod_Helper.Api.Enums;

namespace BTD6ReplayRecorder
{
    public static class ReplayIconHelper
    {
        private static Sprite? _cachedTvSprite;
        private static Sprite? _cachedTvButtonSprite;

        public static Sprite? GetTvReplaySprite()
        {
            if (_cachedTvSprite != null)
            {
                return _cachedTvSprite;
            }

            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string resourceName = "BTD6ReplayRecorder.Resources.ReplayTvIcon.png";
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    byte[] pngBytes = ms.ToArray();

                    var tex = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                    if (ImageConversion.LoadImage(tex, pngBytes))
                    {
                        tex.name = "ReplayTvIconTexture";
                        _cachedTvSprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                        _cachedTvSprite.name = "ReplayTvIconSprite";
                        MelonLogger.Msg("[REPLAY RECORDER] Custom TV icon loaded and initialized successfully.");
                        return _cachedTvSprite;
                    }
                }
                else
                {
                    MelonLogger.Warning($"[REPLAY RECORDER] Manifest resource stream '{resourceName}' was null.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[REPLAY RECORDER] Failed to load custom TV icon: {ex.Message}");
            }

            // Return null on failure so caller can fall back to vanilla sprite
            return null;
        }

        public static Sprite? GetTvReplayButtonSprite()
        {
            if (_cachedTvButtonSprite != null)
            {
                return _cachedTvButtonSprite;
            }

            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string resourceName = "BTD6ReplayRecorder.Resources.ReplayTvButton.png";
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    byte[] pngBytes = ms.ToArray();

                    var tex = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                    if (ImageConversion.LoadImage(tex, pngBytes))
                    {
                        tex.name = "ReplayTvButtonTexture";
                        _cachedTvButtonSprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                        _cachedTvButtonSprite.name = "ReplayTvButtonSprite";
                        MelonLogger.Msg("[REPLAY RECORDER] Custom composite TV replay button sprite loaded and initialized successfully.");
                        return _cachedTvButtonSprite;
                    }
                }
                else
                {
                    MelonLogger.Warning($"[REPLAY RECORDER] Manifest resource stream '{resourceName}' was null.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[REPLAY RECORDER] Failed to load custom TV button sprite: {ex.Message}");
            }

            return null;
        }
    }
}
