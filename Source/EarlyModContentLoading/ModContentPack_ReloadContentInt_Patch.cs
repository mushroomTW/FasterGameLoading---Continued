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
        // loadedMods 僅在主執行緒上存取（Harmony Prefix/Postfix 由 RimWorld 主執行緒觸發）
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
            DelayedActions.allModClassesCreated = true;
            if (loadedMods.Contains(__instance)) return false;
            return true;
        }

        /// <summary>
        /// 後置處理：將已載入完成的 ModContentPack 標記至已載入清單，並檢查是否需要重新掃描 Alien Races 的貼圖。
        /// </summary>
        public static void Postfix(ModContentPack __instance)
        {
            loadedMods.Add(__instance);

            // 先以數量比對短路，避免每次都對所有 Mod 執行 O(n) Contains 查詢（整體 O(n²)）
            // 數量相符後才執行完整的 All() 確認，確保沒有遺漏
            var runningMods = LoadedModManager.RunningMods;
            if (loadedMods.Count >= runningMods.Count()
                && runningMods.All(m => loadedMods.Contains(m)))
            {
                AlienRacesCompat.ScheduleRescan();
            }
        }
    }
}

