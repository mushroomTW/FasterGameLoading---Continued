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

        public static bool AllGraphicLoaded = false;
        private Stopwatch stopwatch = new();
        private Queue<ModContentPack> pendingEarlyLoads;
        private bool earlyLoadingComplete;
        private bool ElapsedMaxImpact => (float)stopwatch.ElapsedTicks / Stopwatch.Frequency >= MaxImpactThisFrame;
        public void LateUpdate()
        {
            if (earlyLoadingComplete || !FasterGameLoadingSettings.earlyModContentLoading)
                return;

            if (pendingEarlyLoads == null)
            {
                pendingEarlyLoads = new Queue<ModContentPack>(
                    LoadedModManager.RunningMods.Where(x =>
                        !ModContentPack_ReloadContentInt_Patch.loadedMods.Contains(x)));
            }

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
                return; // 每幀只載入一個 mod
            }

            earlyLoadingComplete = true;
        }

        public void ResetEarlyLoading()
        {
            pendingEarlyLoads = null;
            earlyLoadingComplete = false;
        }

        public IEnumerator PerformActions()
        {
            stopwatch.Start();
            var count = 0;
            Log.Message("Starting loading graphics: " + graphicsToLoad.Count + " - " + DateTime.Now.ToString());
            List<ThingDef> loadedDefs = [];
            while (graphicsToLoad.Count > 0)
            {
                if (UnityData.IsInMainThread is false)
                {
                    yield return 0;
                }
                var (def, action) = graphicsToLoad.Dequeue();
                try
                {
                    action();
                    loadedDefs.Add(def);
                }
                catch (Exception ex)
                {
                    Log.Warning("Error loading graphic for " + def + ": " + ex);
                }
                count++;
                if (ElapsedMaxImpact)
                {
                    count = 0;
                    yield return 0;
                    stopwatch.Restart();
                }

                if (def.plant != null)
                {
                    def.plant.PostLoadSpecial(def);
                }
            }
            Log.Message("Finished loading graphics - " + DateTime.Now.ToString());
            if (FasterGameLoadingSettings.StaticAtlasesBaking)
            {
                AllGraphicLoaded = true;//Icon Doesn't matter
                Log.Message("Starting baking StaticAtlases - " + DateTime.Now.ToString());
                #region BakeAtlas (Adaptive)
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

                float measuredBakeSpeed_PixelsPerSecond = 2_000_000f;
                int adaptivePixelsPerSlice = INITIAL_PIXELS_PER_SLICE;

                var bakeStopwatch = new Stopwatch();

                var buildQueueSnapshot = GlobalTextureAtlasManager.buildQueue.ToList();
                foreach (var kvp in buildQueueSnapshot)
                {
                    var key = kvp.Key;
                    var allTexturesForThisGroup = kvp.Value.Item1.ToList();

                    int pixelsInCurrentSlice = 0;
                    var batchForNextBake = new List<(Texture2D main, Texture2D mask)>();

                    foreach (Texture2D texture in allTexturesForThisGroup)
                    {
                        if (texture == null) continue;
                        Texture2D mask = key.hasMask && GlobalTextureAtlasManager.buildQueueMasks.TryGetValue(texture, out var m) ? m : null;
                        batchForNextBake.Add((texture, mask));
                        pixelsInCurrentSlice += texture.width * texture.height;
                        if (pixelsInCurrentSlice >= adaptivePixelsPerSlice)
                        {
                            FlushBatch();
                            yield return null;
                            batchForNextBake.Clear();
                            pixelsInCurrentSlice = 0;
                        }
                    }
                    if (batchForNextBake.Count > 0)
                    {
                        FlushBatch();

                        yield return null;
                    }
                    void FlushBatch()
                    {
                        try
                        {
                            var staticTextureAtlas = new StaticTextureAtlas(key);
                            // Always call Insert() to register textures in insertedTextures list
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

                            GlobalTextureAtlasManager.staticTextureAtlases.Add(staticTextureAtlas);
                            double secondsElapsed = bakeStopwatch.Elapsed.TotalSeconds;
                            if (secondsElapsed > 0)
                            {
                                float latestBakeSpeed = (float)(pixelsInCurrentSlice / secondsElapsed);
                                measuredBakeSpeed_PixelsPerSecond = Mathf.Lerp(measuredBakeSpeed_PixelsPerSecond, latestBakeSpeed, ADAPTATION_FACTOR);
                                float newSliceSize = measuredBakeSpeed_PixelsPerSecond * TARGET_BAKE_TIME_SECONDS;
                                adaptivePixelsPerSlice = (int)
                                    (((int)Mathf.Clamp(newSliceSize, MIN_PIXELS_PER_SLICE, MAX_PIXELS_PER_SLICE))
                                    .FloorToPowerOfTwo() * PACK_DENSITY);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning("[FasterGameLoading] Error baking atlas batch: " + ex);
                        }
                    }
                }
                // Prevent vanilla BakeStaticAtlases from re-processing the same queue
                GlobalTextureAtlasManager.buildQueue.Clear();
                GlobalTextureAtlasManager.buildQueueMasks.Clear();
                #endregion
                Log.Message("Finished baking StaticAtlases - " + DateTime.Now.ToString());
            }
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

            count = 0;

            Log.Message("Starting loading icons: " + iconsToLoad.Count + " - " + DateTime.Now.ToString());
            while (iconsToLoad.Count > 0)
            {
                if (UnityData.IsInMainThread is false)
                {
                    yield return 0;
                }
                var (def, action) = iconsToLoad.Dequeue();
                if (def.uiIcon == BaseContent.BadTex)
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning("Error loading icon for " + def + ": " + ex);
                    }
                    count++;

                    if (ElapsedMaxImpact)
                    {
                        count = 0;
                        yield return 0;
                        stopwatch.Restart();
                    }
                }
            }

            Log.Message("Finished loading icons - " + DateTime.Now.ToString());

            count = 0;
            Log.Message("Starting resolving SubSoundDefs: " + subSoundDefToResolve.Count + " - " + DateTime.Now.ToString());
            while (subSoundDefToResolve.Count > 0)
            {
                var (def, action) = subSoundDefToResolve.Dequeue();
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Warning("Error resolving AudioGrain for " + def + ": " + ex);
                }
                count++;
                if (ElapsedMaxImpact)
                {
                    count = 0;
                    yield return 0;
                    stopwatch.Restart();
                }
            }
            SoundStarter_Patch.Unpatch();
            Log.Message("Finished resolving SubSoundDefs - " + DateTime.Now.ToString());

            stopwatch.Stop();
            this.enabled = false;
            yield return null;
        }

        public void Error(string message, Exception ex)
        {
            Log.Error(message + " - " + ex + " - " + new StackTrace());
        }
    }
}

