using RimWorld;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Verse;
using Color = UnityEngine.Color;

namespace FasterGameLoading
{
    public static class TextureResize
    {
        public static Dictionary<string, string> resizedTextureCache = new Dictionary<string, string>();
        private static readonly object cacheLock = new object();
        private static readonly ConcurrentDictionary<string, string> md5HashCache = new ConcurrentDictionary<string, string>();

        public static string CacheDirectory => Path.Combine(GenFilePaths.SaveDataFolderPath, "FasterGameLoading", "TextureCache");
        private static string BuildCacheDirectory(string suffix) => Path.Combine(GenFilePaths.SaveDataFolderPath, "FasterGameLoading", suffix);
        private static string activeCacheDirectory = CacheDirectory;

        public static string GetCachePath(string originalPath)
        {
            return md5HashCache.GetOrAdd(GetCacheKey(originalPath), key =>
            {
                using (var md5 = MD5.Create())
                {
                    var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
                    var sb = new StringBuilder();
                    foreach (var b in hash)
                    {
                        sb.Append(b.ToString("x2"));
                    }
                    return Path.Combine(activeCacheDirectory, sb.ToString() + ".png");
                }
            });
        }

        private static string GetCacheKey(string originalPath)
        {
            try
            {
                var file = new FileInfo(originalPath);
                if (file.Exists)
                {
                    return originalPath + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
                }
            }
            catch
            {
                // Fall back to the path-only key for virtual or inaccessible files.
            }
            return originalPath;
        }

        public static int CacheCount
        {
            get
            {
                lock (cacheLock)
                {
                    return resizedTextureCache.Count;
                }
            }
        }

        public static bool TryGetCachedTexturePath(string originalPath, out string cachePath)
        {
            lock (cacheLock)
            {
                if (resizedTextureCache.TryGetValue(originalPath, out cachePath)
                    && File.Exists(cachePath)
                    && IsCacheFresh(originalPath, cachePath))
                {
                    return true;
                }

                if (cachePath != null)
                {
                    resizedTextureCache.Remove(originalPath);
                }
            }

            cachePath = null;
            return false;
        }

        private static bool IsCacheFresh(string originalPath, string cachePath)
        {
            try
            {
                if (!File.Exists(originalPath))
                {
                    return true;
                }
                return File.GetLastWriteTimeUtc(cachePath) >= File.GetLastWriteTimeUtc(originalPath);
            }
            catch (Exception)
            {
                // 無法比對檔案時間 → 視為快取失效，強制重新生成
                return false;
            }
        }

        public static void RemoveCachedTexturePath(string originalPath)
        {
            lock (cacheLock)
            {
                resizedTextureCache.Remove(originalPath);
            }
        }

        public static void ClearCache()
        {
            try
            {
                if (Directory.Exists(CacheDirectory))
                {
                    Directory.Delete(CacheDirectory, true);
                }
                lock (cacheLock)
                {
                    resizedTextureCache.Clear();
                }
                Log.Message("[FasterGameLoading] Texture cache cleared.");
            }
            catch (Exception ex)
            {
                Log.Error("[FasterGameLoading] Failed to clear texture cache: " + ex.Message);
            }
        }

        public enum TextureType
        {
            None, Building, Pawn, Weapon, Apparel, Item, Plant, Tree, Terrain, Mote, Filth, Projectile, UI, Other
        }
        public static Dictionary<TextureType, int> targetSizes = new Dictionary<TextureType, int>
        {
            { TextureType.Building, 256 },
            { TextureType.Pawn, 256 },
            { TextureType.Apparel, 128 },
            { TextureType.Weapon, 128 },
            { TextureType.Item, 128 },
            { TextureType.Plant, 128 },
            { TextureType.Tree, 256 },
            { TextureType.Terrain, 1024 },
        };

        public static Dictionary<TextureType, List<KeyValuePair<BuildableDef, string>>> textures = new();
        public static Dictionary<Texture, string> texturesByPaths = new();
        public static Dictionary<Texture, KeyValuePair<BuildableDef, string>> texturesByDefs = new();
        public static Dictionary<Texture, ModContentPack> texturesByMods = new();
        private static long lastOriginalPixelCount;
        private static long lastDownscaledPixelCount;

