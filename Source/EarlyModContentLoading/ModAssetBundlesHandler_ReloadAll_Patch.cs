using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 ModAssetBundlesHandler.ReloadAll，確保每個 handler 只執行一次。
    /// 提早載入階段可能重複呼叫 ReloadAll，此 patch 追蹤已處理的 handler 以避免重複的 I/O 操作。
    /// 排在 ChezhouLib 之前：它的 prefix 取代原方法並回傳 false，Harmony 會略過排在後面、回傳 bool 的 prefix，
    /// 排在後面時這裡永遠擋不到第二次呼叫。第一次呼叫照常放行，由 ChezhouLib 接手載入。
    /// </summary>
    [HarmonyPatch(typeof(ModAssetBundlesHandler), "ReloadAll")]
    [HarmonyBefore("ChezhouLib.lib")]
    public static class ModAssetBundlesHandler_ReloadAll_Patch
    {
        // 追蹤已完成 ReloadAll 的 handler，確保第一次呼叫一定放行
        public static ISet<ModAssetBundlesHandler> reloadedHandlers { get; } = new HashSet<ModAssetBundlesHandler>();

        static ModAssetBundlesHandler_ReloadAll_Patch()
        {
            SessionLifecycle.On(LifecyclePhase.LanguageReloading, () => reloadedHandlers.Clear());
        }

        /// <summary>
        /// 前置攔截：若是該 handler 已經載入過，則直接跳過 ReloadAll 執行。
        /// </summary>
        public static bool Prefix(ModAssetBundlesHandler __instance) => !reloadedHandlers.Contains(__instance);

        /// <summary>
        /// 後置處理：將已完成 ReloadAll 的 handler 紀錄到已載入的清單中。
        /// </summary>
        public static void Postfix(ModAssetBundlesHandler __instance)
        {
            reloadedHandlers.Add(__instance);
        }
    }
}
