using HarmonyLib;
using System;
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
        public static Dictionary<string, Type> cachedResults = new Dictionary<string, Type>();
        public static Dictionary<string, string> loadedTypesThisSession = new Dictionary<string, string>();

        static GenTypes_GetTypeInAnyAssemblyInt_Patch()
        {
            CacheResetter.Register(() => ClearCache());
        }

        public static void ClearCache()
        {
            cachedResults.Clear();
            loadedTypesThisSession.Clear();
        }

        public static bool Prefix(ref Type __result, out (string, bool) __state, ref string typeName)
        {
            if (cachedResults.TryGetValue(typeName, out var result))
            {
                __result = result;
                __state = new (typeName, true);
                return false;
            }
            else
            {
                __state = new(typeName, false);
                if (SessionCache.loadedTypesByFullNameSinceLastSession.TryGetValue(typeName, out var fullName))
                {
                    typeName = fullName;
                }
                return true;
            }
        }

        public static void Postfix(Type __result, (string, bool) __state)
        {
            if (__result != null)
            {
                var fullName = __result.FullName;
                if (__state.Item2 is false)
                {
                    cachedResults[__state.Item1] = __result;
                    if (fullName != __state.Item1)
                    {
                        cachedResults[fullName] = __result;
                    }
                }
                if (__state.Item1 != fullName)
                {
                    loadedTypesThisSession[__state.Item1] = fullName;
                }
            }
        }
    }
}