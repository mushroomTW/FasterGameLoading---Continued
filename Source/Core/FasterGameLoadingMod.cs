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
            harmony = new Harmony("FasterGameLoadingMod");

            // Background preload all types to speed up subsequent AccessTools.AllTypes() calls
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
                    // Patch category "SoundStarter" hasn't been registered yet or was already unpatched — skip silently
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
                System.Threading.Tasks.Task.Run(() => XmlChangeDetector.ScanXmlFilesAsync(thirdPartyModPaths));
            }
            catch (System.Exception ex)
            {
                FGLLog.Warning("Failed to start asynchronous XML file scan: " + ex.Message);
                // 萬一出錯，確保快取攔截功能不會被永久關閉
                XmlNode_SelectSingleNode_Patch.isXmlScanComplete = true;
            }

            // 啟動背景執行緒 JIT 預編譯，預熱所有第三方 Mod Assemblies 方法
            try
            {
                JITPrecompiler.StartPrecompilation();
            }
            catch (System.Exception ex)
            {
                FGLLog.Warning("Failed to start background JIT pre-compilation: " + ex.Message);
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

