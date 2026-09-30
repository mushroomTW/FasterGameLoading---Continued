using System;
using System.Collections.Concurrent;
using HarmonyLib;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 AccessTools.TypeByName，以本次 session 的執行期快取加速重複查詢。
    /// 刻意不與 GenTypes_GetTypeInAnyAssemblyInt_Patch 共用快取、也不寫入跨 session 對照：
    /// AccessTools.TypeByName 會以短名稱跨任意命名空間比對，而 GenTypes 只查預設命名空間且不分大小寫，
    /// 兩者解析規則不同，共用結果會讓一方拿到另一方規則下才會解析出的型別。
    /// </summary>
    [HarmonyPatch(typeof(AccessTools), "TypeByName")]
    public static class AccessTools_TypeByName_Patch
    {
        public static bool Prepare() => FasterGameLoadingSettings.TypeLookupCache;

        internal static ConcurrentDictionary<string, Type> cachedResults { get; } = new ConcurrentDictionary<string, Type>(StringComparer.Ordinal);

        /// <summary>
        /// 前置處理：命中執行期快取時直接回傳並跳過原方法。
        /// </summary>
        public static bool Prefix(ref Type __result, string name)
        {
            if (!string.IsNullOrEmpty(name) && cachedResults.TryGetValue(name, out var result))
            {
                __result = result;
                return false;
            }
            return true;
        }

        /// <summary>
        /// 後置處理：只記錄原方法實際解析成功的結果；查無結果不快取，
        /// 因為之後載入的組件可能才定義該型別。
        /// </summary>
        public static void Postfix(Type __result, string name, bool __runOriginal)
        {
            if (__runOriginal && __result != null && !string.IsNullOrEmpty(name))
            {
                cachedResults[name] = __result;
            }
        }
    }
}
