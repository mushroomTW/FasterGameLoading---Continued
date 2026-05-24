using HarmonyLib;
using System;
using System.Collections.Generic;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 AccessTools.TypeByName 以使用跨 session 快取。
    /// 優先查詢上次 session 中記錄的完整型別名稱對照表，
    /// 再查詢本次 session 的執行期快取。
    /// </summary>
    [HarmonyPatch(typeof(AccessTools), "TypeByName")]
    public static class AccessTools_TypeByName_Patch
    {
        public static bool Prefix(ref Type __result, out (bool isCached, string originalName) __state, ref string name)
        {
            var oldName = name;
            if (SessionCache.loadedTypesByFullNameSinceLastSession.TryGetValue(name, out var fullName))
            {
                name = fullName;
            }
            if (GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults.TryGetValue(name, out var result))
            {
                __result = result;
                __state = (true, oldName);
                return false;
            }
            else
            {
                __state = (false, oldName);
                return true;
            }
        }

        public static void Postfix(Type __result, string name, (bool isCached, string originalName) __state)
        {
            if (__state.isCached is false)
            {
                GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults[name] = __result;
                if (__result != null && __result.FullName != name)
                {
                    SessionCache.loadedTypesByFullNameSinceLastSession[__state.originalName] = __result.FullName;
                    GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults[__result.FullName] = __result;
                }
            }
        }
    }
}