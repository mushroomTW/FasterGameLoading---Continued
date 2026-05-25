using HarmonyLib;
using System.Collections.Generic;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 ModAssetBundlesHandler.ReloadAll，確保每個 handler 只執行一次。
    /// 提早載入階段可能重複呼叫 ReloadAll，此 patch 追蹤已處理的 handler 以避免重複的 I/O 操作。
    /// </summary>
    [HarmonyPatch(typeof(ModAssetBundlesHandler), "ReloadAll")]
    public static class ModAssetBundlesHandler_ReloadAll_Patch
    {
        // 追蹤已完成 ReloadAll 的 handler，確保第一次呼叫一定放行
        public static HashSet<ModAssetBundlesHandler> reloadedHandlers = new();

        static ModAssetBundlesHandler_ReloadAll_Patch()
        {
            CacheResetter.Register(() => reloadedHandlers.Clear());
        }

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
