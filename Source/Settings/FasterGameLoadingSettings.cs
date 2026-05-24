using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// FasterGameLoading 的使用者設定與跨 session 持久化資料。
    /// 所有設定開關皆以 public static 欄位暴露，供其他模組直接讀取。
    /// </summary>
    public class FasterGameLoadingSettings : ModSettings
    {
        // ── User-configurable feature toggles ──

        /// <summary>延遲非必要圖形/圖示載入（預設關閉）</summary>
        public static bool DelayGraphicLoading = false;

        /// <summary>提早載入 Mod 內容（預設開啟）</summary>
        /// <remarks>
        /// 此處刻意使用 camelCase 命名，與 Loading Progress mod (ilyvion/loading-progress)
        /// 的整合相容。該 mod 透過 Harmony AccessTools.Field()（case-sensitive）以
        /// "earlyModContentLoading" 反射讀取此欄位值，若改為 PascalCase 將導致其
        /// 無法顯示本模組的額外進度條。
        /// </remarks>
        public static bool earlyModContentLoading = true;

        /// <summary>自適應靜態圖集烘焙（預設開啟）</summary>
        public static bool StaticAtlasesBaking = true;

        /// <summary>圖集快取（預設關閉）</summary>
        public static bool AtlasCaching = false;

        /// <summary>啟用多執行緒預載入（預設開啟）</summary>
        public static bool EnableMultiThreading = true;

        public static void DoSettingsWindowContents(Rect inRect)
        {
            var ls = new Listing_Standard();
            ls.Begin(inRect);
            ls.CheckboxLabeled("FGL_EarlyModContentLoading".Translate(), ref earlyModContentLoading);
            ls.CheckboxLabeled("FGL_EnableMultiThreading".Translate(), ref EnableMultiThreading);
            ls.CheckboxLabeled("FGL_DelayGraphicLoading".Translate(), ref DelayGraphicLoading);
            ls.CheckboxLabeled("FGL_StaticAtlasesBaking".Translate(), ref StaticAtlasesBaking);
            ls.CheckboxLabeled("FGL_AtlasCaching".Translate(), ref AtlasCaching);
            ls.Gap(12f);

            // Texture resize explanation
            var explanationText = "FGL_TextureResizingExplanation".Translate();
            var textHeight = Text.CalcHeight(explanationText, ls.ColumnWidth);
            var explanationRect = ls.GetRect(textHeight + 8f);
            Widgets.Label(explanationRect, explanationText);

            // Texture resize button
            ls.Gap(4f);
            if (ls.ButtonText("FGL_DownscaleTextures".Translate()))
            {
                Find.WindowStack.Add(new Dialog_MessageBox("FGL_DownscaleTexturesConfirmation".Translate(), "Confirm".Translate(), delegate
                {
                    TextureResize.DoTextureResizing();
                }, "GoBack".Translate()));
            }

            // Reset texture button (clear cache) + status display
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

            // Atlas cache clear button
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

            // 使用者設定
            Scribe_Values.Look(ref StaticAtlasesBaking, "StaticAtlasesBaking", true);
            Scribe_Values.Look(ref AtlasCaching, "atlasCaching", false);
            Scribe_Values.Look(ref DelayGraphicLoading, "delayGraphicLoading", false);
            Scribe_Values.Look(ref earlyModContentLoading, "earlyModContentLoading", true);
            Scribe_Values.Look(ref EnableMultiThreading, "enableMultiThreading", true);

            // 紋理快取
            Scribe_Collections.Look(ref TextureCacheManager.resizedTextureCache, "resizedTextureCache", LookMode.Value, LookMode.Value);

            // 跨 session 快取資料委派給 SessionCache
            SessionCache.ExposeData();
        }
    }
}
