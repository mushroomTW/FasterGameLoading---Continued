using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 GlobalTextureAtlasManager.BakeStaticAtlases，根據模組設定決定烘焙策略：
    /// - 如果延遲視覺效果尚未載入完成：跳過（稍後由 DelayedActions 處理）
    /// - 如果沒有啟用延遲載入（DelayGraphicLoading = false）：同步執行自適應烘焙與快取讀寫，不進行推遲，避免非同步競爭造成頭像渲染除以零異常。
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

                // 時序契約：TryLoadFromCache 必須在 PerformAdaptiveStaticAtlasBake 啟動之前執行。
                // AtlasCacheReader.RebuildAtlas 依賴 buildQueue 內容還原紋理與 uvRects 配對；
                // PerformAdaptiveStaticAtlasBake 結束時會清空 buildQueue，若順序顛倒則快取還原失敗。
                if (FasterGameLoadingSettings.AtlasCaching && AtlasCacheReader.TryLoadFromCache())
                {
                    FGLLog.Message("FGL_Log_StaticAtlasesLoadedFromCacheSync".TranslateWithFallback("Static atlases loaded from cache (Raw DXT bytes) - Synchronous"));
                    // 快取命中：buildQueue 已由 CommitAtlasesToManager 清空，不需烘焙也不需寫快取。
                    return;
                }

                string queueHash = null;
                if (FasterGameLoadingSettings.AtlasCaching)
                {
                    queueHash = AtlasHashCalculator.ComputeQueueHash();
                }

                // 項目6：在「確定要烘焙且要寫快取」的窗口設旗標，確保 ApplyTextureCompression
                // 的 Transpiler 替換後的 CustomApply 保持紋理可讀，供 SaveToCacheCoroutine 讀取。
                // 快取命中路徑已在上方 return，不會到達此處，故不會多餘保留 CPU 副本。
                if (FasterGameLoadingSettings.AtlasCaching && queueHash != null)
                {
                    StaticTextureAtlas_ApplyTextureCompression_Patch.KeepTexturesReadable = true;
                }

                // 同步執行自適應烘焙協程（此協程結束後會清空 buildQueue）
                var adaptiveBake = AdaptiveAtlasBaker.PerformAdaptiveStaticAtlasBake(null);
                while (adaptiveBake.MoveNext()) { }

                if (DelayedActions.AdaptiveStaticAtlasBakeFailed)
                {
                    // 烘焙失敗：重設旗標，放行 vanilla 烘焙（不寫快取）
                    StaticTextureAtlas_ApplyTextureCompression_Patch.KeepTexturesReadable = false;
                    FGLLog.Message("FGL_Log_AdaptiveBakeFailedFallbackSync".TranslateWithFallback("Adaptive bake failed, falling back to vanilla static atlas baking - Synchronous"));
                    GlobalTextureAtlasManager.BakeStaticAtlases();
                }
                else if (FasterGameLoadingSettings.AtlasCaching && queueHash != null)
                {
                    // 時序契約：SaveToCacheCoroutine 在此窗口執行，紋理須保持可讀。
                    // KeepTexturesReadable 已於烘焙前設為 true，協程（同步展開）結束後在下方 finally 中重設。
                    var saveCache = AtlasCacheWriter.SaveToCacheCoroutine(
                        GlobalTextureAtlasManager.staticTextureAtlases, queueHash);
                    while (saveCache.MoveNext()) { }
                    // 寫入完成後立即重設，避免後續壓縮呼叫誤保留可讀性
                    StaticTextureAtlas_ApplyTextureCompression_Patch.KeepTexturesReadable = false;
                }
            }
            catch (System.Exception ex)
            {
                FGLLog.Error("FGL_Log_ErrorSyncStaticAtlasBaking".TranslateWithFallback("Error during synchronous static atlas baking: {0}", ex));
                DelayedActions.AdaptiveStaticAtlasBakeFailed = true;
                GlobalTextureAtlasManager.BakeStaticAtlases();
            }
            finally
            {
                // 確保無論成功、失敗或例外，KeepTexturesReadable 旗標都被重設，
                // 避免後續（如 vanilla 烘焙）不必要地保留紋理 CPU 副本。
                StaticTextureAtlas_ApplyTextureCompression_Patch.KeepTexturesReadable = false;
                isBaking = false;
            }
        }
    }
}

