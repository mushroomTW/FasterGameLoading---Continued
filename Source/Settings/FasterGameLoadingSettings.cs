using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    public class FasterGameLoadingSettings : ModSettings
    {
        public static Dictionary<string, string> loadedTexturesSinceLastSession = new Dictionary<string, string>();
        public static Dictionary<string, ModContentPack> modsByPackageIds = new Dictionary<string, ModContentPack>();
        public static Dictionary<string, string> loadedTypesByFullNameSinceLastSession = new Dictionary<string, string>();
        public static List<string> modsInLastSession = new List<string>();
        public static Dictionary<string, bool> xmlPathsSinceLastSession = new Dictionary<string, bool>();
        public static bool delayGraphicLoading = false;
        public static bool earlyModContentLoading = true;
        public static bool StaticAtlasesBaking = true;
        public static bool atlasCaching = false;
        public static List<float> historicalBakeSpeeds = new List<float>();
        public const int HISTORY_SIZE = 5;
        public static readonly float[] WEIGHTS = { 0.4f, 0.3f, 0.2f, 0.1f };
        public static ModContentPack GetModContent(string packageId)
        {
            var packageLower = packageId.ToLower();
            if (!modsByPackageIds.TryGetValue(packageLower, out var mod))
            {
                modsByPackageIds[packageLower] = mod = LoadedModManager.RunningModsListForReading.FirstOrDefault(x =>
                    x.PackageIdPlayerFacing.ToLower() == packageLower);
            }
            return mod;
        }

        public static void DoSettingsWindowContents(Rect inRect)
        {
            var ls = new Listing_Standard();
            ls.Begin(inRect);
            ls.CheckboxLabeled("FGL_EarlyModContentLoading".Translate(), ref earlyModContentLoading);
            ls.CheckboxLabeled("FGL_DelayGraphicLoading".Translate(), ref delayGraphicLoading);
            ls.CheckboxLabeled("FGL_StaticAtlasesBaking".Translate(), ref StaticAtlasesBaking);
            ls.CheckboxLabeled("FGL_AtlasCaching".Translate(), ref atlasCaching);
            ls.Gap(12f);

            // 紋理縮放說明文字
            var explanationText = "FGL_TextureResizingExplanation".Translate();
            var textHeight = Text.CalcHeight(explanationText, ls.ColumnWidth);
            var explanationRect = ls.GetRect(textHeight + 8f);
            Widgets.Label(explanationRect, explanationText);

            // 紋理縮放按鈕
            ls.Gap(4f);
            if (ls.ButtonText("FGL_DownscaleTextures".Translate()))
            {
                Find.WindowStack.Add(new Dialog_MessageBox("FGL_TextureResizingConfirmation".Translate(), "Confirm".Translate(), delegate
                {
                    TextureResize.DoTextureResizing();
                }, "GoBack".Translate()));
            }

            // 還原紋理按鈕（清除快取）+ 狀態顯示
            ls.Gap(4f);
            var cacheCount = TextureResize.CacheCount;
            var cacheStatusText = cacheCount > 0
                ? "FGL_TextureCacheStatus_Active".Translate(cacheCount)
                : "FGL_TextureCacheStatus_Empty".Translate();
            ls.Label(cacheStatusText);
            ls.Gap(4f);
            if (ls.ButtonText("FGL_ClearTextureCache".Translate()))
            {
                Find.WindowStack.Add(new Dialog_MessageBox("FGL_ClearTextureCacheConfirmation".Translate(), "Confirm".Translate(), delegate
                {
                    TextureResize.ClearCache();
                    LoadedModManager.GetMod<FasterGameLoadingMod>().WriteSettings();
                }, "GoBack".Translate()));
            }

            // Atlas Cache clear button
            ls.Gap(4f);
            if (ls.ButtonText("FGL_ClearAtlasCache".Translate()))
            {
                Find.WindowStack.Add(new Dialog_MessageBox("FGL_ClearAtlasCacheConfirmation".Translate(), "Confirm".Translate(), delegate
                {
                    StaticAtlasCache.ClearCache();
                }, "GoBack".Translate()));
            }

            ls.End();
        }


        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref loadedTexturesSinceLastSession, "loadedTexturesSinceLastSession", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref loadedTypesByFullNameSinceLastSession, "loadedTypesByFullNameSinceLastSession", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref xmlPathsSinceLastSession, "xmlPathsSinceLastSession", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref modsInLastSession, "modsInLastSession", LookMode.Value);
            Scribe_Collections.Look(ref TextureResize.resizedTextureCache, "resizedTextureCache", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref historicalBakeSpeeds, "historicalBakeSpeeds", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref StaticAtlasesBaking, "StaticAtlasesBaking", true);
            Scribe_Values.Look(ref atlasCaching, "atlasCaching", false);
            Scribe_Values.Look(ref delayGraphicLoading, "delayGraphicLoading", false);
            Scribe_Values.Look(ref earlyModContentLoading, "earlyModContentLoading", true);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                loadedTexturesSinceLastSession ??= new Dictionary<string, string>();
                loadedTypesByFullNameSinceLastSession ??= new Dictionary<string, string>();
                xmlPathsSinceLastSession ??= new Dictionary<string, bool>();
                modsInLastSession ??= new List<string>();
                historicalBakeSpeeds ??= new List<float>();
                TextureResize.resizedTextureCache ??= new Dictionary<string, string>();

                // 使用 hash 比較偵測 mod 組合變更，避免 O(n) SequenceEqual
                int currentHash = 0;
                foreach (var mod in ModsConfig.ActiveModsInLoadOrder)
                    unchecked { currentHash = currentHash * 31 + mod.packageIdLowerCase.GetHashCode(); }

                int lastHash = 0;
                foreach (var modId in modsInLastSession)
                    unchecked { lastHash = lastHash * 31 + modId.GetHashCode(); }

                if (currentHash != lastHash)
                {
                    loadedTexturesSinceLastSession.Clear();
                    loadedTypesByFullNameSinceLastSession.Clear();
                    xmlPathsSinceLastSession.Clear();
                    TextureResize.ClearCache();
                    StaticAtlasCache.ClearCache();
                }
            }
        }
    }
}

