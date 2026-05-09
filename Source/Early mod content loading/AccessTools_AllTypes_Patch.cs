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
        private static Task<List<Type>> allTypesCached;

        public static void Preload()
        {
            if (allTypesCached != null) return;

            allTypesCached = Task.Run(() =>
            {
                var types = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(assembly =>
                    {
                        try { return AccessTools.GetTypesFromAssembly(assembly); }
                        catch { return Array.Empty<Type>(); }
                    }).ToList();
                return types;
            });
        }

        public static bool Prefix(ref IEnumerable<Type> __result)
        {
            if (allTypesCached == null)
            {
                Preload();
            }

            __result = allTypesCached.Result;
            return false;
        }
    }
}