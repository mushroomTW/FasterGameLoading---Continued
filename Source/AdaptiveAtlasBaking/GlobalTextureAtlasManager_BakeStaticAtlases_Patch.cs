using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 GlobalTextureAtlasManager.BakeStaticAtlases，根據模組設定決定烘焙策略：
    /// - 如果延遲視覺效果尚未載入完成：跳過（稍後由 DelayedActions 處理）
    /// - 如果沒有啟用延遲載入（DelayGraphicLoading = false）：同步執行自適應烘焙，不進行推遲，避免非同步競爭造成頭像渲染除以零異常。
    /// - 如果自適應烘焙關閉：放行原始流程
    /// </summary>
    [HarmonyPatch(typeof(GlobalTextureAtlasManager), "BakeStaticAtlases")]
    public static class GlobalTextureAtlasManager_BakeStaticAtlases_Patch
    {
        private static bool isBaking = false;

        public static bool Prefix()
        {
            if (isBaking)
            {
                return true;
            }

            // 沒有啟用延遲圖形載入時，在啟動階段同步完成所有烘焙，避免非同步競爭
            if (!FasterGameLoadingSettings.DelayGraphicLoading)
            {
                if (!FasterGameLoadingSettings.StaticAtlasesBaking)
                {
                    return true; // 放行 vanilla 同步烘焙
                }

                PerformSynchronousBake();
                return false; // 已同步烘焙完成，跳過 vanilla 原生烘焙
            }

            // 啟用了延遲圖形載入
            if (!DelayedActions.AllDeferredVisualsLoaded)
            {
                return false; // 還沒載入完，跳過
            }

            if (!FasterGameLoadingSettings.StaticAtlasesBaking)
            {
                return true; // 放行 vanilla 烘焙
            }

            return DelayedActions.AdaptiveStaticAtlasBakeFailed;
        }

        private static void PerformSynchronousBake()
        {
            isBaking = true;
            try
            {
                DelayedActions.AdaptiveStaticAtlasBakeFailed = false;
                DelayedActions.AllDeferredVisualsLoaded = true;

                // 同步執行自適應烘焙協程（此協程結束後會清空 buildQueue）
                var adaptiveBake = AdaptiveAtlasBaker.PerformAdaptiveStaticAtlasBake(null);
                while (adaptiveBake.MoveNext()) { }

                if (DelayedActions.AdaptiveStaticAtlasBakeFailed)
                {
                    // 烘焙失敗：放行 vanilla 烘焙
                    FGLLog.Warning("Adaptive bake failed, falling back to vanilla static atlas baking - Synchronous");
                    AtlasBakeDiagnostics.LogPotentialMaskIssues("sync fallback");
                    GlobalTextureAtlasManager.BakeStaticAtlases();
                }
            }
            catch (System.Exception ex)
            {
                FGLLog.Error($"Error during synchronous static atlas baking: {ex}");
                DelayedActions.AdaptiveStaticAtlasBakeFailed = true;
                AtlasBakeDiagnostics.LogPotentialMaskIssues("sync catch fallback");
                GlobalTextureAtlasManager.BakeStaticAtlases();
            }
            finally
            {
                isBaking = false;
            }
        }
    }
}

