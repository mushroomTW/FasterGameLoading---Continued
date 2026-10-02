using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
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
        public static bool Prepare() => FasterGameLoadingSettings.TypeLookupCache;

        internal static ConcurrentDictionary<string, Type> cachedResults { get; } = new ConcurrentDictionary<string, Type>(StringComparer.Ordinal);
        internal static ConcurrentDictionary<string, string> loadedTypesThisSession { get; } = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// 指向（已套用本補丁的）GenTypes.GetTypeInAnyAssemblyInt，供舊對照失效時以原名重查。
        /// 該方法為 private，直接呼叫會觸發 MethodAccessException，故經由委派呼叫；
        /// 延遲建立以確保在 Harmony 套用補丁之後才取得。
        /// </summary>
        private static Func<string, string, Type> resolveUncached;

        public static void ClearCache()
        {
            cachedResults.Clear();
            loadedTypesThisSession.Clear();
        }

        /// <summary>
        /// 以 FullName 預熱型別快取。由 Mod 建構子在載入事件緒呼叫：此時所有 mod 組件都已載入，
        /// 而型別查詢最密集的 Def／Patch XML 解析尚未開始。依原版搜尋順序先到先得，
        /// 同名型別只保留原版以完整名稱會先查到的那一個。
        /// 只能預熱 FullName：不同 mod 常有同名但不同命名空間的類別，寫入短名稱會讓查詢拿到錯誤型別
        /// （過去曾因此在翻譯注入時於 MakeGenericType 崩潰）；短名稱交由原方法解析後再由 Postfix 記錄。
        /// </summary>
        internal static void WarmupFullNames(IEnumerable<Assembly> assembliesInSearchOrder)
        {
            foreach (var assembly in assembliesInSearchOrder)
            {
                Type[] types;
                try
                {
                    // 與 AllTypes 的背景預載共用列舉結果，同一組件不掃第二遍。
                    types = AssemblyTypesCache.Get(assembly);
                }
                catch
                {
                    // 忽略個別組件型別讀取失敗
                    continue;
                }

                foreach (var type in types)
                {
                    try
                    {
                        var fullName = type?.FullName;
                        if (!string.IsNullOrEmpty(fullName))
                        {
                            cachedResults.TryAdd(fullName, type);
                        }
                    }
                    catch
                    {
                        // 忽略個別型別反射處理錯誤
                    }
                }
            }
        }

        /// <summary>
        /// 前置處理：優先使用執行期型別快取或跨 session 快取比對，命中時返回並跳過原方法。
        /// </summary>
        public static bool Prefix(ref Type __result, out (string originalTypeName, string namespaceIfAmbiguous, string cacheKey, bool isCached, bool usedSessionMapping) __state, ref string typeName, string namespaceIfAmbiguous)
        {
            var cacheKey = MakeCacheKey(typeName, namespaceIfAmbiguous);
            if (cachedResults.TryGetValue(cacheKey, out var result) || TryGetExactFullName(typeName, namespaceIfAmbiguous, out result))
            {
                __result = result;
                __state = (typeName, namespaceIfAmbiguous, cacheKey, true, false);
                return false;
            }

            var originalTypeName = typeName;
            bool usedSessionMapping = false;
            if (TypeLookupCache.FullNamesFromLastSession.TryGetValue(cacheKey, out var fullName))
            {
                usedSessionMapping = !string.Equals(fullName, typeName, StringComparison.Ordinal);
                typeName = fullName;
            }
            __state = (originalTypeName, namespaceIfAmbiguous, cacheKey, false, usedSessionMapping);
            return true;
        }

        /// <summary>
        /// 後置處理：若為非快取查詢，將結果寫入執行期和 session 的名稱映射快取。
        /// 上次 session 的對照查不到型別時（例如 mod 更新後類別改名），捨棄該對照並以原名重查。
        /// </summary>
        public static void Postfix(ref Type __result, (string originalTypeName, string namespaceIfAmbiguous, string cacheKey, bool isCached, bool usedSessionMapping) __state)
        {
            if (__result == null && __state.usedSessionMapping)
            {
                // 沒有命名空間時 cacheKey 就是原名，因此只需移除這一個鍵。
                TypeLookupCache.FullNamesFromLastSession.TryRemove(__state.cacheKey, out _);
                resolveUncached ??= AccessTools.MethodDelegate<Func<string, string, Type>>(
                    AccessTools.Method(typeof(GenTypes), "GetTypeInAnyAssemblyInt"));
                // 重新進入本補丁：對照已移除，因此會走原始解析並由內層 Postfix 正常記錄結果。
                __result = resolveUncached(__state.originalTypeName, __state.namespaceIfAmbiguous);
                return;
            }

            if (__result != null)
            {
                RecordResolvedType(__result, __state.originalTypeName, __state.namespaceIfAmbiguous, __state.cacheKey, __state.isCached);
            }
        }

        private static void RecordResolvedType(Type result, string originalTypeName, string namespaceIfAmbiguous, string cacheKey, bool isCached)
        {
            var fullName = result.FullName;
            if (string.IsNullOrEmpty(fullName))
            {
                return;
            }
            if (!isCached)
            {
                cachedResults[cacheKey] = result;
                if (!string.Equals(fullName, originalTypeName, StringComparison.Ordinal))
                {
                    cachedResults[MakeCacheKey(fullName, namespaceIfAmbiguous: null)] = result;
                    if (!string.IsNullOrEmpty(namespaceIfAmbiguous))
                    {
                        cachedResults[MakeCacheKey(fullName, namespaceIfAmbiguous)] = result;
                    }
                }
            }
            if (!string.Equals(originalTypeName, fullName, StringComparison.Ordinal))
            {
                loadedTypesThisSession[cacheKey] = fullName;
            }
        }

        /// <summary>
        /// 原版 GetTypeInAnyAssemblyInt 第一步就是不看命名空間、以原名查詢，查到即回傳；
        /// XML 的 Class= 屬性都會帶命名空間提示，因此帶提示的完整名稱也能直接使用 FullName 預熱的結果。
        /// 只接受 FullName 與查詢名稱完全相同的項目：其餘項目可能經命名空間探測才解析出來，換個提示結果就可能不同。
        /// </summary>
        private static bool TryGetExactFullName(string typeName, string namespaceIfAmbiguous, out Type result)
        {
            if (!string.IsNullOrEmpty(namespaceIfAmbiguous) && !string.IsNullOrEmpty(typeName)
                && cachedResults.TryGetValue(typeName, out result)
                && string.Equals(result.FullName, typeName, StringComparison.Ordinal))
            {
                return true;
            }
            result = null;
            return false;
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
