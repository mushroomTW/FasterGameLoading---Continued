using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Verse;
using Color = UnityEngine.Color;

namespace FasterGameLoading
{
    /// <summary>
    /// 紋理降質的整合調度器。協同 CacheManager、Scanner、Resizer 及 PngUtils 執行降質流程。
    /// </summary>
    public class TextureResize
    {
        /// <summary>紋理分類，用於決定降質目標尺寸。</summary>
        public enum TextureType
        {
            None, Building, Pawn, Weapon, Apparel, Item, Plant, Tree, Terrain, Mote, Filth, Projectile, UI, Other
        }

        private readonly TextureCacheManager cacheManager;
        private readonly TextureScanner scanner;

        private long lastOriginalPixelCount;
        private long lastDownscaledPixelCount;

        /// <summary>單一紋理的縮放候選資訊。</summary>
        private struct TextureResizeCandidate
        {
            public Texture source;
            public string path;
            public int targetSize;
            public int originalWidth;
            public int originalHeight;
        }

        public TextureResize(TextureCacheManager cacheManager)
        {
            this.cacheManager = cacheManager;
            this.scanner = new TextureScanner();
        }

        // ════════════════════════════════════════════════════════════════
        //  主要流程
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// 執行完整紋理降質流程：
        /// 掃描所有已載入的紋理 → 計算縮放候選 → 批次降質 → 替換快取目錄。
        /// 失敗時自動還原先前的快取狀態。
        /// </summary>
        public void DoTextureResizing()
        {
            // 透過執行緒安全介面取得快照，避免與 TextureCacheManager 內部的 cacheLock 競爭
            var previousCacheMap = cacheManager.GetResizedTextureCacheCopy();
            var previousCacheDirectory = cacheManager.CacheDirectory;
            var stagingDirectory = cacheManager.BuildCacheDirectory(FGLConsts.TextureCacheStagingDir);
            lastOriginalPixelCount = 0;
            lastDownscaledPixelCount = 0;
            try
            {
                cacheManager.SetupResizeStagingDirectory(stagingDirectory);
                scanner.BuildTextureScanData();

                var texturesToResize = BuildResizeCandidates();

                if (texturesToResize.Any())
                {
                    foreach (var entry in texturesToResize)
                    {
                        ResizeTexture(entry);
                    }
                    FGLLog.Message($"Downscaled {texturesToResize.Count} textures (cached, originals untouched)");
                    cacheManager.ReplaceTextureCacheDirectory(stagingDirectory);
                    LogResizeSummary(texturesToResize.Count);
                }
                else
                {
                    cacheManager.RestorePreviousCacheState(previousCacheMap, previousCacheDirectory, stagingDirectory);
                }

                // 持久化快取對照表
                LoadedModManager.GetMod<FasterGameLoadingMod>().WriteSettings();
            }
            catch (Exception ex)
            {
                FGLLog.Error("Texture downscale failed, keeping previous cache:", ex);
                cacheManager.RestorePreviousCacheState(previousCacheMap, previousCacheDirectory, stagingDirectory);
            }
            finally
            {
                scanner.ClearTextureScanData();
            }
        }

        /// <summary>
        /// 篩選需要縮放的紋理：尺寸超過目標尺寸且 drawSize 總和 ≤ 8 的紋理。
        /// </summary>
        private List<TextureResizeCandidate> BuildResizeCandidates()
        {
            var texturesToResize = new List<TextureResizeCandidate>();
            foreach (var texture in scanner.texturesByPaths)
            {
                if (AdaptiveBakingSkipList.IsProtectedModTexturePath(texture.Value)) continue;

                var sourceWidth = texture.Key.width;
                var sourceHeight = texture.Key.height;
                PngUtils.TryGetImageDimensions(texture.Value, ref sourceWidth, ref sourceHeight);

                if (scanner.texturesByDefs.TryGetValue(texture.Key, out var value)
                    && TextureResizer.TryGetResizeTarget(texture.Key, value.Key, out var targetSize)
                    && (sourceWidth > targetSize || sourceHeight > targetSize))
                {
                    texturesToResize.Add(new TextureResizeCandidate
                    {
                        source = texture.Key,
                        path = texture.Value,
                        targetSize = targetSize,
                        originalWidth = sourceWidth,
                        originalHeight = sourceHeight
                    });
                }
            }
            return texturesToResize;
        }

