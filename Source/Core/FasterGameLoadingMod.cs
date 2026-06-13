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

        public ITextureCacheManager CacheManager { get; private set; }
        public IAtlasCacheManager AtlasCache => StaticAtlasCache.Instance;
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

            // 啟動異步 XML 檔案變更掃描，比對是否需要清除 XPath 快取
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
                System.Threading.Tasks.Task.Run(() => XmlChangeDetector.ScanXmlFiles(thirdPartyModPaths, configPath));
            }
            catch (System.Exception ex)
            {
                FGLLog.Warning("FGL_Log_FailedToStartAsyncXMLScan".TranslateWithFallback("Failed to start asynchronous XML file scan: {0}", ex.Message));
                // 萬一出錯，確保快取攔截功能不會被永久關閉
                XmlNode_SelectSingleNode_Patch.isXmlScanComplete = true;
            }

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

