using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace FasterGameLoading
{

    public class DelayedActions : MonoBehaviour
    {
        public float MaxImpactThisFrame => Current.Game != null ? 0.008f : 0.05f;
        public Queue<(ThingDef def, Action action)> graphicsToLoad = new();
        public Queue<(BuildableDef def, Action action)> iconsToLoad = new();
        public Queue<(SubSoundDef def, Action action)> subSoundDefToResolve = new();

        public static bool AllDeferredVisualsLoaded = false;
        public static bool AdaptiveStaticAtlasBakeFailed = false;

        static DelayedActions()
        {
            CacheResetter.Register(() =>
            {
                AllDeferredVisualsLoaded = false;
                AdaptiveStaticAtlasBakeFailed = false;
            });
        }

        private Stopwatch stopwatch = new();
        private Queue<ModContentPack> pendingEarlyLoads;
        private bool earlyLoadingComplete;
        private int consecutiveTimeouts;
        private const int TIMEOUT_THRESHOLD = 3;
        private int skipFrames;
        private const int SKIP_FRAME_COUNT = 5;
        private bool OverBudget => (float)stopwatch.ElapsedTicks / Stopwatch.Frequency >= MaxImpactThisFrame;
        public void LateUpdate()
        {
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
                    .Where(x => !ModContentPack_ReloadContentInt_Patch.loadedMods.Contains(x))
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
                    Log.Warning("[FasterGameLoading] Early loading failed for " + modToLoad.PackageIdPlayerFacing + ", will retry in normal flow: " + ex.Message);
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

        public void ResetEarlyLoading()
        {
            pendingEarlyLoads = null;
            earlyLoadingComplete = false;
            AllDeferredVisualsLoaded = false;
            AdaptiveStaticAtlasBakeFailed = false;
            consecutiveTimeouts = 0;
            skipFrames = 0;
        }

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

        /// <summary>
        /// 在時間預算內批次載入延遲的圖形紋理。
        /// </summary>
        private IEnumerator LoadDeferredGraphicsCoroutine(List<ThingDef> loadedDefs)
        {
            stopwatch.Start();
            int count = 0;
            Log.Message("[FasterGameLoading] Starting loading graphics: " + graphicsToLoad.Count);
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
                    }
                    catch (Exception ex)
                    {
                        Log.Warning("[FasterGameLoading] Error loading graphic for " + def + ": " + ex);
                    }
                    count++;
                    def.plant?.PostLoadSpecial(def);
                }

                if (graphicsToLoad.Count > 0)
                {
                    yield return 0;
                    stopwatch.Restart();
                }
            }
            Log.Message("[FasterGameLoading] Finished loading graphics");
        }

        /// <summary>
        /// 執行靜態圖集烘焙（自適應或快取還原）。
        /// </summary>
        private IEnumerator BakeDeferredAtlasesCoroutine()
        {
            AdaptiveStaticAtlasBakeFailed = false;
            AllDeferredVisualsLoaded = true;
            if (FasterGameLoadingSettings.StaticAtlasesBaking)
            {
                if (FasterGameLoadingSettings.atlasCaching && StaticAtlasCache.TryLoadFromCache())
                {
                    Log.Message("[FasterGameLoading] Static atlases loaded from cache (Raw DXT bytes)");
                }
                else
                {
                    string queueHash = null;
                    if (FasterGameLoadingSettings.atlasCaching)
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
                        Log.Message("[FasterGameLoading] Falling back to deferred vanilla static atlas baking");
                        GlobalTextureAtlasManager.BakeStaticAtlases();
                        Log.Message("[FasterGameLoading] Finished deferred vanilla static atlas baking");
                    }

                    if (FasterGameLoadingSettings.atlasCaching && !AdaptiveStaticAtlasBakeFailed && queueHash != null)
                    {
                        var saveCache = StaticAtlasCache.SaveToCacheCoroutine(GlobalTextureAtlasManager.staticTextureAtlases, queueHash);
                        while (saveCache.MoveNext())
                        {
                            yield return saveCache.Current;
                        }
                    }
                }
            }
            else
            {
                Log.Message("[FasterGameLoading] Starting deferred vanilla static atlas baking");
                GlobalTextureAtlasManager.BakeStaticAtlases();
                Log.Message("[FasterGameLoading] Finished deferred vanilla static atlas baking");
            }
        }

        /// <summary>
        /// 將已載入的圖形標記為需要重新繪製地圖網格。
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
                                map.mapDrawer.MapMeshDirty(thing.Position, MapMeshFlagDefOf.Things | MapMeshFlagDefOf.Buildings);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[FasterGameLoading] Error updating map mesh: " + ex);
            }
            yield break;
        }

        /// <summary>
        /// 在時間預算內批次載入延遲的圖示紋理。
        /// </summary>
        private IEnumerator LoadDeferredIconsCoroutine()
        {
            int count = 0;
            stopwatch.Restart();

            Log.Message("[FasterGameLoading] Starting loading icons: " + iconsToLoad.Count);
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
                            Log.Warning("[FasterGameLoading] Error loading icon for " + def + ": " + ex);
                        }
                        count++;
                    }
                }

                if (iconsToLoad.Count > 0)
                {
                    yield return 0;
                    stopwatch.Restart();
                }
            }

            Log.Message("[FasterGameLoading] Finished loading icons");
        }

        /// <summary>
        /// 在時間預算內批次解析延遲的 SubSoundDef，完成後解除聲音攔截 patch。
        /// </summary>
        private IEnumerator ResolveSubSoundDefsCoroutine()
        {
            int count = 0;
            stopwatch.Restart();
            Log.Message("[FasterGameLoading] Starting resolving SubSoundDefs: " + subSoundDefToResolve.Count);
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
                        Log.Warning("[FasterGameLoading] Error resolving AudioGrain for " + def + ": " + ex);
                    }
                    count++;
                }

                if (subSoundDefToResolve.Count > 0)
                {
                    yield return 0;
                    stopwatch.Restart();
                }
            }
            SoundStarter_Patch.Unpatch();
            Log.Message("[FasterGameLoading] Finished resolving SubSoundDefs");
        }

        private IEnumerator PerformAdaptiveStaticAtlasBake()
        {
            Log.Message("[FasterGameLoading] Starting baking StaticAtlases");
            const float TARGET_BAKE_TIME_SECONDS = 0.008f;
            const float ADAPTATION_FACTOR = 0.2f;
            // Player won't notice this initial lag (Hopefully)
            const int INITIAL_PIXELS_PER_SLICE = 1024 * 1024;
            // I dont think atlas smaller than 1024x makes any sense for render optimization
            // the original 64x will logs out every texture divided
            // use ShowMoreActions/DumpStaticAtlases while in game map to see dumped atlases
            // dont know why we cant use this action in main menu
            const int MIN_PIXELS_PER_SLICE = 1024 * 1024;
            // For those who have good gpus
            const int MAX_PIXELS_PER_SLICE = 4096 * 4096;
            // Personal Experience 0.7-0.9 can reduce empty spaces in a texture atlas
            const float PACK_DENSITY = 0.8f;

            float measuredBakeSpeed_PixelsPerSecond;
            if (SessionCache.historicalBakeSpeeds.Count == 0)
            {
                // First run - use existing conservative estimate
                measuredBakeSpeed_PixelsPerSecond = 2_000_000f;
            }
            else
            {
                // Calculate weighted average from history
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
            List<StaticTextureAtlas> atlasesToCommit = new List<StaticTextureAtlas>();

            foreach (var kvp in buildQueueSnapshot)
            {
                var key = kvp.Key;
                var allTexturesForThisGroup = kvp.Value.Item1.ToList();
                int pixelsInCurrentSlice = 0;
                var batchForNextBake = new List<(Texture2D main, Texture2D mask)>();
                var bakedAtlasesForGroup = new List<StaticTextureAtlas>();

                foreach (Texture2D texture in allTexturesForThisGroup)
                {
                    if (texture == null)
                    {
                        continue;
                    }

                    Texture2D mask = key.hasMask && GlobalTextureAtlasManager.buildQueueMasks.TryGetValue(texture, out var m) ? m : null;
                    batchForNextBake.Add((texture, mask));
                    pixelsInCurrentSlice += texture.width * texture.height;
                    if (pixelsInCurrentSlice >= adaptivePixelsPerSlice)
                    {
                        if (!TryBakeBatch())
                        {
                            yield break;
                        }

                        yield return null;
                        batchForNextBake.Clear();
                        pixelsInCurrentSlice = 0;
                    }
                }

                if (batchForNextBake.Count > 0)
                {
                    if (!TryBakeBatch())
                    {
                        yield break;
                    }

                    yield return null;
                }

                atlasesToCommit.AddRange(bakedAtlasesForGroup);

                bool TryBakeBatch()
                {
                    try
                    {
                        var staticTextureAtlas = new StaticTextureAtlas(key);
                        foreach (var (main, msk) in batchForNextBake)
                        {
                            staticTextureAtlas.Insert(main, msk);
                        }

                        if (batchForNextBake.Count == 1)
                        {
                            // Bake() doesn't work with single texture - set texture directly
                            staticTextureAtlas.colorTexture = batchForNextBake[0].main;
                            if (key.hasMask)
                            {
                                staticTextureAtlas.maskTexture = batchForNextBake[0].mask;
                            }

                            staticTextureAtlas.BuildMeshesForUvs([new(0, 0, 1, 1)]);
                            bakeStopwatch.Reset();
                        }
                        else
                        {
                            bakeStopwatch.Restart();
                            staticTextureAtlas.Bake();
                            bakeStopwatch.Stop();
                        }

                        bakedAtlasesForGroup.Add(staticTextureAtlas);
                        double secondsElapsed = bakeStopwatch.Elapsed.TotalSeconds;
                        if (secondsElapsed > 0)
                        {
                            float latestBakeSpeed = (float)(pixelsInCurrentSlice / secondsElapsed);
                            measuredBakeSpeed_PixelsPerSecond = Mathf.Lerp(measuredBakeSpeed_PixelsPerSecond, latestBakeSpeed, ADAPTATION_FACTOR);
                            float newSliceSize = measuredBakeSpeed_PixelsPerSecond * TARGET_BAKE_TIME_SECONDS;
                            int adjusted = (int)(newSliceSize * PACK_DENSITY);
                            adaptivePixelsPerSlice = (int)Mathf.Clamp(
                                adjusted.FloorToPowerOfTwo(),
                                MIN_PIXELS_PER_SLICE, MAX_PIXELS_PER_SLICE);
                        }

                        return true;
                    }
                    catch (Exception ex)
                    {
                        AdaptiveStaticAtlasBakeFailed = true;
                        Log.Warning("[FasterGameLoading] Error baking atlas batch: " + ex);
                        return false;
                    }
                }
            }

            foreach (var staticTextureAtlas in atlasesToCommit)
            {
                GlobalTextureAtlasManager.staticTextureAtlases.Add(staticTextureAtlas);
            }

            // Add current session's final speed to history
            SessionCache.historicalBakeSpeeds.Insert(0, measuredBakeSpeed_PixelsPerSecond);
            if (SessionCache.historicalBakeSpeeds.Count > SessionCache.HISTORY_SIZE)
            {
                SessionCache.historicalBakeSpeeds.RemoveAt(SessionCache.HISTORY_SIZE);
            }

            // Prevent vanilla BakeStaticAtlases from re-processing the same queue
            GlobalTextureAtlasManager.buildQueue.Clear();
            GlobalTextureAtlasManager.buildQueueMasks.Clear();
            Log.Message("[FasterGameLoading] Finished baking StaticAtlases");
        }

        public void Error(string message, Exception ex)
        {
            Log.Error(message + " - " + ex + " - " + new StackTrace());
        }
    }
}