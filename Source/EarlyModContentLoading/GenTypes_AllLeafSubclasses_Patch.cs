using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 GenTypes.AllLeafSubclasses 並快取結果。
    /// 葉子子類別 = 某基底型別的子類別中，沒有被其他子類別繼承的型別。
    /// </summary>
    [HarmonyPatch(typeof(GenTypes), "AllLeafSubclasses")]
    public static class GenTypes_AllLeafSubclasses_Patch
    {
        // 使用 ConcurrentDictionary 確保多執行緒下的讀寫安全（Preload 在 Task.Run 背景執行緒中執行）
        public static ConcurrentDictionary<Type, HashSet<Type>> keyValuePairs = new ConcurrentDictionary<Type, HashSet<Type>>();

        static GenTypes_AllLeafSubclasses_Patch()
        {
            CacheResetter.Register(() => ClearCache());
        }

        public static void ClearCache()
        {
            keyValuePairs.Clear();
        }

        /// <summary>
        /// 前置處理：若已經有快取結果則直接回傳，否則運算出該基底型別的所有葉子子類別並加入快取。
        /// 回傳快取的副本（new List）以防呼叫端修改共用快取。
        /// </summary>
        public static bool Prefix(ref IEnumerable<Type> __result, Type baseType)
        {
            if (!keyValuePairs.TryGetValue(baseType, out var final))
            {
                var subClasses = baseType.AllSubclasses().ToHashSet();
                var typesWithSubclasses = subClasses
                    .Select(t => t.BaseType)
                    .Where(t => t != null && subClasses.Contains(t))
                    .ToHashSet();

                // 葉子 = 沒有任何其他子類別以它為基底的型別
                final = subClasses.Where(t => !typesWithSubclasses.Contains(t)).ToHashSet();
                keyValuePairs[baseType] = final;
            }
            // 回傳副本，避免呼叫端修改到共用快取的 HashSet
            __result = new List<Type>(final);
            return false;
        }
    }
}
