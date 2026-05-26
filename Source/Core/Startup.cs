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
            // Save current session data for cross-session caching
            SessionCache.modsInLastSession = ModsConfig.ActiveModsInLoadOrder.Select(x => x.packageIdLowerCase).ToList();
            
            // Execute all registered startup-completed callbacks
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

            // 收集所有被 Patch 的 methods 關聯的 Patch Assembly 名稱
            var patchedAssemblies = new HashSet<string>();
            try
            {
                foreach (var method in Harmony.GetAllPatchedMethods())
                {
                    var patchInfo = Harmony.GetPatchInfo(method);
                    if (patchInfo == null) continue;

                    var patches = patchInfo.Prefixes.Concat(patchInfo.Postfixes).Concat(patchInfo.Transpilers);
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
            catch (Exception ex)
            {
                FGLLog.Warning("Error collecting patched assemblies: " + ex.Message);
            }
            lock (SessionCache.patchedAssembliesLock)
            {
                SessionCache.patchedAssembliesLastSession = patchedAssemblies.ToList();
            }

            // Inject translations
            TranslationInjector.InjectTranslations();

            // Schedule setting write and delayed actions via LongEventHandler to avoid blocking startup
            LongEventHandler.toExecuteWhenFinished.Add(delegate
            {
                LoadedModManager.GetMod<FasterGameLoadingMod>().WriteSettings();
            });
            LongEventHandler.toExecuteWhenFinished.Add(delegate
            {
                FasterGameLoadingMod.delayedActions.StartCoroutine(FasterGameLoadingMod.delayedActions.PerformActions());
            });
        }
    }
}
