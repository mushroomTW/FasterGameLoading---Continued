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
        /// <summary>詳細日誌記錄開關（預設關閉）</summary>
        public static bool VerboseLogging = false;


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

        /// <summary>自適應靜態圖集烘焙（預設關閉）</summary>
        public static bool StaticAtlasesBaking = false;

        /// <summary>啟用多執行緒預載入（預設開啟）</summary>
        public static bool EnableMultiThreading = true;

        /// <summary>XPath 快取（預設開啟）</summary>
        public static bool XPathCaching = true;


        private static Vector2 scrollPosition = Vector2.zero;
        private static float viewHeight = 0f;

        public static void DoSettingsWindowContents(Rect inRect)
        {
            Rect viewRect = new Rect(0f, 0f, inRect.width - 18f, Mathf.Max(viewHeight, inRect.height));
            Widgets.BeginScrollView(inRect, ref scrollPosition, viewRect);

            var ls = new Listing_Standard();
            ls.Begin(viewRect);
            ls.CheckboxLabeled("FGL_EarlyModContentLoading".Translate(), ref earlyModContentLoading);
            ls.CheckboxLabeled("FGL_MultiThreading".Translate(), ref EnableMultiThreading);
            ls.CheckboxLabeled("FGL_XPathCaching".Translate(), ref XPathCaching);
            ls.CheckboxLabeled("FGL_DelayGraphicLoading".Translate(), ref DelayGraphicLoading);

            ls.CheckboxLabeled("FGL_StaticAtlasesBaking".Translate(), ref StaticAtlasesBaking);
            ls.CheckboxLabeled("FGL_VerboseLogging".Translate(), ref VerboseLogging);
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
                    // 防止 Mod 初始化失敗時 Instance 或 Resizer 為 null 導致 NRE
                    FasterGameLoadingMod.Instance?.Resizer?.DoTextureResizing();
                }, "GoBack".Translate()));
            }

            // Reset texture button (clear cache) + status display
            ls.Gap(4f);
            var cacheCount = FasterGameLoadingMod.Instance?.Resizer?.CacheCount ?? 0;
            var cacheStatusText = cacheCount > 0
                ? "FGL_TextureCacheStatus_Active".Translate(cacheCount)
                : "FGL_TextureCacheStatus_Empty".Translate();
            ls.Label(cacheStatusText);
            ls.Gap(4f);
            if (ls.ButtonText("FGL_ClearTextureCache".Translate()))
            {
                Find.WindowStack.Add(new Dialog_MessageBox("FGL_ClearTextureCacheConfirmation".Translate(), "Confirm".Translate(), delegate
                {
                    // 防止 Mod 初始化失敗時 Instance 或 Resizer 為 null 導致 NRE
                    FasterGameLoadingMod.Instance?.Resizer?.ClearCache();
                    LoadedModManager.GetMod<FasterGameLoadingMod>().WriteSettings();
                }, "GoBack".Translate()));
            }

            ls.End();
            viewHeight = ls.CurHeight + 20f;
            Widgets.EndScrollView();
        }

        public override void ExposeData()
        {
            base.ExposeData();

            // 使用者設定
            Scribe_Values.Look(ref StaticAtlasesBaking, "StaticAtlasesBaking", false);
            Scribe_Values.Look(ref DelayGraphicLoading, "delayGraphicLoading", false);
            Scribe_Values.Look(ref earlyModContentLoading, "earlyModContentLoading", true);
            Scribe_Values.Look(ref EnableMultiThreading, "enableMultiThreading", true);
            Scribe_Values.Look(ref XPathCaching, "XPathCaching", true);
            Scribe_Values.Look(ref VerboseLogging, "verboseLogging", false);


            // 紋理快取
            var cacheManager = FasterGameLoadingMod.Instance?.CacheManager;
            if (cacheManager != null)
            {
                Scribe_Collections.Look(ref cacheManager.resizedTextureCache, "resizedTextureCache", LookMode.Value, LookMode.Value);
                if (cacheManager.resizedTextureCache == null)
                {
                    cacheManager.resizedTextureCache = new Dictionary<string, string>();
                }
            }

            // 跨 session 快取資料委派給 SessionCache
            SessionCache.ExposeData();
        }
    }
}
