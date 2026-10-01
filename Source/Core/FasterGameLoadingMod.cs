using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// FasterGameLoading 模組的進入點。
    /// 初始化 Harmony patch、設定、延遲動作管理器，並註冊快取重置回呼。
    /// </summary>
    public class FasterGameLoadingMod : Mod
    {
        public static FasterGameLoadingMod Instance { get; private set; }
        public static Harmony harmony { get; private set; }
        public static FasterGameLoadingSettings settings { get; private set; }
        public static DelayedActions delayedActions { get; private set; }

        public TextureCacheManager CacheManager { get; private set; }
        public TextureResize Resizer { get; private set; }

        public FasterGameLoadingMod(ModContentPack pack) : base(pack)
        {
            Instance = this;
            CacheManager = new TextureCacheManager();
            Resizer = new TextureResize(CacheManager);

            var gameObject = new GameObject("FasterGameLoadingMod");
            Object.DontDestroyOnLoad(gameObject);
            delayedActions = gameObject.AddComponent<DelayedActions>();
            settings = this.GetSettings<FasterGameLoadingSettings>();

            // 背景預載入已快取的紋理
            ModContentLoaderTexture2D_LoadTexture_Patch.StartPreloadCachedTextures();
            // 背景依載入順序預讀原始貼圖，主執行緒載入貼圖時不必等磁碟 I/O
            TexturePrefetcher.Start();
            StartCleanupInvalidImageOptCaches();

            harmony = new Harmony("FasterGameLoadingMod");
            // 補丁的 Prepare 與延遲視覺管線共用同一份定案值，執行期改設定不會讓兩者分歧。
            DelayedActions.CaptureStartupSettings();

            // 背景預載入所有類型，以加速後續的 AccessTools.AllTypes() 呼叫
            AccessTools_AllTypes_Patch.Preload();
            if (FasterGameLoadingSettings.TypeLookupCache)
            {
                // Mod 建構子在載入事件緒上執行：所有 mod 組件都已載入、Def／Patch XML 尚未解析，
                // 此時預熱才能讓 XML 解析階段大量的完整型別名稱查詢直接命中。
                GenTypes_GetTypeInAnyAssemblyInt_Patch.WarmupFullNames(TypeLookupCache.SearchAssemblies());
            }
            harmony.PatchAll();
            HyperdriveCompat.PatchLoadModXML(harmony, HyperdriveCompat.FindModType());
            ImageOptEarlyLoadCoordinator.TryInstall();

            // 註冊執行個體層級的快取清理（在語言切換時由 SessionLifecycle.Raise(LifecyclePhase.LanguageReloading) 觸發）
            SessionLifecycle.On(LifecyclePhase.LanguageReloading, () =>
            {
                if (delayedActions) // 利用 Unity Object 的隱式 bool 轉型檢查，防範 GameObject 銷毀時的異常
                {
                    // 不重啟提早載入：切換語言的重載交給原版流程（見 EarlyModContentLoader.Update 的說明）。
                    delayedActions.StopAllCoroutines();
                    delayedActions.ClearQueues();
                }
                try
                {
                    SoundStarter_Patch.ResetUnpatchedStatus();
                    harmony.PatchCategory("SoundStarter");
                }
                catch (System.InvalidOperationException)
                {
                    // 補丁類別 "SoundStarter" 尚未被註冊或已經被解除補丁 — 靜默跳過
                }
            });
        }

        private static void StartCleanupInvalidImageOptCaches()
        {
            if (TextureOwnership.Current is not TextureOwner.ImageOpt) return;

            var roots = new List<string>();
            foreach (var mod in ModsConfig.ActiveModsInLoadOrder)
            {
                if (mod?.RootDir != null)
                {
                    roots.Add(mod.RootDir.FullName);
                }
            }

            Task.Run(() =>
            {
                // 背景清理為 fire-and-forget，沒有呼叫端會觀察這個 Task。
                // 若不自行攔截，清理途中的例外會成為未觀察的 faulted task 而被靜默吞掉。
                try
                {
                    Thread.Sleep(FGLConsts.TexturePreloadDelayMs);
                    var deleted = ImageOptCompat.CleanupInvalidDdsZstdCaches(roots);
                    if (deleted > 0)
                    {
                        FGLLog.Message($"Removed invalid ImageOpt DDS cache files: {deleted.ToString(CultureInfo.InvariantCulture)}");
                    }
                }
                catch (System.Exception ex)
                {
                    FGLLog.Warning("Background cleanup of invalid ImageOpt DDS caches failed:", ex);
                }
            });
        }



        public override string SettingsCategory()
        {
            return "FGL_ModName".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            base.DoSettingsWindowContents(inRect);
            FasterGameLoadingSettings.DoSettingsWindowContents(inRect);
        }
    }
}

