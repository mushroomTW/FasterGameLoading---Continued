using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Verse;

namespace FasterGameLoading
{
    [HarmonyPatch(typeof(AccessTools), "AllTypes")]
    public static class AccessTools_AllTypes_Patch
    {
        private static volatile List<Type> allTypesCached;
        private static readonly object typesLock = new();

        public static void Preload()
        {
            Task.Run(() =>
            {
                var types = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(assembly =>
                    {
                        try { return AccessTools.GetTypesFromAssembly(assembly); }
                        catch { return Array.Empty<Type>(); }
                    }).ToList();
                lock (typesLock)
                {
                    allTypesCached = types;
                }
            });
        }

        public static bool Prefix(ref IEnumerable<Type> __result)
        {
            var cached = allTypesCached;
            if (cached != null)
            {
                __result = cached;
                return false;
            }
            lock (typesLock)
            {
                cached = allTypesCached;
                if (cached != null)
                {
                    __result = cached;
                    return false;
                }
                allTypesCached = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(assembly =>
                    {
                        try { return AccessTools.GetTypesFromAssembly(assembly); }
                        catch { return Array.Empty<Type>(); }
                    }).ToList();
                __result = allTypesCached;
                return false;
            }
        }
    }
}