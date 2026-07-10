using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 GenTypes.GetTypeInAnyAssemblyInt 以快取型別查詢結果。
    /// 先從跨 session 快取中尋找完整名稱對照表，再查詢本次 session 的執行期快取。
    /// 命中時跳過原始方法，未命中則記錄到本次 session 快取。
    /// </summary>
    [HarmonyPatch(typeof(GenTypes), "GetTypeInAnyAssemblyInt")]
    public static class GenTypes_GetTypeInAnyAssemblyInt_Patch
    {
        internal static ConcurrentDictionary<string, Type> cachedResults = new ConcurrentDictionary<string, Type>();
        internal static ConcurrentDictionary<string, string> loadedTypesThisSession = new ConcurrentDictionary<string, string>();

        static GenTypes_GetTypeInAnyAssemblyInt_Patch()
        {
            CacheResetter.Register(() => ClearCache());

            Startup.RegisterOnStartupCompleted(() =>
            {
                SessionCache.loadedTypesByFullNameSinceLastSession = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(loadedTypesThisSession);
            });
        }

        public static void ClearCache()
        {
            cachedResults.Clear();
            loadedTypesThisSession.Clear();
        }

        /// <summary>
        /// 前置處理：優先使用執行期型別快取或跨 session 快取比對，命中時返回並跳過原方法。
        /// </summary>
        public static bool Prefix(ref Type __result, out (string originalTypeName, string namespaceIfAmbiguous, string cacheKey, bool isCached) __state, ref string typeName, string namespaceIfAmbiguous)
        {
            var cacheKey = MakeCacheKey(typeName, namespaceIfAmbiguous);
            if (cachedResults.TryGetValue(cacheKey, out var result))
            {
                __result = result;
                __state = (typeName, namespaceIfAmbiguous, cacheKey, true);
                return false;
            }
            else
            {
                __state = (typeName, namespaceIfAmbiguous, cacheKey, false);
                if (SessionCache.loadedTypesByFullNameSinceLastSession.TryGetValue(cacheKey, out var fullName)
                    || (string.IsNullOrEmpty(namespaceIfAmbiguous) && SessionCache.loadedTypesByFullNameSinceLastSession.TryGetValue(typeName, out fullName)))
                {
                    typeName = fullName;
                }
                return true;
            }
        }

        /// <summary>
        /// 後置處理：若為非快取查詢，將結果寫入執行期和 session 的名稱映射快取。
        /// </summary>
        public static void Postfix(Type __result, (string originalTypeName, string namespaceIfAmbiguous, string cacheKey, bool isCached) __state)
        {
            if (__result != null)
            {
                var fullName = __result.FullName;
                if (string.IsNullOrEmpty(fullName))
                {
                    return;
                }
                if (__state.isCached is false)
                {
                    cachedResults[__state.cacheKey] = __result;
                    if (fullName != __state.originalTypeName)
                    {
                        cachedResults[MakeCacheKey(fullName, null)] = __result;
                        if (!string.IsNullOrEmpty(__state.namespaceIfAmbiguous))
                        {
                            cachedResults[MakeCacheKey(fullName, __state.namespaceIfAmbiguous)] = __result;
                        }
                    }
                }
                if (__state.originalTypeName != fullName)
                {
                    loadedTypesThisSession[__state.cacheKey] = fullName;
                }
            }
        }

        internal static string MakeCacheKey(string typeName, string namespaceIfAmbiguous = null)
        {
            if (string.IsNullOrEmpty(namespaceIfAmbiguous))
            {
                return typeName ?? string.Empty;
            }
            return $"{typeName}|ns|{namespaceIfAmbiguous}";
        }
    }
}
