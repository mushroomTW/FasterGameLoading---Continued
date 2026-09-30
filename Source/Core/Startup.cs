using System;
using System.Collections.Generic;
using HarmonyLib;
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

            // 初次啟動與每次語言重載後的 CallAll 都會到這裡，各模組據此結算本輪的載入資料。
            SessionLifecycle.Raise(LifecyclePhase.StartupCompleted);
            StartBackgroundCacheCleanup();
            InjectTranslations();
            ScheduleDeferredStartupActions();
        }

        /// <summary>在背景執行緒啟動過期／無效的材質快取自動清理，避免阻塞啟動流程與主頁面。</summary>
        private static void StartBackgroundCacheCleanup()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    FasterGameLoadingMod.Instance?.CacheManager?.CleanupObsoleteCacheFiles();
                }
                catch (Exception ex)
                {
                    FGLLog.Warning("Error executing obsolete cache cleanup:", ex);
                }
            });
        }

        /// <summary>注入翻譯；包在 try/catch 內，避免例外中斷 StaticConstructorOnStartupUtility.CallAll。</summary>
        private static void InjectTranslations()
        {
            try
            {
                TranslationInjector.InjectTranslations();
            }
            catch (Exception ex)
            {
                FGLLog.Error("TranslationInjector.InjectTranslations execution failed", ex);
            }
        }

        /// <summary>透過 LongEventHandler 排程設定寫入與延遲動作，以避免阻塞啟動流程。</summary>
        private static void ScheduleDeferredStartupActions()
        {
            try
            {
                LongEventHandler.ExecuteWhenFinished(delegate
                {
                    LoadedModManager.GetMod<FasterGameLoadingMod>().WriteSettings();
                });
                // 必須與設定寫入分開排程。LongEventHandler 是逐一回呼各自 try/catch，
                // 兩者合併成同一個委派時，WriteSettings 失敗（設定目錄唯讀、磁碟已滿、
                // 被防毒鎖定，或 GetMod 回傳 null）會連帶讓延遲載入協程整個 session 都無法啟動。
                LongEventHandler.ExecuteWhenFinished(delegate
                {
                    var delayedActions = FasterGameLoadingMod.delayedActions;
                    if (delayedActions)
                    {
                        delayedActions.enabled = true;
                        delayedActions.StartCoroutine(delayedActions.PerformActions());
                    }
                });
            }
            catch (Exception ex)
            {
                FGLLog.Error("Error scheduling startup completion actions in LongEventHandler", ex);
            }
        }
    }
}
