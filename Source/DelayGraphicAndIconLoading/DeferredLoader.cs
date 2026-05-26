using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace FasterGameLoading
{
    /// <summary>
    /// 負責批次延遲載入圖形、圖示、更新地圖網格、以及解析音效的協程邏輯。
    /// </summary>
    public static class DeferredLoader
    {
        /// <summary>
        /// 在時間預算內批次載入延遲的圖形紋理。
        /// </summary>
        /// <param name="delayedActions">延遲動作管理器實例，提供時間預算與佇列存取。</param>
        /// <param name="loadedDefs">存放已載入的 ThingDef 清單，供後續更新地圖網格使用。</param>
        public static IEnumerator LoadDeferredGraphicsCoroutine(DelayedActions delayedActions, List<ThingDef> loadedDefs)
        {
            delayedActions.StartStopwatch();
            FGLLog.Message("Starting deferred graphics: " + delayedActions.GraphicsToLoadCount);
            while (delayedActions.GraphicsToLoadCount > 0)
            {
                if (UnityData.IsInMainThread is false)
                {
                    yield return 0;
                }
                while (delayedActions.GraphicsToLoadCount > 0 && !delayedActions.IsOverBudget)
                {
                    ThingDef def;
                    Action action;
                    if (!delayedActions.TryDequeueGraphic(out def, out action))
                        break;

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

                if (delayedActions.GraphicsToLoadCount > 0)
                {
                    yield return 0;
                    delayedActions.RestartStopwatch();
                }
            }
            FGLLog.Message("Deferred graphics loaded");
        }

        /// <summary>
        /// 將已載入的圖形標記為需要重新繪製地圖網格，
        /// 確保延遲載入的圖形在地圖上立即顯示。
        /// </summary>
        /// <param name="loadedDefs">已載入的 ThingDef 清單。</param>
        public static IEnumerator UpdateMapMeshForLoadedDefs(List<ThingDef> loadedDefs)
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
        /// </summary>
        /// <param name="delayedActions">延遲動作管理器實例。</param>
        public static IEnumerator LoadDeferredIconsCoroutine(DelayedActions delayedActions)
        {
            delayedActions.RestartStopwatch();
            FGLLog.Message("Starting deferred icons: " + delayedActions.IconsToLoadCount);
            while (delayedActions.IconsToLoadCount > 0)
            {
                if (UnityData.IsInMainThread is false)
                {
                    yield return 0;
                }
                while (delayedActions.IconsToLoadCount > 0 && !delayedActions.IsOverBudget)
                {
                    BuildableDef def;
                    Action action;
                    if (!delayedActions.TryDequeueIcon(out def, out action))
                        break;

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

                if (delayedActions.IconsToLoadCount > 0)
                {
                    yield return 0;
                    delayedActions.RestartStopwatch();
                }
            }
            FGLLog.Message("Deferred icons loaded");
        }

        /// <summary>
        /// 在時間預算內批次解析延遲的 SubSoundDef。
        /// 完成後由 World_FinalizeInit_Patch 負責解除 SoundStarter 攔截。
        /// </summary>
        /// <param name="delayedActions">延遲動作管理器實例。</param>
        public static IEnumerator ResolveSubSoundDefsCoroutine(DelayedActions delayedActions)
        {
            delayedActions.RestartStopwatch();
            FGLLog.Message("Starting SubSoundDef resolution: " + delayedActions.SubSoundDefToResolveCount);
            while (delayedActions.SubSoundDefToResolveCount > 0)
            {
                while (delayedActions.SubSoundDefToResolveCount > 0 && !delayedActions.IsOverBudget)
                {
                    SubSoundDef def;
                    Action action;
                    if (!delayedActions.TryDequeueSubSound(out def, out action))
                        break;

                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        FGLLog.Warning("Error resolving AudioGrain for " + def + ": ", ex);
                    }
                }

                if (delayedActions.SubSoundDefToResolveCount > 0)
                {
                    yield return 0;
                    delayedActions.RestartStopwatch();
                }
            }
            SoundStarter_Patch.Unpatch();
            FGLLog.Message("SubSoundDef resolution complete");
        }
    }
}
