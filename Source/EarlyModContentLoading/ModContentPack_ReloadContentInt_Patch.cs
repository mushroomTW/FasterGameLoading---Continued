using HarmonyLib;
using System.Collections.Generic;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 ModContentPack.ReloadContentInt，避免已被提早載入的 Mod 重複載入。
    /// </summary>
    [HarmonyPatch(typeof(ModContentPack), "ReloadContentInt")]
    public static class ModContentPack_ReloadContentInt_Patch
    {
        // loadedMods 僅在主執行緒存取。
        internal static readonly HashSet<ModContentPack> loadedMods = new HashSet<ModContentPack>();

        static ModContentPack_ReloadContentInt_Patch()
        {
            CacheResetter.Register(() => loadedMods.Clear());
        }

        public static bool Prefix(ModContentPack __instance)
        {
            return !loadedMods.Contains(__instance);
        }

        public static void Postfix(ModContentPack __instance)
        {
            loadedMods.Add(__instance);
        }
    }

    [HarmonyPatch(typeof(LoadedModManager), nameof(LoadedModManager.LoadModContent))]
    public static class LoadedModManager_LoadModContent_Patch
    {
        public static void Postfix()
        {
            DelayedActions.VanillaModContentLoadCompleted = true;
        }
    }
}
