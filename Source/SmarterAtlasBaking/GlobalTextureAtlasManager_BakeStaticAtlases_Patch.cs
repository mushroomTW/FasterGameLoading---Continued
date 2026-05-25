using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 GlobalTextureAtlasManager.BakeStaticAtlases，根據模組設定決定烘焙策略：
    /// - 如果延遲視覺效果尚未載入完成：跳過（稍後由 DelayedActions 處理）
    /// - 如果自適應烘焙關閉：放行原始流程
    /// - 如果自適應烘焙失敗：放行原始流程作為 fallback
    /// </summary>
    [HarmonyPatch(typeof(GlobalTextureAtlasManager), "BakeStaticAtlases")]
    public static class GlobalTextureAtlasManager_BakeStaticAtlases_Patch
    {
        public static bool Prefix()
        {
            // 沒有啟用延遲圖形載入時，不需要阻擋烘焙流程
            if (!FasterGameLoadingSettings.DelayGraphicLoading)
            {
                // 但仍可啟用自適應烘焙
                if (!FasterGameLoadingSettings.StaticAtlasesBaking)
                    return true;
                return DelayedActions.AdaptiveStaticAtlasBakeFailed;
            }

            if (!DelayedActions.AllDeferredVisualsLoaded)
            {
                return false;
            }

            if (!FasterGameLoadingSettings.StaticAtlasesBaking)
            {
                return true;
            }

            return DelayedActions.AdaptiveStaticAtlasBakeFailed;
        }
    }
}

