using System;
using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;

namespace FasterGameLoading
{
    /// <summary>
    /// 每個組件只列舉一次型別：<see cref="AccessTools_AllTypes_Patch"/> 的背景預載與
    /// <see cref="GenTypes_GetTypeInAnyAssemblyInt_Patch.WarmupFullNames"/> 共用同一份陣列，不再各自掃一遍。
    /// 兩邊剛好同時列舉同一個組件時各自列舉，不互相等待：GetTypes 可能經 AssemblyResolve 執行其他 mod 的程式碼，
    /// 等待別的執行緒可能形成死結。列舉拋出例外時不快取，下次再試。
    /// 回傳的陣列由所有呼叫端共用，不得修改。
    /// </summary>
    internal static class AssemblyTypesCache
    {
        private static readonly ConcurrentDictionary<Assembly, Type[]> typesByAssembly = new();

        /// <summary>
        /// 取得組件的型別（同 <see cref="AccessTools.GetTypesFromAssembly"/>）。
        /// 動態組件之後仍可能定義新型別，每次都重新列舉。
        /// </summary>
        internal static Type[] Get(Assembly assembly)
        {
            if (assembly.IsDynamic) return AccessTools.GetTypesFromAssembly(assembly);
            if (typesByAssembly.TryGetValue(assembly, out var types)) return types;
            types = AccessTools.GetTypesFromAssembly(assembly);
            return typesByAssembly.GetOrAdd(assembly, types);
        }

        /// <summary>測試用：清除快取，讓下一次查詢重新列舉。</summary>
        internal static void Clear() => typesByAssembly.Clear();
    }
}