        private struct TextureResizeCandidate
        {
            public Texture source;
            public string path;
            public int targetSize;
            public int originalWidth;
            public int originalHeight;
        }

        public static void DoTextureResizing()
        {
            var previousCacheMap = new Dictionary<string, string>(resizedTextureCache);
            var previousCacheDirectory = activeCacheDirectory;
            var stagingDirectory = BuildCacheDirectory("TextureCache_New");
            lastOriginalPixelCount = 0;
            lastDownscaledPixelCount = 0;
            try
            {
                SetupResizeStagingDirectory(stagingDirectory);
                BuildTextureScanData();

                var texturesToResize = BuildResizeCandidates();

                if (texturesToResize.Any())
                {
                    foreach (var entry in texturesToResize)
                    {
                        ResizeTexture(entry);
                    }
                    Log.Warning("Downscaled " + texturesToResize.Count + " textures (cached, originals untouched)");
                    ReplaceTextureCacheDirectory(stagingDirectory);
                    LogResizeSummary(texturesToResize.Count);
                }
                else
                {
                    RestorePreviousCacheState(previousCacheMap, previousCacheDirectory, stagingDirectory);
                }

                // 持久化快取對照表
                LoadedModManager.GetMod<FasterGameLoadingMod>().WriteSettings();
            }
            finally
            {
                ClearTextureScanData();
            }
        }

        private static void SetupResizeStagingDirectory(string stagingDirectory)
        {
            md5HashCache.Clear();
            activeCacheDirectory = stagingDirectory;
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, true);
            }
            Directory.CreateDirectory(stagingDirectory);
            lock (cacheLock)
            {
                resizedTextureCache.Clear();
            }

