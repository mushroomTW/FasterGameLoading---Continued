using HarmonyLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
                lock (SessionCache.loadedTypesLock)
                {
                    SessionCache.loadedTypesByFullNameSinceLastSession = new System.Collections.Generic.Dictionary<string, string>(loadedTypesThisSession);
                }
            });
        }

        public static void ClearCache()
        {
            cachedResults.Clear();
            loadedTypesThisSession.Clear();
        }

        public static bool Prefix(ref Type __result, out (string originalTypeName, bool isCached) __state, ref string typeName)
        {
            if (cachedResults.TryGetValue(typeName, out var result))
            {
                __result = result;
                __state = (typeName, true);
                return false;
            }
            else
            {
                __state = (typeName, false);
                string fullName = null;
                bool found = false;
                lock (SessionCache.loadedTypesLock)
                {
                    found = SessionCache.loadedTypesByFullNameSinceLastSession.TryGetValue(typeName, out fullName);
                }
                if (found)
                {
                    typeName = fullName;
                }
                return true;
            }
        }

        public static void Postfix(Type __result, (string originalTypeName, bool isCached) __state)
        {
            if (__result != null)
            {
                var fullName = __result.FullName;
                if (__state.isCached is false)
                {
                    cachedResults[__state.originalTypeName] = __result;
                    if (fullName != __state.originalTypeName)
                    {
                        cachedResults[fullName] = __result;
                    }
                }
                if (__state.originalTypeName != fullName)
                {
                    loadedTypesThisSession[__state.originalTypeName] = fullName;
                }
            }
        }
    }
}