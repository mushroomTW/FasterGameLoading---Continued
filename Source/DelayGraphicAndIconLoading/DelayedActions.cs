using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace FasterGameLoading
{
    /// <summary>
    /// 延遲動作管理器 — 掛載於獨立的 GameObject 上，負責：
    /// 1. 協調 EarlyModContentLoader 利用 LateUpdate 提早載入 Mod 內容。
    /// 2. 在遊戲進入後排程與執行延遲的視覺、聲音解析、以及自適應圖集烘焙協程。
    /// </summary>
    public class DelayedActions : MonoBehaviour
    {
        // ── 每幀時間預算 ──
        /// <summary>遊戲中每幀最多佔用 8ms，主選單中最多 50ms。</summary>
        public float MaxImpactThisFrame => Current.Game != null ? 0.008f : 0.05f;

        // ── 延遲佇列 ──
        private readonly Queue<(ThingDef def, Action action)> graphicsToLoad = new();
        private readonly Queue<(BuildableDef def, Action action)> iconsToLoad = new();
        private readonly Queue<(SubSoundDef def, Action action)> subSoundDefToResolve = new();

        public int GraphicsToLoadCount
        {
            get { lock (graphicsToLoad) return graphicsToLoad.Count; }
        }

        public int IconsToLoadCount
        {
            get { lock (iconsToLoad) return iconsToLoad.Count; }
        }

        public int SubSoundDefToResolveCount
        {
            get { lock (subSoundDefToResolve) return subSoundDefToResolve.Count; }
        }

        public void EnqueueGraphic(ThingDef def, Action action)
        {
            lock (graphicsToLoad)
            {
                graphicsToLoad.Enqueue((def, action));
            }
        }

        public void EnqueueIcon(BuildableDef def, Action action)
        {
            lock (iconsToLoad)
            {
                iconsToLoad.Enqueue((def, action));
            }
        }

        public void EnqueueSubSound(SubSoundDef def, Action action)
        {
            lock (subSoundDefToResolve)
            {
                subSoundDefToResolve.Enqueue((def, action));
            }
        }

        public bool TryDequeueGraphic(out ThingDef def, out Action action)
        {
            lock (graphicsToLoad)
            {
                if (graphicsToLoad.Count > 0)
                {
                    (def, action) = graphicsToLoad.Dequeue();
                    return true;
                }
            }
            def = default;
            action = default;
            return false;
        }

        public bool TryDequeueIcon(out BuildableDef def, out Action action)
        {
            lock (iconsToLoad)
            {
                if (iconsToLoad.Count > 0)
                {
                    (def, action) = iconsToLoad.Dequeue();
                    return true;
                }
            }
            def = default;
            action = default;
            return false;
        }

        public bool TryDequeueSubSound(out SubSoundDef def, out Action action)
        {
            lock (subSoundDefToResolve)
            {
                if (subSoundDefToResolve.Count > 0)
                {
                    (def, action) = subSoundDefToResolve.Dequeue();
                    return true;
                }
            }
            def = default;
            action = default;
            return false;
        }

        public void ClearQueues()
        {
            lock (graphicsToLoad) graphicsToLoad.Clear();
            lock (iconsToLoad) iconsToLoad.Clear();
            lock (subSoundDefToResolve) subSoundDefToResolve.Clear();
        }

        // ── 全域狀態旗標 ──
        /// <summary>所有延遲視覺效果是否已載入完成。</summary>
        public static bool AllDeferredVisualsLoaded = false;
        /// <summary>自適應靜態圖集烘焙是否失敗（fallback 到原始流程）。</summary>
        public static bool AdaptiveStaticAtlasBakeFailed = false;
        /// <summary>所有 Mod 的 class 是否已經建立完畢。</summary>
        public static bool allModClassesCreated = false;

        /// <summary>靜態建構子：註冊快取重置時的回呼。</summary>
        static DelayedActions()
        {
            CacheResetter.Register(() =>
            {
                allModClassesCreated = false;
                AllDeferredVisualsLoaded = false;
                AdaptiveStaticAtlasBakeFailed = false;
            });
        }

        // ── 提早載入狀態與處理器 ──
        private Stopwatch stopwatch = new();
        private EarlyModContentLoader earlyModContentLoader = new();

        public bool earlyLoadingComplete => earlyModContentLoader.EarlyLoadingComplete;

        /// <summary>目前幀的時間預算是否已耗盡。</summary>
        public bool IsOverBudget => (float)stopwatch.ElapsedTicks / Stopwatch.Frequency >= MaxImpactThisFrame;

        public void StartStopwatch() => stopwatch.Start();
        public void RestartStopwatch() => stopwatch.Restart();
        public void StopStopwatch() => stopwatch.Stop();

        // ════════════════════════════════════════════════════════════════
        //  提早載入 Mod 內容（LateUpdate 時間預算排程）
        // ════════════════════════════════════════════════════════════════

        public void Update()
        {
            ModContentLoaderTexture2D_LoadTexture_Patch.ProcessPendingMainThreadRequests();
        }

        public void LateUpdate()
        {
            earlyModContentLoader.Update(this);
        }

        /// <summary>
        /// 重置提早載入狀態（語言切換時由 CacheResetter 觸發）。
        /// </summary>
        public void ResetEarlyLoading()
        {
            earlyModContentLoader.Reset();
            allModClassesCreated = false;
            AllDeferredVisualsLoaded = false;
            AdaptiveStaticAtlasBakeFailed = false;
        }

        // ════════════════════════════════════════════════════════════════
        //  遊戲進入後的延遲動作主協程
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// 主協程：依序執行所有延遲的載入動作。
        /// 由 Startup.Postfix 排入 LongEventHandler.toExecuteWhenFinished 觸發。
        /// </summary>
        public IEnumerator PerformActions()
        {
            var loadedDefs = new List<ThingDef>();
            yield return DeferredLoader.LoadDeferredGraphicsCoroutine(this, loadedDefs);
            yield return BakeDeferredAtlasesCoroutine();
            yield return DeferredLoader.UpdateMapMeshForLoadedDefs(loadedDefs);
            yield return DeferredLoader.LoadDeferredIconsCoroutine(this);
            yield return DeferredLoader.ResolveSubSoundDefsCoroutine(this);

            GraphicData_Init_Patch.savedGraphics.Clear();
            stopwatch.Stop();
            this.enabled = false;
            yield return null;
        }

        /// <summary>
        /// 執行靜態圖集烘焙。
        /// 優先嘗試從快取載入，若無快取則執行自適應烘焙，
        /// 烘焙失敗時 fallback 到原始流程。
        /// </summary>
        private IEnumerator BakeDeferredAtlasesCoroutine()
        {
            AdaptiveStaticAtlasBakeFailed = false;
            AllDeferredVisualsLoaded = true;

            if (FasterGameLoadingSettings.StaticAtlasesBaking)
            {
                if (FasterGameLoadingSettings.AtlasCaching && AtlasCacheReader.TryLoadFromCache())
                {
                    FGLLog.Message("FGL_Log_StaticAtlasesLoadedFromCache".TranslateWithFallback("Static atlases loaded from cache (Raw DXT bytes)"));
                }
                else
                {
                    string queueHash = null;
                    if (FasterGameLoadingSettings.AtlasCaching)
                    {
                        queueHash = AtlasHashCalculator.ComputeQueueHash();
                    }

                    var adaptiveBake = AdaptiveAtlasBaker.PerformAdaptiveStaticAtlasBake(this);
                    while (adaptiveBake.MoveNext())
                    {
                        yield return adaptiveBake.Current;
                    }

                    if (AdaptiveStaticAtlasBakeFailed)
                    {
                        FGLLog.Message("FGL_Log_AdaptiveBakeFailedFallback".TranslateWithFallback("Adaptive bake failed, falling back to vanilla static atlas baking"));
                        GlobalTextureAtlasManager.BakeStaticAtlases();
                        FGLLog.Message("FGL_Log_VanillaStaticAtlasBakingComplete".TranslateWithFallback("Vanilla static atlas baking complete"));
                    }

                    if (FasterGameLoadingSettings.AtlasCaching && !AdaptiveStaticAtlasBakeFailed && queueHash != null)
                    {
                        var saveCache = AtlasCacheWriter.SaveToCacheCoroutine(
                            GlobalTextureAtlasManager.staticTextureAtlases, queueHash);
                        while (saveCache.MoveNext())
                        {
                            yield return saveCache.Current;
                        }
                    }
                }
            }
            else
            {
                FGLLog.Message("FGL_Log_StartingDeferredVanillaStaticAtlasBaking".TranslateWithFallback("Starting deferred vanilla static atlas baking"));
                GlobalTextureAtlasManager.BakeStaticAtlases();
                FGLLog.Message("FGL_Log_DeferredVanillaStaticAtlasBakingComplete".TranslateWithFallback("Deferred vanilla static atlas baking complete"));
            }
        }

        /// <summary>
        /// 記錄錯誤訊息，包含完整堆疊追蹤。
        /// </summary>
        [Obsolete("Use FGLLog.Error instead")]
        public void Error(string message, Exception ex)
        {
            FGLLog.Error(message, ex);
        }
    }
}
