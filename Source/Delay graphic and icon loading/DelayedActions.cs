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
    /// 1. 在 LateUpdate 中利用時間預算提早載入 Mod 內容。
    /// 2. 在遊戲進入後依序執行延遲的圖形、圖示、聲音解析以及自適應圖集烘焙。
    /// </summary>
    public class DelayedActions : MonoBehaviour
    {
        // ── 每幀時間預算 ──
        /// <summary>遊戲中每幀最多佔用 8ms，主選單中最多 50ms。</summary>
        public float MaxImpactThisFrame => Current.Game != null ? 0.008f : 0.05f;

        // ── 延遲佇列 ──
        /// <summary>待載入的圖形清單（ThingDef + 其載入委派）。</summary>
        public Queue<(ThingDef def, Action action)> graphicsToLoad = new();
        /// <summary>待載入的圖示清單（BuildableDef + 其載入委派）。</summary>
        public Queue<(BuildableDef def, Action action)> iconsToLoad = new();
        /// <summary>待解析的 SubSoundDef 清單。</summary>
        public Queue<(SubSoundDef def, Action action)> subSoundDefToResolve = new();

        // ── 全域狀態旗標 ──
        /// <summary>所有延遲視覺效果是否已載入完成。</summary>
        public static bool AllDeferredVisualsLoaded = false;
        /// <summary>自適應靜態圖集烘焙是否失敗（fallback 到原始流程）。</summary>
        public static bool AdaptiveStaticAtlasBakeFailed = false;

        /// <summary>靜態建構子：註冊快取重置時的回呼。</summary>
        static DelayedActions()
        {
            CacheResetter.Register(() =>
            {
                AllDeferredVisualsLoaded = false;
                AdaptiveStaticAtlasBakeFailed = false;
            });
        }

        // ── 提早載入狀態 ──
        private Stopwatch stopwatch = new();
        private Queue<ModContentPack> pendingEarlyLoads;
        private bool earlyLoadingComplete;
        private int consecutiveTimeouts;
        private const int TIMEOUT_THRESHOLD = 3;
        private int skipFrames;
        private const int SKIP_FRAME_COUNT = 5;

        /// <summary>目前幀的時間預算是否已耗盡。</summary>
        private bool OverBudget => (float)stopwatch.ElapsedTicks / Stopwatch.Frequency >= MaxImpactThisFrame;

        // ════════════════════════════════════════════════════════════════
        //  提早載入 Mod 內容（LateUpdate 時間預算排程）
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// 每幀執行，利用空閒時間預先載入尚未處理的 Mod 內容。
        /// 每個 Mod 的 ReloadContentInt 完成後立即加入 loadedMods，
        /// 避免正式的 ReloadContentInt 階段重複載入。
        /// 若連續數幀皆超過時間預算，會主動跳過幾幀以讓遊戲維持響應。
        /// </summary>
        public void LateUpdate()
        {
            // earlyModContentLoading 採 camelCase 以相容 loading-progress 的反射查詢，詳見 FasterGameLoadingSettings
            if (earlyLoadingComplete || !FasterGameLoadingSettings.earlyModContentLoading)
                return;

            if (skipFrames > 0)
            {
                skipFrames--;
                return;
            }

            if (pendingEarlyLoads == null)
            {
                var modsToLoad = LoadedModManager.RunningMods
                    .Where(x => !ModContentPack_ReloadContentInt_Patch.loadedMods.Contains(x)
                                && !EarlyLoadSkipList.ShouldSkip(x.PackageIdPlayerFacing))
                    .ToList();
                pendingEarlyLoads = new Queue<ModContentPack>(modsToLoad);
            }

            stopwatch.Restart();
            while (pendingEarlyLoads.Count > 0)
            {
                var modToLoad = pendingEarlyLoads.Dequeue();
                if (ModContentPack_ReloadContentInt_Patch.loadedMods.Contains(modToLoad))
                    continue;
                try
                {
                    modToLoad.ReloadContentInt();
                    ModContentPack_ReloadContentInt_Patch.loadedMods.Add(modToLoad);
                }
                catch (Exception ex)
                {
                    // 載入失敗時不加入 loadedMods，讓正式流程可以重試
                    FGLLog.Warning("Early loading failed for " + modToLoad.PackageIdPlayerFacing + ", will retry in normal flow: ", ex);
                }

                // 用完時間預算就讓出這幀，下幀繼續
                if (OverBudget)
                {
                    consecutiveTimeouts++;
                    if (consecutiveTimeouts >= TIMEOUT_THRESHOLD)
                    {
                        consecutiveTimeouts = 0;
                        skipFrames = SKIP_FRAME_COUNT;
                    }
                    return;
                }
                else
                {
                    consecutiveTimeouts = 0;
                }
            }

            earlyLoadingComplete = true;
        }

        /// <summary>
        /// 重置提早載入狀態（語言切換時由 CacheResetter 觸發）。
        /// </summary>
        public void ResetEarlyLoading()
        {
            pendingEarlyLoads = null;
            earlyLoadingComplete = false;
            AllDeferredVisualsLoaded = false;
            AdaptiveStaticAtlasBakeFailed = false;
            consecutiveTimeouts = 0;
            skipFrames = 0;
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
            yield return LoadDeferredGraphicsCoroutine(loadedDefs);
            yield return BakeDeferredAtlasesCoroutine();
            yield return UpdateMapMeshForLoadedDefs(loadedDefs);
            yield return LoadDeferredIconsCoroutine();
            yield return ResolveSubSoundDefsCoroutine();

            GraphicData_Init_Patch.savedGraphics.Clear();
            stopwatch.Stop();
            this.enabled = false;
            yield return null;
        }

        // ════════════════════════════════════════════════════════════════
        //  子協程 — 圖形載入、圖集烘焙、圖示載入、聲音解析
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// 在時間預算內批次載入延遲的圖形紋理。
        /// 超過時間預算時暫停，下幀繼續。
        /// </summary>
        private IEnumerator LoadDeferredGraphicsCoroutine(List<ThingDef> loadedDefs)
        {
            stopwatch.Start();
            FGLLog.Message("Starting deferred graphics: " + graphicsToLoad.Count);
            while (graphicsToLoad.Count > 0)
            {
                if (UnityData.IsInMainThread is false)
                {
                    yield return 0;
                }
                while (graphicsToLoad.Count > 0 && !OverBudget)
                {
                    var (def, action) = graphicsToLoad.Dequeue();
                    try
                    {
                        action();
                        loadedDefs.Add(def);

                        // 圖形剛載入完成，重新解析 UI 圖示。
                        // BuildableDef.PostLoad 的圖示回呼在 ExecuteWhenFinished 階段以正常時機執行，
                        // 但那時圖形尚未載入，導致 uiIcon 被設為 BadTex。
                        // 現在圖形已載入，重新解析圖示即可得到正確的紋理。
                        if (def.uiIcon == BaseContent.BadTex)
                        {
                            if (def.uiIconPath.NullOrEmpty() is false)
                            {
                                // 有明確的圖示路徑，直接載入
                                def.uiIcon = ContentFinder<Texture2D>.Get(def.uiIconPath, true);
                            }
                            else if (def.graphicData?.Graphic != null)
                            {
                                // 從已初始化的圖形取得 UI 圖示。
                                // 必須使用 Graphic.MatSingle.mainTexture，這是 RimWorld 原始
                                // BuildableDef.PostLoad 中用來設定 uiIcon 的邏輯。
                                // 不能用 ContentFinder.Get(texPath)，因為 Graphic_Multi 等
                                // 多方向圖形的 texPath 只是基礎路徑（如 "Things/Building/Lamp"），
                                // 實際紋理是 "Lamp_south" 等帶有方向後綴的檔案，
                                // ContentFinder 無法找到不含後綴的路徑。
                                var mat = def.graphicData.Graphic.MatSingle;
                                if (mat != null && mat.mainTexture is Texture2D tex
                                    && tex != null && tex != BaseContent.BadTex)
                                {
                                    def.uiIcon = tex;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        FGLLog.Warning("Error loading graphic for " + def + ": ", ex);
                    }
                    def.plant?.PostLoadSpecial(def);
                }

                if (graphicsToLoad.Count > 0)
                {
                    yield return 0;
                    stopwatch.Restart();
                }
            }
            FGLLog.Message("Deferred graphics loaded");
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
                if (FasterGameLoadingSettings.AtlasCaching && StaticAtlasCache.TryLoadFromCache())
                {
                    FGLLog.Message("Static atlases loaded from cache (Raw DXT bytes)");
                }
                else
                {
                    string queueHash = null;
                    if (FasterGameLoadingSettings.AtlasCaching)
                    {
                        queueHash = StaticAtlasCache.ComputeQueueHash();
                    }

                    var adaptiveBake = PerformAdaptiveStaticAtlasBake();
                    while (adaptiveBake.MoveNext())
                    {
                        yield return adaptiveBake.Current;
                    }

                    if (AdaptiveStaticAtlasBakeFailed)
                    {
                        FGLLog.Message("Adaptive bake failed, falling back to vanilla static atlas baking");
                        GlobalTextureAtlasManager.BakeStaticAtlases();
                        FGLLog.Message("Vanilla static atlas baking complete");
                    }

                    if (FasterGameLoadingSettings.AtlasCaching && !AdaptiveStaticAtlasBakeFailed && queueHash != null)
                    {
                        var saveCache = StaticAtlasCache.SaveToCacheCoroutine(
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
                FGLLog.Message("Starting deferred vanilla static atlas baking");
                GlobalTextureAtlasManager.BakeStaticAtlases();
                FGLLog.Message("Deferred vanilla static atlas baking complete");
            }
        }

        /// <summary>
        /// 將已載入的圖形標記為需要重新繪製地圖網格，
        /// 確保延遲載入的圖形在地圖上立即顯示。
        /// </summary>
        private IEnumerator UpdateMapMeshForLoadedDefs(List<ThingDef> loadedDefs)
        {
            try
            {
                if (Current.Game != null)
                {
                    foreach (var map in Find.Maps)
                    {
                        if (map.mapDrawer.sections != null)
                        {
                            foreach (var thing in map.listerThings.ThingsOfDefs(loadedDefs))
                            {
                                map.mapDrawer.MapMeshDirty(thing.Position,
                                    MapMeshFlagDefOf.Things | MapMeshFlagDefOf.Buildings);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FGLLog.Warning("Error updating map mesh: ", ex);
            }
            yield break;
        }

        /// <summary>
        /// 在時間預算內批次載入延遲的圖示紋理。
        /// 只處理 uiIcon 尚未被正確載入的項目（仍為 BadTex）。
        /// </summary>
        private IEnumerator LoadDeferredIconsCoroutine()
        {
            stopwatch.Restart();
            FGLLog.Message("Starting deferred icons: " + iconsToLoad.Count);
            while (iconsToLoad.Count > 0)
            {
                if (UnityData.IsInMainThread is false)
                {
                    yield return 0;
                }
                while (iconsToLoad.Count > 0 && !OverBudget)
                {
                    var (def, action) = iconsToLoad.Dequeue();
                    if (def.uiIcon == BaseContent.BadTex)
                    {
                        try
                        {
                            action();
                        }
                        catch (Exception ex)
                        {
                            FGLLog.Warning("Error loading icon for " + def + ": ", ex);
                        }
                    }
                }

                if (iconsToLoad.Count > 0)
                {
                    yield return 0;
                    stopwatch.Restart();
                }
            }
            FGLLog.Message("Deferred icons loaded");
        }

        /// <summary>
        /// 在時間預算內批次解析延遲的 SubSoundDef。
        /// 完成後由 World_FinalizeInit_Patch 負責解除 SoundStarter 攔截。
        /// </summary>
        private IEnumerator ResolveSubSoundDefsCoroutine()
        {
            stopwatch.Restart();
            FGLLog.Message("Starting SubSoundDef resolution: " + subSoundDefToResolve.Count);
            while (subSoundDefToResolve.Count > 0)
            {
                while (subSoundDefToResolve.Count > 0 && !OverBudget)
                {
                    var (def, action) = subSoundDefToResolve.Dequeue();
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        FGLLog.Warning("Error resolving AudioGrain for " + def + ": ", ex);
                    }
                }

                if (subSoundDefToResolve.Count > 0)
                {
                    yield return 0;
                    stopwatch.Restart();
                }
            }
            SoundStarter_Patch.Unpatch();
            FGLLog.Message("SubSoundDef resolution complete");
        }

        // ════════════════════════════════════════════════════════════════
        //  自適應圖集烘焙
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// 自適應靜態圖集烘焙 — 根據 GPU 烘焙速度動態調整批次大小，
        /// 以「每個 slice 約 8ms」為目標，避免單幀卡頓。
        /// 使用加權移動平均追蹤歷史烘焙速度，並在每 slice 後即時調整。
        /// </summary>
        private IEnumerator PerformAdaptiveStaticAtlasBake()
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

        // ════════════════════════════════════════════════════════════════
        //  輔助方法
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
                AdaptiveStaticAtlasBakeFailed = true;
                FGLLog.Warning("Error baking atlas batch: ", ex);
                return false;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  輔助方法
        // ════════════════════════════════════════════════════════════════

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
