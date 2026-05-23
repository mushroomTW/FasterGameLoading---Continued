using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 GenTypes.AllLeafSubclasses 並快取結果。
    /// 計算給定基底型別的所有葉子子類別（未被其他子類別繼承的型別），
    /// 避免每次查詢時都遍歷所有型別。
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

                // 計數每個型別作為「其他子類別的基底型別」的次數
                var baseTypeCounts = new Dictionary<Type, int>();
                foreach (var sub in subClasses)
                {
                    var directBase = sub.BaseType;
                    if (subClasses.Contains(directBase))
                    {
                        baseTypeCounts[directBase] = baseTypeCounts.TryGetValue(directBase, out var count)
                            ? count + 1
                            : 1;
                    }
                }

                // 沒有被任何子類別引用為基底型別的，即為 leaf
                final = new HashSet<Type>();
                foreach (var sub in subClasses)
                {
                    if (!baseTypeCounts.ContainsKey(sub))
                    {
                        final.Add(sub);
                    }
                }
                keyValuePairs[baseType] = final;
            }
            __result = final;
            return false;
        }
    }
}