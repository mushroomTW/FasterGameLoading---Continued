using System.Collections.Generic;
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
        public static Harmony harmony;
        public static FasterGameLoadingSettings settings;
        public static DelayedActions delayedActions;

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
            StartCleanupInvalidImageOptCaches();

            harmony = new Harmony("FasterGameLoadingMod");

            // 背景預載入所有類型，以加速後續的 AccessTools.AllTypes() 呼叫
            AccessTools_AllTypes_Patch.Preload();
            harmony.PatchAll();

            // 註冊執行個體層級的快取清理（在語言切換時由 CacheResetter.ResetAll() 觸發）
            CacheResetter.Register(() =>
            {
                if (delayedActions) // 利用 Unity Object 的隱式 bool 轉型檢查，防範 GameObject 銷毀時的異常
                {
                    delayedActions.StopAllCoroutines();
                    delayedActions.ClearQueues();
                    delayedActions.ResetEarlyLoading();
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

            // XML metadata 僅在背景執行緒讀取；快取狀態由 Update 主執行緒提交。
            try
            {
                var thirdPartyModPaths = new System.Collections.Generic.List<string>();
                foreach (var m in ModsConfig.ActiveModsInLoadOrder)
                {
                    if (m != null && !m.Official && m.RootDir != null)
                    {
                        thirdPartyModPaths.Add(m.RootDir.FullName);
                    }
                }
                string configPath = GenFilePaths.ConfigFolderPath;
                XmlNode_SelectSingleNode_Patch.isXmlScanComplete = false;
                XmlChangeDetector.StartScanAsync(thirdPartyModPaths, configPath, delayedActions.EnqueueMainThreadAction);
            }
            catch (System.Exception ex)
            {
                FGLLog.Warning("Failed to start XML file scan:", ex);
                // 萬一出錯，確保快取攔截功能不會被永久關閉
                XmlNode_SelectSingleNode_Patch.isXmlScanComplete = true;
            }

        }

        private static void StartCleanupInvalidImageOptCaches()
        {
            if (!ImageOptCompat.IsActive) return;

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
                Thread.Sleep(FGLConsts.TexturePreloadDelayMs);
                var deleted = ImageOptCompat.CleanupInvalidDdsZstdCaches(roots);
                if (deleted > 0)
                {
                    FGLLog.Message($"Removed invalid ImageOpt DDS cache files: {deleted}");
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