        /// <summary>
        /// 執行單一紋理降質：載入原始 PNG → 按比例縮放 → 輸出 PNG 到快取目錄。
        /// </summary>
        private void ResizeTexture(TextureResizeCandidate candidate)
        {
            Texture2D originalTexture = null;
            try
            {
                var resizeSource = TryLoadOriginalTexture(candidate.path, out originalTexture) ? originalTexture : candidate.source;
                if (resizeSource == null || resizeSource.width <= 0 || resizeSource.height <= 0) return;

                var sourceWidth = originalTexture != null ? resizeSource.width : candidate.originalWidth;
                var sourceHeight = originalTexture != null ? resizeSource.height : candidate.originalHeight;
                double ratio = sourceHeight > sourceWidth
                    ? (double)candidate.targetSize / sourceHeight
                    : (double)candidate.targetSize / sourceWidth;
                int newWidth = Math.Max(1, (int)Math.Round(sourceWidth * ratio));
                int newHeight = Math.Max(1, (int)Math.Round(sourceHeight * ratio));
                lastOriginalPixelCount += (long)sourceWidth * sourceHeight;
                lastDownscaledPixelCount += (long)newWidth * newHeight;
                var cachePath = cacheManager.GetCachePath(candidate.path);
                IORetryHelper.WriteAllBytesWithRetry(cachePath, TextureResizer.ResizeTextureToPng(resizeSource, newWidth, newHeight));
                cacheManager.SetCacheEntry(candidate.path, cachePath);
            }
            catch (IOException ex)
            {
                FGLLog.Error($"Failed to downscale texture {candidate.path}:", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                FGLLog.Error($"Failed to downscale texture {candidate.path}:", ex);
            }
            catch (Exception ex)
            {
                FGLLog.Warning($"Failed to downscale texture {candidate.path}:", ex);
                cacheManager.RemoveCachedTexturePath(candidate.path);
            }
            finally
            {
                if (originalTexture != null) TextureResizer.DestroyTemporaryUnityObject(originalTexture);
            }
        }

        /// <summary>嘗試從磁碟載入原始 PNG 紋理。失敗時回傳 false，由呼叫端使用記憶體中的版本。</summary>
        private bool TryLoadOriginalTexture(string path, out Texture2D texture)
        {
            texture = null;
            try
            {
                if (!File.Exists(path)) return false;
                var data = File.ReadAllBytes(path);
                texture = new Texture2D(FGLConsts.PlaceholderTextureSize, FGLConsts.PlaceholderTextureSize, TextureFormat.RGBA32, false);
                if (texture.LoadImage(data) && texture.width > 0 && texture.height > 0)
                {
                    texture.name = Path.GetFileNameWithoutExtension(path);
                    return true;
                }
            }
            catch (Exception ex)
            {
                // 無法從磁碟載入原始紋理，caller 會改用記憶體中的版本作為 fallback
                FGLLog.Warning("Cannot load original texture from disk, using in-memory copy:", ex);
            }

            if (texture != null) { TextureResizer.DestroyTemporaryUnityObject(texture); texture = null; }
            return false;
        }

        private void LogResizeSummary(int resizedCount)
        {
            FGLLog.Message($"Texture downscale summary: resized={resizedCount}, sourcePixels={lastOriginalPixelCount}, downscaledPixels={lastDownscaledPixelCount}, estimatedSaved={FormatBytes((lastOriginalPixelCount - lastDownscaledPixelCount) * 4)}");
        }

        private static string FormatBytes(long bytes)
        {
            return (bytes / 1024f / 1024f).ToString("F1") + " MiB";
        }

        // ════════════════════════════════════════════════════════════════
        //  服裝圖形輔助
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// 嘗試取得指定身體類型的穿著外觀圖形。
        /// 考量發育階段過濾、圖層類型、pack 渲染模式等。
        /// </summary>
        public static bool TryGetGraphicApparel(ThingDef def, string wornGraphicPath, BodyTypeDef bodyType, out Graphic rec)
        {
            if (bodyType == BodyTypeDefOf.Baby && def.apparel.developmentalStageFilter.HasFlag(DevelopmentalStage.Baby) is false
                || bodyType == BodyTypeDefOf.Child && def.apparel.developmentalStageFilter.HasFlag(DevelopmentalStage.Child) is false)
            {
                rec = null;
                return false;
            }
            if (wornGraphicPath.NullOrEmpty())
            {
                rec = null;
                return false;
            }
            string path = ((def.apparel.LastLayer != ApparelLayerDefOf.Overhead && def.apparel.LastLayer != ApparelLayerDefOf.EyeCover
                && !RenderAsPack(def) && !(wornGraphicPath == BaseContent.PlaceholderImagePath)
                && !(wornGraphicPath == BaseContent.PlaceholderGearImagePath)) ? (wornGraphicPath + "_" + bodyType.defName)
                : wornGraphicPath);
            Shader shader = ShaderDatabase.Cutout;
            if (def.apparel.useWornGraphicMask)
            {
                shader = ShaderDatabase.CutoutComplex;
            }
            rec = GraphicDatabase.Get<Graphic_Multi>(path, shader, def.graphicData.drawSize, Color.white);
            return true;
        }

        /// <summary>判斷此服裝是否需要以 pack 模式渲染（Utility 層預設為 true）。</summary>
        public static bool RenderAsPack(ThingDef def)
        {
            if (def.apparel.LastLayer.IsUtilityLayer)
            {
                if (def.apparel.wornGraphicData != null)
                {
                    return def.apparel.wornGraphicData.renderUtilityAsPack;
                }
                return true;
            }
            return false;
        }

        // ════════════════════════════════════════════════════════════════
        //  紋理類型判斷
        // ════════════════════════════════════════════════════════════════

        /// <summary>根據 ThingDef 的屬性判斷其紋理類型。</summary>
        internal static TextureType GetTextureType(ThingDef thingDef)
        {
            if (thingDef.building != null) return TextureType.Building;
            if (thingDef.IsWeapon) return TextureType.Weapon;
            if (thingDef.IsApparel) return TextureType.Apparel;
            if (thingDef.IsPlant)
            {
                return thingDef.plant.IsTree ? TextureType.Tree : TextureType.Plant;
            }
            if (thingDef.projectile != null) return TextureType.Projectile;
            if (thingDef.category == ThingCategory.Mote) return TextureType.Mote;
            if (thingDef.category == ThingCategory.Filth) return TextureType.Filth;
            if (thingDef.category == ThingCategory.Item) return TextureType.Item;
            if (thingDef.race != null) return TextureType.Pawn;
            return TextureType.None;
        }

        // ════════════════════════════════════════════════════════════════
        //  向後相容性代理 API
        // ════════════════════════════════════════════════════════════════

        /// <summary>相容性代理：目前快取的紋理數量。</summary>
        public int CacheCount => cacheManager.CacheCount;

        /// <summary>相容性代理：清除所有紋理快取。</summary>
        public void ClearCache() => cacheManager.ClearCache();
    }
}