            texturesByPaths.Clear();
            texturesByDefs.Clear();
            texturesByMods.Clear();
        }

        private static void BuildTextureScanData()
        {
            foreach (var value in Enum.GetValues(typeof(TextureType)).Cast<TextureType>())
            {
                textures[value] = new();
            }

            RefreshTexturePathMap();

            foreach (var mod in LoadedModManager.RunningMods)
            {
                foreach (var texture in mod.textures.contentList.Values)
                {
                    texturesByMods[texture] = mod;
                }
            }

            foreach (var pawnKind in DefDatabase<PawnKindDef>.AllDefs)
            {
                var modContent = pawnKind.modContentPack;
                if (modContent != null && modContent.IsOfficialMod)
                {
                    continue;
                }
                if (pawnKind.lifeStages != null)
                {
                    foreach (var lifeStage in pawnKind.lifeStages)
                    {
                        if (lifeStage.bodyGraphicData != null)
                        {
                            AddEntry(TextureType.Pawn, pawnKind.race, lifeStage.bodyGraphicData.Graphic);
                            if (lifeStage.dessicatedBodyGraphicData != null)
                            {
                                AddEntry(TextureType.Pawn, pawnKind.race, lifeStage.dessicatedBodyGraphicData.Graphic);
                            }
                        }
                    }
                }
            }

            foreach (var styleDef in DefDatabase<StyleCategoryDef>.AllDefs)
            {
                var modContent = styleDef.modContentPack;
                if (modContent != null && modContent.IsOfficialMod)
                {
                    continue;
                }
                foreach (var style in styleDef.thingDefStyles)
                {
                    var type = GetTextureType(style.ThingDef);
                    AddEntry(type, style.ThingDef, style.StyleDef.Graphic);
                    if (style.StyleDef.wornGraphicPath.NullOrEmpty() is false)
                    {
                        foreach (var bodyType in DefDatabase<BodyTypeDef>.AllDefs)
                        {
                            if (TryGetGraphicApparel(style.ThingDef, style.StyleDef.wornGraphicPath, bodyType, out var graphic))
                            {
                                AddEntry(type, style.ThingDef, graphic);
                            }
                        }
                    }
                }
            }

            foreach (var def in DefDatabase<BuildableDef>.AllDefs)
            {
                var modContent = def.modContentPack;
                if (modContent != null && modContent.IsOfficialMod)
                {
                    continue;
                }
                if (def is TerrainDef terrain)
                {
                    FillEntry(TextureType.Terrain, def);
                }
                else if (def is ThingDef thingDef)
                {
                    var type = GetTextureType(thingDef);
                    FillEntry(type, thingDef);
                    if (type == TextureType.Apparel)
                    {
                        foreach (var bodyType in DefDatabase<BodyTypeDef>.AllDefs)
                        {
                            if (TryGetGraphicApparel(thingDef, thingDef.apparel.wornGraphicPath, bodyType, out var graphic))
                            {
                                AddEntry(type, def, graphic);
                            }
                            if (thingDef.apparel.wornGraphicPaths != null)
                            {
                                foreach (var path in thingDef.apparel.wornGraphicPaths)
                                {
                                    if (TryGetGraphicApparel(thingDef, path, bodyType, out var graphic2))
                                    {
                                        AddEntry(type, def, graphic2);
                                    }
                                }
                            }
                        }
                    }
                    else if (type == TextureType.Plant || type == TextureType.Tree)
                    {
                        if (thingDef.plant.leaflessGraphic != null)
                        {
                            AddEntry(type, def, thingDef.plant.leaflessGraphic);
                        }
                        if (thingDef.plant.immatureGraphic != null)
                        {
                            AddEntry(type, def, thingDef.plant.immatureGraphic);
                        }
                        if (thingDef.plant.pollutedGraphic != null)
                        {
                            AddEntry(type, def, thingDef.plant.pollutedGraphic);
                        }
                    }
                }
            }
        }

        private static List<TextureResizeCandidate> BuildResizeCandidates()
        {
            var texturesToResize = new List<TextureResizeCandidate>();
            foreach (var texture in texturesByPaths)
            {
                if (texturesByMods.TryGetValue(texture.Key, out var mod))
                {
                    // TODO: 考慮改用可設定的排除清單（LTO Colony Groups 的紋理含 UI 元素，不適合縮放）
                    /*if (mod.PackageIdPlayerFacing == "DerekBickley.LTOColonyGroupsFinal")
                    {
                        continue;
                    }*/
                }
                var sourceWidth = texture.Key.width;
                var sourceHeight = texture.Key.height;
                TryGetImageDimensions(texture.Value, ref sourceWidth, ref sourceHeight);

                if (texturesByDefs.TryGetValue(texture.Key, out var value)
                    && TryGetResizeTarget(texture.Key, value.Key, out var targetSize)
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

        private static void RestorePreviousCacheState(
            Dictionary<string, string> previousCacheMap,
            string previousCacheDirectory,
            string stagingDirectory)
        {
            lock (cacheLock)
            {
                resizedTextureCache = previousCacheMap;
            }
            activeCacheDirectory = previousCacheDirectory;
            md5HashCache.Clear();
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, true);
            }
            Log.Warning("[FasterGameLoading] No full-size textures found to downscale. Existing texture cache was left unchanged.");
            LogResizeSummary(0);
        }

        private static void ReplaceTextureCacheDirectory(string stagingDirectory)
        {
            if (Directory.Exists(CacheDirectory))
            {
                Directory.Delete(CacheDirectory, true);
            }
            Directory.Move(stagingDirectory, CacheDirectory);
            var updatedCacheMap = new Dictionary<string, string>();
            foreach (var kvp in resizedTextureCache)
            {
                updatedCacheMap[kvp.Key] = Path.Combine(CacheDirectory, Path.GetFileName(kvp.Value));
            }
            lock (cacheLock)
            {
                resizedTextureCache = updatedCacheMap;
            }
            activeCacheDirectory = CacheDirectory;
            md5HashCache.Clear();
        }

        private static void ClearTextureScanData()
        {
            texturesByPaths.Clear();
            texturesByDefs.Clear();
            texturesByMods.Clear();
            foreach (var value in textures.Values)
            {
                value.Clear();
            }
        }

        private static void ResizeTexture(TextureResizeCandidate candidate)
        {
            Texture2D originalTexture = null;
            try
            {
                var resizeSource = TryLoadOriginalTexture(candidate.path, out originalTexture) ? originalTexture : candidate.source;
                if (resizeSource == null || resizeSource.width <= 0 || resizeSource.height <= 0)
                    return;

                var sourceWidth = originalTexture != null ? resizeSource.width : candidate.originalWidth;
                var sourceHeight = originalTexture != null ? resizeSource.height : candidate.originalHeight;
                double ratio = sourceHeight > sourceWidth ? (double)candidate.targetSize / sourceHeight : (double)candidate.targetSize / sourceWidth;
                int newWidth = Math.Max(1, (int)Math.Round(sourceWidth * ratio));
                int newHeight = Math.Max(1, (int)Math.Round(sourceHeight * ratio));
                lastOriginalPixelCount += (long)sourceWidth * sourceHeight;
                lastDownscaledPixelCount += (long)newWidth * newHeight;
                var cachePath = GetCachePath(candidate.path);
                File.WriteAllBytes(cachePath, ResizeTextureToPng(resizeSource, newWidth, newHeight));
                lock (cacheLock)
                {
                    resizedTextureCache[candidate.path] = cachePath;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[FasterGameLoading] Failed to resize texture: " + candidate.path + " - " + ex.Message);
            }
            finally
            {
                if (originalTexture != null)
                {
                    DestroyTemporaryUnityObject(originalTexture);
                }
            }
        }

        private static bool TryLoadOriginalTexture(string path, out Texture2D texture)
        {
            texture = null;
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                var data = File.ReadAllBytes(path);
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (texture.LoadImage(data) && texture.width > 0 && texture.height > 0)
                {
                    texture.name = Path.GetFileNameWithoutExtension(path);
                    return true;
                }
            }
            catch (Exception)
            {
                // 載入原始紋理失敗，後續會使用記憶體中的版本 fallback
            }

            if (texture != null)
            {
                DestroyTemporaryUnityObject(texture);
                texture = null;
            }
            return false;
        }

        private static bool TryGetImageDimensions(string path, ref int width, ref int height)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                using (var stream = File.OpenRead(path))
                {
                    return TryReadPngDimensions(stream, ref width, ref height);
                }
            }
            catch (Exception)
            {
                // 無法讀取 PNG 標頭，放棄尺寸判斷
                return false;
            }
        }

        private static bool TryReadPngDimensions(Stream stream, ref int width, ref int height)
        {
            stream.Position = 0;
            var header = new byte[24];
            if (stream.Read(header, 0, header.Length) != header.Length)
            {
                return false;
            }

            if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47)
            {
                return false;
            }

            width = ReadBigEndianInt32(header, 16);
            height = ReadBigEndianInt32(header, 20);
            return width > 0 && height > 0;
        }

        private static int ReadBigEndianInt32(byte[] bytes, int offset)
        {
            return (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        private static void LogResizeSummary(int resizedCount)
        {
            Log.Message("[FasterGameLoading] Texture downscale summary: resized=" + resizedCount
                + ", sourcePixels=" + lastOriginalPixelCount
                + ", downscaledPixels=" + lastDownscaledPixelCount
                + ", estimatedSaved=" + FormatBytes((lastOriginalPixelCount - lastDownscaledPixelCount) * 4));
        }

        private static string FormatBytes(long bytes)
        {
            return (bytes / 1024f / 1024f).ToString("F1") + " MiB";
        }

        private static void DestroyTemporaryUnityObject(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return;
            }

            try
            {
                UnityEngine.Object.DestroyImmediate(obj);
            }
            catch (Exception)
            {
                // DestroyImmediate 失敗（跨執行緒等情境），改用非立即銷毀
                UnityEngine.Object.Destroy(obj);
            }
        }

        private static byte[] ResizeTextureToPng(Texture source, int width, int height)
        {
            var previous = RenderTexture.active;
            var renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            Texture2D readable = null;

            try
            {
                Graphics.Blit(source, renderTexture);
                RenderTexture.active = renderTexture;

                readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readable.Apply(false, false);
                return readable.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(renderTexture);
                if (readable != null)
                {
                    DestroyTemporaryUnityObject(readable);
                }
            }
        }

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

        private static TextureType GetTextureType(ThingDef thingDef)
        {
            if (thingDef.building != null)
            {
                return TextureType.Building;
            }
            else if (thingDef.IsWeapon)
            {
                return TextureType.Weapon;
            }
            else if (thingDef.IsApparel)
            {
                return TextureType.Apparel;
            }
            else if (thingDef.IsPlant)
            {
                if (thingDef.plant.IsTree)
                {
                    return TextureType.Tree;
                }
                return TextureType.Plant;
            }
            else if (thingDef.projectile != null)
            {
                return TextureType.Projectile;
            }
            else if (thingDef.category == ThingCategory.Mote)
            {
                return TextureType.Mote;
            }
            else if (thingDef.category == ThingCategory.Filth)
            {
                return TextureType.Filth;
            }
            else if (thingDef.category == ThingCategory.Item)
            {
                return TextureType.Item;
            }
            else if (thingDef.race != null)
            {
                return TextureType.Pawn;
            }
            return TextureType.None;
        }

        private static void FillEntry(TextureType type, BuildableDef def, Graphic graphicOverride = null)
        {
            var graphic = graphicOverride ?? def.graphic;
            AddEntry(type, def, graphic);
            if (def.uiIconPath.NullOrEmpty() is false)
            {
                if (def.uiIcon != null)
                {
                    if (TryGetTexturePath(def.uiIcon, out var fullPath))
                    {
                        AddEntry(TextureType.UI, def, fullPath, def.uiIcon);
                    }
                }
            }
        }
        private static void AddEntry(TextureType type, BuildableDef def, Graphic graphic)
        {
            if (graphic is Graphic_Multi multi)
            {
                foreach (var mat in multi.mats)
                {
                    GetMatTexture(type, def, mat);
                }
            }
            else if (graphic is Graphic_Appearances appearances)
            {
                foreach (var subGraphic in appearances.subGraphics)
                {
                    AddEntry(type, def, subGraphic);
                }
            }
            else if (graphic is Graphic_Single single)
            {
                GetMatTexture(type, def, single.mat);
            }
            else if (graphic is Graphic_RandomRotated randomRotated)
            {
                AddEntry(type, def, randomRotated.subGraphic);
            }
            else if (graphic is Graphic_Linked linked)
            {
                AddEntry(type, def, linked.subGraphic);
            }
            else if (def.graphic is Graphic_Collection collection)
            {
                foreach (var subGraphic in collection.subGraphics)
                {
                    AddEntry(type, def, subGraphic);
                }
            }
        }
        private static void GetMatTexture(TextureType type, BuildableDef def, Material mat)
        {
            if (mat?.mainTexture != null && TryGetTexturePath(mat.mainTexture, out var fullPath))
            {
                AddEntry(type, def, fullPath, mat.mainTexture);
                Texture2D mask = null;
                if (mat.HasProperty(ShaderPropertyIDs.MaskTex))
                {
                    mask = (Texture2D)mat.GetTexture(ShaderPropertyIDs.MaskTex);
                }
                if (mask != null && TryGetTexturePath(mask, out var maskPath))
                {
                    AddEntry(type, def, maskPath, mask);
                }
            }
        }

        private static bool TryGetResizeTarget(Texture texture, BuildableDef def, out int targetSize)
        {
            if (def is TerrainDef)
            {
                targetSize = targetSizes[TextureType.Terrain];
                return true;
            }

            if (def is ThingDef thingDef && thingDef.graphicData != null
                && thingDef.graphicData.drawSize.x + thingDef.graphicData.drawSize.y <= 8)
            {
                return targetSizes.TryGetValue(GetTextureType(thingDef), out targetSize);
            }

            targetSize = 0;
            return false;
        }

        private static void RefreshTexturePathMap()
        {
            foreach (var kvp in ModContentLoaderTexture2D_LoadTexture_Patch.savedTextures)
            {
                if (kvp.Value.TryGetTarget(out var tex))
                {
                    texturesByPaths[tex] = kvp.Key;
                }
            }
        }

        private static bool TryGetTexturePath(Texture texture, out string fullPath)
        {
            if (texture != null && texturesByPaths.TryGetValue(texture, out fullPath))
            {
                return true;
            }

            if (texture != null)
            {
                foreach (var kvp in ModContentLoaderTexture2D_LoadTexture_Patch.savedTextures)
                {
                    if (kvp.Value.TryGetTarget(out var savedTexture) && ReferenceEquals(savedTexture, texture))
                    {
                        fullPath = kvp.Key;
                        texturesByPaths[texture] = fullPath;
                        return true;
                    }
                }
            }

            fullPath = null;
            return false;
        }

        private static void AddEntry(TextureType type, BuildableDef def, string fullPath, Texture texture)
        {
            var entry = new KeyValuePair<BuildableDef, string>(def, fullPath);
            textures[type].Add(entry);
            texturesByDefs[texture] = entry;
        }
    }
}
