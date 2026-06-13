using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 在所有 StaticConstructorOnStartupUtility 完成後執行收尾工作：
    /// 儲存跨 session 快取資料、注入翻譯、排程延遲動作。
    /// </summary>
    [HarmonyPatch(typeof(StaticConstructorOnStartupUtility), nameof(StaticConstructorOnStartupUtility.CallAll))]
    public static class Startup
    {
        private static readonly List<Action> onStartupCompleted = new List<Action>();

        /// <summary>
        /// 註冊在遊戲啟動載入完畢（CallAll 完成）後要執行的回呼。
        /// </summary>
        public static void RegisterOnStartupCompleted(Action callback)
        {
            if (callback != null)
            {
                onStartupCompleted.Add(callback);
            }
        }

        public static void Postfix()
        {
            // 儲存目前 session 的數據，以用於跨 session 快取（使用 loop 避免 LINQ 分配）
            var activeMods = ModsConfig.ActiveModsInLoadOrder;
            var mods = new List<string>();
            if (activeMods != null)
            {
                foreach (var mod in activeMods)
                {
                    if (mod != null)
                    {
                        mods.Add(mod.packageIdLowerCase);
                    }
                }
            }
            SessionCache.modsInLastSession = mods;
            
            // 執行所有註冊的啟動完成回呼
            foreach (var callback in onStartupCompleted)
            {
                try
                {
                    callback();
                }
                catch (Exception ex)
                {
                    FGLLog.Error("Error executing startup-completed callback: " + ex.Message, ex);
                }
            }
            onStartupCompleted.Clear();

            // 收集所有被 Patch 的 methods 關聯的 Patch Assembly 名稱（移至背景執行以避免卡頓主執行緒）
            System.Threading.Tasks.Task.Run(() =>
            {
                // 最外層安全網：背景 Task 的例外不會被任何人觀察（fire-and-forget），
                // 若未被捕捉將成為 unobserved task exception 而靜默遺失。此處統一兜底記錄，
                // 以防未來新增的程式碼或下方未被細粒度 try 包覆的區段拋出例外導致背景執行緒悄悄崩潰。
                try
                {
                    var patchedAssemblies = new HashSet<string>();
                    try
                    {
                        foreach (var method in Harmony.GetAllPatchedMethods())
                        {
                            var patchInfo = Harmony.GetPatchInfo(method);
                            if (patchInfo == null) continue;

                            AddPatchAssemblies(patchInfo.Prefixes, patchedAssemblies);
                            AddPatchAssemblies(patchInfo.Postfixes, patchedAssemblies);
                            AddPatchAssemblies(patchInfo.Transpilers, patchedAssemblies);
                        }
                    }
                    catch (Exception ex)
                    {
                        FGLLog.Warning("Error collecting patched assemblies: " + ex.Message);
                    }
                    lock (SessionCache.patchedAssembliesLock)
                    {
                        SessionCache.patchedAssembliesLastSession = patchedAssemblies.ToList();
                    }

                    // 在背景背景執行緒啟動過期/無效的材質快取自動清理，避免阻塞啟動流程與主頁面
                    try
                    {
                        FasterGameLoadingMod.Instance?.CacheManager?.CleanupObsoleteCacheFiles();
                    }
                    catch (Exception ex)
                    {
                        FGLLog.Warning("Error executing obsolete cache cleanup: " + ex.Message);
                    }
                }
                catch (Exception ex)
                {
                    FGLLog.Error("背景啟動收尾工作發生未預期例外", ex);
                }
            });

            // 注入翻譯（包在 try/catch 內，避免例外中斷 StaticConstructorOnStartupUtility.CallAll）
            try
            {
                TranslationInjector.InjectTranslations();
            }
            catch (Exception ex)
            {
                FGLLog.Error("TranslationInjector.InjectTranslations 執行失敗", ex);
            }

            // 透過 LongEventHandler 排程設定寫入與延遲動作，以避免阻塞啟動流程
            try
            {
                LongEventHandler.toExecuteWhenFinished.Add(delegate
                {
                    LoadedModManager.GetMod<FasterGameLoadingMod>().WriteSettings();
                });
                LongEventHandler.toExecuteWhenFinished.Add(delegate
                {
                    FasterGameLoadingMod.delayedActions.StartCoroutine(FasterGameLoadingMod.delayedActions.PerformActions());
                });
            }
            catch (Exception ex)
            {
                FGLLog.Error("LongEventHandler 排程啟動收尾動作時發生錯誤", ex);
            }
        }

        private static void AddPatchAssemblies(IEnumerable<Patch> patches, HashSet<string> patchedAssemblies)
        {
            if (patches == null) return;
            foreach (var patch in patches)
            {
                if (patch?.PatchMethod?.DeclaringType?.Assembly != null)
                {
                    var name = patch.PatchMethod.DeclaringType.Assembly.GetName().Name;
                    if (name != "FasterGameLoading")
                    {
                        patchedAssemblies.Add(name);
                    }
                }
            }
        }
    }
}
