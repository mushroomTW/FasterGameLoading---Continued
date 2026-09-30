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
        public static bool VerboseLogging { get; set; }


        /// <summary>延遲非必要圖形/圖示載入（預設關閉）</summary>
        public static bool DelayGraphicLoading { get; set; }

        /// <summary>提早載入 Mod 內容（預設開啟）</summary>
        /// <remarks>
        /// 此處刻意使用 camelCase 命名，與 Loading Progress mod (ilyvion/loading-progress)
        /// 的整合相容。該 mod 透過 Harmony AccessTools.Field()（case-sensitive）以
        /// "earlyModContentLoading" 反射讀取此欄位值，若改為 PascalCase 將導致其
        /// 無法顯示本模組的額外進度條。
        /// </remarks>
        // MA0069/S1104: 必須維持為公開靜態欄位。loading-progress 以
        // AccessTools.Field("earlyModContentLoading") 反射讀取；改成屬性後只會留下
        // 編譯器產生的 <earlyModContentLoading>k__BackingField，該反射查詢會失敗。
#pragma warning disable MA0069, S1104
        public static bool earlyModContentLoading = true;
#pragma warning restore MA0069, S1104

        /// <summary>自適應靜態圖集烘焙（預設關閉）</summary>
        public static bool StaticAtlasesBaking { get; set; }

        /// <summary>啟用多執行緒預載入（預設開啟）</summary>
        public static bool EnableMultiThreading { get; set; } = true;

        /// <summary>型別查詢快取（預設開啟）；重新啟動遊戲後生效。</summary>
        public static bool TypeLookupCache { get; set; } = true;


        private static Vector2 scrollPosition = Vector2.zero;
        private static float viewHeight = 0f;

        public static void DoSettingsWindowContents(Rect inRect)
        {
            Rect viewRect = new Rect(0f, 0f, inRect.width - 18f, Mathf.Max(viewHeight, inRect.height));
            Widgets.BeginScrollView(inRect, ref scrollPosition, viewRect);

            var ls = new Listing_Standard();
            ls.Begin(viewRect);

            DrawLoadingOptions(ls);
            DrawDiagnosticsOptions(ls);
            ls.Gap(12f);
            DrawTextureCacheSection(ls);

            ls.End();
            viewHeight = ls.CurHeight + 20f;
            Widgets.EndScrollView();
        }

        /// <summary>載入行為相關的開關。</summary>
        private static void DrawLoadingOptions(Listing_Standard ls)
        {
            ls.CheckboxLabeled("FGL_EarlyModContentLoading".Translate(), ref earlyModContentLoading);
            var enableMultiThreading = EnableMultiThreading;
            ls.CheckboxLabeled("FGL_MultiThreading".Translate(), ref enableMultiThreading);
            EnableMultiThreading = enableMultiThreading;
            var typeLookupCache = TypeLookupCache;
            ls.CheckboxLabeled("FGL_TypeLookupCache".Translate(), ref typeLookupCache);
            TypeLookupCache = typeLookupCache;
            var delayGraphicLoading = DelayGraphicLoading;
            // 延遲載入與自適應烘焙在 Mod 建構子定案（DelayedActions.CaptureStartupSettings），改了要重開遊戲才生效。
            ls.CheckboxLabeled("FGL_DelayGraphicLoading".Translate() + " " + "FGL_RequiresRestart".Translate(), ref delayGraphicLoading);
            DelayGraphicLoading = delayGraphicLoading;
        }

        /// <summary>圖集烘焙與詳細日誌開關。</summary>
        private static void DrawDiagnosticsOptions(Listing_Standard ls)
        {
            var staticAtlasesBaking = StaticAtlasesBaking;
            ls.CheckboxLabeled("FGL_StaticAtlasesBaking".Translate() + " " + "FGL_RequiresRestart".Translate(), ref staticAtlasesBaking);
            StaticAtlasesBaking = staticAtlasesBaking;
            var verboseLogging = VerboseLogging;
            ls.CheckboxLabeled("FGL_VerboseLogging".Translate(), ref verboseLogging);
            VerboseLogging = verboseLogging;
        }

        /// <summary>紋理降質說明、執行按鈕，以及快取狀態與清除按鈕。</summary>
        private static void DrawTextureCacheSection(Listing_Standard ls)
        {
            // Texture resize explanation
            var explanationText = "FGL_TextureResizingExplanation".Translate();
            var textHeight = Text.CalcHeight(explanationText, ls.ColumnWidth);
            var explanationRect = ls.GetRect(textHeight + 8f);
            Widgets.Label(explanationRect, explanationText);

            // Texture resize button
            ls.Gap(4f);
            if (!TextureOwnership.FglOwnsTextureLoading)
            {
                // 外部工具接手貼圖載入時 FGL 不登記貼圖，降質工具掃描不到任何東西，按了也只會留下舊快取。
                ls.Label("FGL_DownscaleTexturesUnavailable".Translate());
            }
            else if (ls.ButtonText("FGL_DownscaleTextures".Translate()))
            {
                Find.WindowStack.Add(new Dialog_MessageBox("FGL_DownscaleTexturesConfirmation".Translate(), "Confirm".Translate(), delegate
                {
                    // 防止 Mod 初始化失敗時 Instance 或 Resizer 為 null 導致 NRE
                    LongEventHandler.QueueLongEvent(
                        () => FasterGameLoadingMod.Instance?.Resizer?.DoTextureResizing(),
                        "FGL_DownscalingTextures",
                        doAsynchronously: false,
                        exceptionHandler: ex => FGLLog.Error("Texture downscale long event failed:", ex));
                }, "GoBack".Translate()));
            }

            // Reset texture button (clear cache) + status display
            ls.Gap(4f);
            var cacheCount = FasterGameLoadingMod.Instance?.CacheManager?.CacheCount ?? 0;
            var cacheStatusText = cacheCount > 0
                ? "FGL_TextureCacheStatus_Active".Translate(cacheCount)
                : "FGL_TextureCacheStatus_Empty".Translate();
            ls.Label(cacheStatusText);
            ls.Gap(4f);
            if (ls.ButtonText("FGL_ClearTextureCache".Translate()))
            {
                Find.WindowStack.Add(new Dialog_MessageBox("FGL_ClearTextureCacheConfirmation".Translate(), "Confirm".Translate(), static () =>
                {
                    // 與降質相同走長事件：清除要等背景快取清理結束（共用維護鎖），期間顯示載入畫面而不是讓遊戲無回應。
                    LongEventHandler.QueueLongEvent(
                        static () =>
                        {
                            // 防止 Mod 初始化失敗時 Instance 或 CacheManager 為 null 導致 NRE
                            FasterGameLoadingMod.Instance?.CacheManager?.ClearCache();
                            LoadedModManager.GetMod<FasterGameLoadingMod>().WriteSettings();
                        },
                        "FGL_ClearingTextureCache",
                        doAsynchronously: false,
                        exceptionHandler: static ex => FGLLog.Error("Clear texture cache long event failed:", ex));
                }, "GoBack".Translate()));
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();

            // 使用者設定
            var staticAtlasesBaking = StaticAtlasesBaking;
            Scribe_Values.Look(ref staticAtlasesBaking, "StaticAtlasesBaking", defaultValue: false);
            StaticAtlasesBaking = staticAtlasesBaking;
            var delayGraphicLoading = DelayGraphicLoading;
            Scribe_Values.Look(ref delayGraphicLoading, "delayGraphicLoading", defaultValue: false);
            DelayGraphicLoading = delayGraphicLoading;
            Scribe_Values.Look(ref earlyModContentLoading, "earlyModContentLoading", defaultValue: true);
            var enableMultiThreading = EnableMultiThreading;
            Scribe_Values.Look(ref enableMultiThreading, "enableMultiThreading", defaultValue: true);
            EnableMultiThreading = enableMultiThreading;
            var typeLookupCache = TypeLookupCache;
            Scribe_Values.Look(ref typeLookupCache, "typeLookupCache", defaultValue: true);
            TypeLookupCache = typeLookupCache;
            var verboseLogging = VerboseLogging;
            Scribe_Values.Look(ref verboseLogging, "verboseLogging", defaultValue: false);
            VerboseLogging = verboseLogging;


            // 紋理快取
            var cacheManager = FasterGameLoadingMod.Instance?.CacheManager;
            if (cacheManager != null)
            {
                // 存檔時序列化鎖內取得的快照：啟動收尾的背景清理可能同時從對照表移除項目，
                // 直接讓 Scribe 列舉正在被修改的字典會拋例外，而 InitSaving 已先截斷設定檔，只會留下半份設定。
                var resizedTextureCache = Scribe.mode is LoadSaveMode.Saving ? cacheManager.GetResizedTextureCacheCopy() : null;
                Scribe_Collections.Look(ref resizedTextureCache, "resizedTextureCache", LookMode.Value, LookMode.Value);
                // 值型別字典只在 LoadingVars 那輪被填入，之後各輪的區域變數都是 null，必須在同一輪寫回。
                if (Scribe.mode is LoadSaveMode.LoadingVars)
                {
                    cacheManager.ReplaceCacheMap(resizedTextureCache);
                }
            }

            // 跨 session 快取資料委派給 SessionCache
            SessionCache.ExposeData();
        }
    }
}
