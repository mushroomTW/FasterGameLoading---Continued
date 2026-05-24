using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
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
        public static Dictionary<Type, HashSet<Type>> keyValuePairs = new Dictionary<Type, HashSet<Type>>();

        static GenTypes_AllLeafSubclasses_Patch()
        {
            CacheResetter.Register(() => ClearCache());
        }

        public static void ClearCache()
        {
            keyValuePairs.Clear();
        }

        public static bool Prefix(ref IEnumerable<Type> __result, Type baseType)
        {
            if (!keyValuePairs.TryGetValue(baseType, out var final))
            {
                var subClasses = baseType.AllSubclasses().ToHashSet();
                var typesWithSubclasses = subClasses
                    .Select(t => t.BaseType)
                    .Where(t => t != null && subClasses.Contains(t))
                    .ToHashSet();

                // 葉子 = 沒有任何其他子類別以它為基底的非抽象型別。
                final = subClasses.Where(t => !t.IsAbstract && !typesWithSubclasses.Contains(t)).ToHashSet();
                keyValuePairs[baseType] = final;
            }
            __result = final;
            return false;
        }
    }
}
