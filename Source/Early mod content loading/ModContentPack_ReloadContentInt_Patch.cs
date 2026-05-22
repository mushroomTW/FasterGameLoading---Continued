using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace FasterGameLoading
{
    [HarmonyPatch(typeof(ModContentPack), "ReloadContentInt")]
    public static class ModContentPack_ReloadContentInt_Patch
    {
        public static HashSet<ModContentPack> loadedMods = new HashSet<ModContentPack>();

        static ModContentPack_ReloadContentInt_Patch()
        {
            CacheResetter.Register(() => loadedMods.Clear());
        }
        public static bool Prefix(ModContentPack __instance)
        {
            if (loadedMods.Contains(__instance)) return false;
            return true;
        }
        public static void Postfix(ModContentPack __instance)
        {
            loadedMods.Add(__instance);

            // 所有 Mod 皆已完成 ReloadContentInt 後，安排 Alien Races 重新掃描
            if (LoadedModManager.RunningMods.All(m => loadedMods.Contains(m)))
            {
                AlienRacesCompat.ScheduleRescan();
            }
        }
    }
}

