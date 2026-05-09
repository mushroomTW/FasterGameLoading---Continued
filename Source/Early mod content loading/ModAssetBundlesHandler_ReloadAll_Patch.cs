using HarmonyLib;
using System.Collections.Generic;
using Verse;

namespace FasterGameLoading
{
    [HarmonyPatch(typeof(ModAssetBundlesHandler), "ReloadAll")]
    public static class ModAssetBundlesHandler_ReloadAll_Patch
    {
        // 追蹤已完成 ReloadAll 的 handler，確保第一次呼叫一定放行
        public static HashSet<ModAssetBundlesHandler> reloadedHandlers = new();

        public static bool Prefix(ModAssetBundlesHandler __instance)
        {
            // 第一次呼叫放行（提取資產），之後的重複呼叫才跳過
            if (reloadedHandlers.Contains(__instance))
                return false;
            return true;
        }

        public static void Postfix(ModAssetBundlesHandler __instance)
        {
            reloadedHandlers.Add(__instance);
        }
    }
}
