using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 自適應靜態圖集烘焙 — 根據 GPU 烘焙速度動態調整批次大小，
    /// 以「每個 slice 約 8ms」為目標，避免單幀卡頓。
    /// 使用加權移動平均追蹤歷史烘焙速度，並在每 slice 後即時調整。
    /// </summary>
    public static class AdaptiveAtlasBaker
    {
        /// <summary>
        /// 自適應靜態圖集烘焙主協程。
        /// </summary>
        /// <param name="delayedActions">延遲動作管理器實例，主要用來回報或獲取狀態。</param>
        public static IEnumerator PerformAdaptiveStaticAtlasBake(DelayedActions delayedActions)
        {
            FGLLog.Message("Starting adaptive static atlas bake");

            const float TARGET_BAKE_TIME_SECONDS = 0.008f;
            const float ADAPTATION_FACTOR = 0.2f;
            // 初始保守估計：1024×1024 像素
            const int INITIAL_PIXELS_PER_SLICE = 1024 * 1024;
            // 圖集小於 1024×1024 對渲染最佳化沒有意義
            const int MIN_PIXELS_PER_SLICE = 1024 * 1024;
            // 高效能 GPU 的上限
            const int MAX_PIXELS_PER_SLICE = 4096 * 4096;
            // 0.7–0.9 可減少圖集中的空白區域
            const float PACK_DENSITY = 0.8f;

            // ── 計算預估烘焙速度 ──
            float measuredBakeSpeed_PixelsPerSecond;
            if (SessionCache.historicalBakeSpeeds.Count == 0)
            {
                // 初次執行：使用保守估計值
                measuredBakeSpeed_PixelsPerSecond = 2_000_000f;
            }
            else
            {
                // 從歷史記錄計算加權移動平均
                float weightedSum = 0f;
                float weightSum = 0f;
                int count = Math.Min(SessionCache.historicalBakeSpeeds.Count, SessionCache.WEIGHTS.Length);
                for (int i = 0; i < count; i++)
                {
                    weightedSum += SessionCache.historicalBakeSpeeds[i] * SessionCache.WEIGHTS[i];
                    weightSum += SessionCache.WEIGHTS[i];
                }
                measuredBakeSpeed_PixelsPerSecond = weightedSum / weightSum;
            }

            int adaptivePixelsPerSlice = INITIAL_PIXELS_PER_SLICE;
            var bakeStopwatch = new Stopwatch();
            var buildQueueSnapshot = GlobalTextureAtlasManager.buildQueue.ToList();
            var atlasesToCommit = new List<StaticTextureAtlas>();

            foreach (var kvp in buildQueueSnapshot)
            {
                var key = kvp.Key;
                var allTexturesForThisGroup = kvp.Value.Item1.ToList();

                int pixelsInCurrentSlice = 0;
                var batchForNextBake = new List<(Texture2D main, Texture2D mask)>();
                var bakedAtlasesForGroup = new List<StaticTextureAtlas>();

                foreach (Texture2D texture in allTexturesForThisGroup)
                {
                    if (texture == null) continue;

                    Texture2D mask = key.hasMask
                        && GlobalTextureAtlasManager.buildQueueMasks.TryGetValue(texture, out var m)
                        ? m : null;

                    batchForNextBake.Add((texture, mask));
                    pixelsInCurrentSlice += texture.width * texture.height;

                    if (pixelsInCurrentSlice >= adaptivePixelsPerSlice)
                    {
                        if (!TryBakeSingleBatch(key, batchForNextBake, bakedAtlasesForGroup,
                                bakeStopwatch, pixelsInCurrentSlice,
                                ref measuredBakeSpeed_PixelsPerSecond, ref adaptivePixelsPerSlice,
                                TARGET_BAKE_TIME_SECONDS, ADAPTATION_FACTOR,
                                MIN_PIXELS_PER_SLICE, MAX_PIXELS_PER_SLICE, PACK_DENSITY))
                        {
                            DelayedActions.AdaptiveStaticAtlasBakeFailed = true;
                            yield break;
                        }

                        yield return null;
                        batchForNextBake.Clear();
                        pixelsInCurrentSlice = 0;
                    }
                }

                // 處理最後一批未滿一個 slice 的紋理
                if (batchForNextBake.Count > 0)
                {
                    if (!TryBakeSingleBatch(key, batchForNextBake, bakedAtlasesForGroup,
                            bakeStopwatch, pixelsInCurrentSlice,
                            ref measuredBakeSpeed_PixelsPerSecond, ref adaptivePixelsPerSlice,
                            TARGET_BAKE_TIME_SECONDS, ADAPTATION_FACTOR,
                            MIN_PIXELS_PER_SLICE, MAX_PIXELS_PER_SLICE, PACK_DENSITY))
                    {
                        DelayedActions.AdaptiveStaticAtlasBakeFailed = true;
                        yield break;
                    }
                    yield return null;
                }

                // 將此 group 的所有烘焙結果放入全域清單
                foreach (var atlas in bakedAtlasesForGroup)
                {
                    atlasesToCommit.Add(atlas);
                }
            }

            // 提交所有烘焙完成的圖集
            foreach (var staticTextureAtlas in atlasesToCommit)
            {
                GlobalTextureAtlasManager.staticTextureAtlases.Add(staticTextureAtlas);
            }

            // 將本次 session 的最終速度記錄到歷史
            SessionCache.historicalBakeSpeeds.Insert(0, measuredBakeSpeed_PixelsPerSecond);
            if (SessionCache.historicalBakeSpeeds.Count > SessionCache.HISTORY_SIZE)
            {
                SessionCache.historicalBakeSpeeds.RemoveAt(SessionCache.HISTORY_SIZE);
            }

            // 清除原始 buildQueue，防止 vanilla 重複處理
            GlobalTextureAtlasManager.buildQueue.Clear();
            GlobalTextureAtlasManager.buildQueueMasks.Clear();
            FGLLog.Message("Adaptive static atlas bake complete");
        }

        private static bool TryBakeSingleBatch(
            TextureAtlasGroupKey key,
            List<(Texture2D main, Texture2D mask)> batch,
            List<StaticTextureAtlas> bakedAtlases,
            Stopwatch bakeStopwatch,
            int pixelsInThisSlice,
            ref float measuredBakeSpeed,
            ref int adaptivePixelsPerSlice,
            float targetBakeTime,
            float adaptationFactor,
            int minPixelsPerSlice,
            int maxPixelsPerSlice,
            float packDensity)
        {
            try
            {
                var atlas = new StaticTextureAtlas(key);
                foreach (var (main, msk) in batch)
                {
                    atlas.Insert(main, msk);
                }

                if (batch.Count == 1)
                {
                    // Bake() 不支援單一紋理，直接設定
                    atlas.colorTexture = batch[0].main;
                    if (key.hasMask)
                    {
                        atlas.maskTexture = batch[0].mask;
                    }
                    atlas.BuildMeshesForUvs([new Rect(0, 0, 1, 1)]);
                    bakeStopwatch.Reset();
                }
                else
                {
                    bakeStopwatch.Restart();
                    atlas.Bake();
                    bakeStopwatch.Stop();
                }

                bakedAtlases.Add(atlas);

                // 根據實際烘焙時間調整下個 slice 的大小
                double secondsElapsed = bakeStopwatch.Elapsed.TotalSeconds;
                if (secondsElapsed > 0)
                {
                    float latestBakeSpeed = (float)(pixelsInThisSlice / secondsElapsed);
                    measuredBakeSpeed = Mathf.Lerp(measuredBakeSpeed, latestBakeSpeed, adaptationFactor);
                    float newSliceSize = measuredBakeSpeed * targetBakeTime;
                    int adjusted = (int)(newSliceSize * packDensity);
                    adaptivePixelsPerSlice = (int)Mathf.Clamp(
                        adjusted.FloorToPowerOfTwo(),
                        minPixelsPerSlice, maxPixelsPerSlice);
                }

                return true;
            }
            catch (Exception ex)
            {
                FGLLog.Warning("Error baking atlas batch: ", ex);
                return false;
            }
        }
    }
}
