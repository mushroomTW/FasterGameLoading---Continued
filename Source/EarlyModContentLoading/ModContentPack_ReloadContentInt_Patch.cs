using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 ModContentPack.ReloadContentInt，跳過已在提早載入階段處理完的 Mod。
    /// 所有 Mod 完成後自動觸發 AlienRacesCompat.ScheduleRescan。
    /// </summary>
    [HarmonyPatch(typeof(ModContentPack), "ReloadContentInt")]
    public static class ModContentPack_ReloadContentInt_Patch
    {
        internal static readonly HashSet<ModContentPack> loadedMods = new HashSet<ModContentPack>();

        static ModContentPack_ReloadContentInt_Patch()
        {
            CacheResetter.Register(() => loadedMods.Clear());
        }
        /// <summary>
        /// 前置攔截：若是該 ModContentPack 已經在預載入階段完成處理，則直接跳過 ReloadContentInt 執行。
        /// </summary>
        public static bool Prefix(ModContentPack __instance)
        {
            if (loadedMods.Contains(__instance)) return false;
            return true;
        }

        /// <summary>
        /// 後置處理：將已載入完成的 ModContentPack 標記至已載入清單，並檢查是否需要重新掃描 Alien Races 的貼圖。
        /// </summary>
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

