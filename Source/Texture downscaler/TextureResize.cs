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
    /// <summary>
    /// 紋理降質管理器：將高解析度紋理縮小以節省 VRAM 並加速載入。
    /// 原始 Mod 檔案不會被修改 — 降質副本儲存在獨立的快取目錄中。
    /// </summary>
    public static class TextureResize
    {
        // ════════════════════════════════════════════════════════════════
        //  快取管理
        // ════════════════════════════════════════════════════════════════

        /// <summary>原始路徑 → 降質快取路徑的對照表（會透過 Scribe 持久化）。</summary>
        public static Dictionary<string, string> resizedTextureCache = new Dictionary<string, string>();
        private static readonly object cacheLock = new object();
        private static readonly ConcurrentDictionary<string, string> md5HashCache = new ConcurrentDictionary<string, string>();

        /// <summary>紋理快取的根目錄。</summary>
        public static string CacheDirectory => Path.Combine(GenFilePaths.SaveDataFolderPath, "FasterGameLoading", "TextureCache");
        private static string BuildCacheDirectory(string suffix) => Path.Combine(GenFilePaths.SaveDataFolderPath, "FasterGameLoading", suffix);
        private static string activeCacheDirectory = CacheDirectory;

        /// <summary>
        /// 根據原始檔案路徑產生 MD5 快取檔案路徑。
        /// 快取鍵結合路徑、檔案大小和最後修改時間，確保原始檔案變更時自動失效。
        /// </summary>
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
            catch (Exception)
            {
                // 無法讀取檔案資訊（路徑過長、權限不足等），改用純路徑作為快取鍵
            }
            return originalPath;
        }

        /// <summary>目前快取的紋理數量（執行緒安全）。</summary>
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

        /// <summary>
        /// 嘗試取得指定原始路徑對應的快取紋理路徑。
        /// 自動檢查快取是否過期（原始檔案比快取檔案新時視為失效）。
        /// </summary>
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

        /// <summary>
        /// 檢查快取是否比原始檔案更新。若無法讀取檔案時間則視為失效。
        /// </summary>
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
                return false;
            }
        }

        /// <summary>移除指定原始路徑的快取項目。</summary>
        public static void RemoveCachedTexturePath(string originalPath)
        {
            lock (cacheLock)
            {
                resizedTextureCache.Remove(originalPath);
            }
        }

        /// <summary>清除所有紋理快取（檔案 + 記憶體對照表）。</summary>
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

        // ════════════════════════════════════════════════════════════════
        //  紋理類型與目標尺寸
        // ════════════════════════════════════════════════════════════════

        /// <summary>紋理分類，用於決定降質目標尺寸。</summary>
        public enum TextureType
        {
            None, Building, Pawn, Weapon, Apparel, Item, Plant, Tree, Terrain, Mote, Filth, Projectile, UI, Other
        }

        /// <summary>各紋理類型的降質目標尺寸（較長邊會縮放至此尺寸）。</summary>
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

        /// <summary>按紋理類型分類的紋理條目。</summary>
        public static Dictionary<TextureType, List<KeyValuePair<BuildableDef, string>>> textures = new();
        /// <summary>紋理 → 檔案路徑的對照表。</summary>
        public static Dictionary<Texture, string> texturesByPaths = new();
        /// <summary>紋理 → (Def, 路徑) 的對照表。</summary>
        public static Dictionary<Texture, KeyValuePair<BuildableDef, string>> texturesByDefs = new();
        /// <summary>紋理 → 來源 Mod 的對照表。</summary>
        public static Dictionary<Texture, ModContentPack> texturesByMods = new();
        private static long lastOriginalPixelCount;
        private static long lastDownscaledPixelCount;

        /// <summary>單一紋理的縮放候選資訊。</summary>
        private struct TextureResizeCandidate
        {
            public Texture source;
            public string path;
            public int targetSize;
            public int originalWidth;
            public int originalHeight;
        }

        // ════════════════════════════════════════════════════════════════
        //  主要流程
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// 執行完整紋理降質流程：
        /// 掃描所有已載入的紋理 → 計算縮放候選 → 批次降質 → 替換快取目錄。
        /// 失敗時自動還原先前的快取狀態。
        /// </summary>
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
                    Log.Warning("[FasterGameLoading] Downscaled " + texturesToResize.Count + " textures (cached, originals untouched)");
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

        // ════════════════════════════════════════════════════════════════
        //  紋理掃描
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// 掃描所有已載入的紋理，按類型分類並建立對照表。
        /// 只處理非官方 Mod 的紋理（IsOfficialMod = false）。
        /// </summary>
        private static void BuildTextureScanData()
        {
            InitializeScanContainers();
            RefreshTexturePathMap();
            BuildModTextureMap();
            ScanPawnTextures();
            ScanStyleTextures();
            ScanBuildableTextures();
        }

        private static void InitializeScanContainers()
        {
            foreach (var value in Enum.GetValues(typeof(TextureType)).Cast<TextureType>())
            {
                textures[value] = new();
            }
        }

        private static void BuildModTextureMap()
        {
            foreach (var mod in LoadedModManager.RunningMods)
            {
                foreach (var texture in mod.textures.contentList.Values)
                {
                    texturesByMods[texture] = mod;
                }
            }
        }

        /// <summary>掃描所有 PawnKindDef 的種族紋理與生命階段圖形。</summary>
        private static void ScanPawnTextures()
        {
            foreach (var pawnKind in DefDatabase<PawnKindDef>.AllDefs)
            {
                var modContent = pawnKind.modContentPack;
                if (modContent != null && modContent.IsOfficialMod) continue;
                if (pawnKind.lifeStages == null) continue;

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

        /// <summary>掃描所有 StyleCategoryDef 的外觀圖形。</summary>
        private static void ScanStyleTextures()
        {
            foreach (var styleDef in DefDatabase<StyleCategoryDef>.AllDefs)
            {
                var modContent = styleDef.modContentPack;
                if (modContent != null && modContent.IsOfficialMod) continue;

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
        }

        /// <summary>掃描所有 BuildableDef 的建物/物品/植物紋理。</summary>
        private static void ScanBuildableTextures()
        {
            foreach (var def in DefDatabase<BuildableDef>.AllDefs)
            {
                var modContent = def.modContentPack;
                if (modContent != null && modContent.IsOfficialMod) continue;

                if (def is TerrainDef terrain)
                {
                    FillEntry(TextureType.Terrain, def);
                }
                else if (def is ThingDef thingDef)
                {
                    var type = GetTextureType(thingDef);
                    FillEntry(type, thingDef);
                    ScanApparelVariants(type, def, thingDef);
                    ScanPlantVariants(type, def, thingDef);
                }
            }
        }

        /// <summary>掃描服裝的多種穿著外觀變體（含 wornGraphicPaths）。</summary>
        private static void ScanApparelVariants(TextureType type, BuildableDef def, ThingDef thingDef)
        {
            if (type != TextureType.Apparel) return;

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

        /// <summary>掃描植物的特殊圖形變體（落葉、未成熟、受汙染）。</summary>
        private static void ScanPlantVariants(TextureType type, BuildableDef def, ThingDef thingDef)
        {
            if (type != TextureType.Plant && type != TextureType.Tree) return;

            if (thingDef.plant.leaflessGraphic != null)
                AddEntry(type, def, thingDef.plant.leaflessGraphic);
            if (thingDef.plant.immatureGraphic != null)
                AddEntry(type, def, thingDef.plant.immatureGraphic);
            if (thingDef.plant.pollutedGraphic != null)
                AddEntry(type, def, thingDef.plant.pollutedGraphic);
        }

        // ════════════════════════════════════════════════════════════════
        //  縮放候選與執行
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// 篩選需要縮放的紋理：尺寸超過目標尺寸且 drawSize 總和 ≤ 8 的紋理。
        /// </summary>
        private static List<TextureResizeCandidate> BuildResizeCandidates()
        {
            var texturesToResize = new List<TextureResizeCandidate>();
            foreach (var texture in texturesByPaths)
            {
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
            lock (cacheLock) { resizedTextureCache = previousCacheMap; }
            activeCacheDirectory = previousCacheDirectory;
            md5HashCache.Clear();
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, true);
            }
            Log.Warning("[FasterGameLoading] No full-size textures found to downscale. Existing texture cache was left unchanged.");
            LogResizeSummary(0);
        }

        /// <summary>將暫存目錄替換為正式快取目錄。</summary>
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
            lock (cacheLock) { resizedTextureCache = updatedCacheMap; }
            activeCacheDirectory = CacheDirectory;
            md5HashCache.Clear();
        }

        /// <summary>清理掃描階段的暫存資料。</summary>
        private static void ClearTextureScanData()
        {
            texturesByPaths.Clear();
            texturesByDefs.Clear();
            texturesByMods.Clear();
            foreach (var value in textures.Values) { value.Clear(); }
        }

        /// <summary>
        /// 執行單一紋理降質：載入原始 PNG → 按比例縮放 → 輸出 PNG 到快取目錄。
        /// </summary>
        private static void ResizeTexture(TextureResizeCandidate candidate)
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
                var cachePath = GetCachePath(candidate.path);
                File.WriteAllBytes(cachePath, ResizeTextureToPng(resizeSource, newWidth, newHeight));
                lock (cacheLock) { resizedTextureCache[candidate.path] = cachePath; }
            }
            catch (Exception ex)
            {
                Log.Warning("[FasterGameLoading] Failed to resize texture: " + candidate.path + " - " + ex.Message);
            }
            finally
            {
                if (originalTexture != null) DestroyTemporaryUnityObject(originalTexture);
            }
        }

        /// <summary>嘗試從磁碟載入原始 PNG 紋理。失敗時回傳 false，由呼叫端使用記憶體中的版本。</summary>
        private static bool TryLoadOriginalTexture(string path, out Texture2D texture)
        {
            texture = null;
            try
            {
                if (!File.Exists(path)) return false;
                var data = File.ReadAllBytes(path);
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (texture.LoadImage(data) && texture.width > 0 && texture.height > 0)
                {
                    texture.name = Path.GetFileNameWithoutExtension(path);
                    return true;
                }
            }
            catch (Exception) { /* fallback 到記憶體中的版本 */ }

            if (texture != null) { DestroyTemporaryUnityObject(texture); texture = null; }
            return false;
        }

        // ════════════════════════════════════════════════════════════════
        //  PNG 解析工具
        // ════════════════════════════════════════════════════════════════

        /// <summary>嘗試從 PNG 檔案標頭讀取尺寸（不載入完整圖片）。</summary>
        private static bool TryGetImageDimensions(string path, ref int width, ref int height)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using (var stream = File.OpenRead(path))
                {
                    return TryReadPngDimensions(stream, ref width, ref height);
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>從 PNG 檔案串流讀取 IHDR chunk 中的寬高。</summary>
        private static bool TryReadPngDimensions(Stream stream, ref int width, ref int height)
        {
            stream.Position = 0;
            var header = new byte[24];
            if (stream.Read(header, 0, header.Length) != header.Length) return false;
            if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47) return false;

            width = ReadBigEndianInt32(header, 16);
            height = ReadBigEndianInt32(header, 20);
            return width > 0 && height > 0;
        }

        private static int ReadBigEndianInt32(byte[] bytes, int offset)
        {
            return (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        // ════════════════════════════════════════════════════════════════
        //  紋理操作工具
        // ════════════════════════════════════════════════════════════════

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

        /// <summary>
        /// 安全銷毀暫存 Unity 物件。先嘗試 DestroyImmediate，失敗時改用 Destroy。
        /// </summary>
        private static void DestroyTemporaryUnityObject(UnityEngine.Object obj)
        {
            if (obj == null) return;
            try { UnityEngine.Object.DestroyImmediate(obj); }
            catch (Exception) { UnityEngine.Object.Destroy(obj); }
        }

        /// <summary>
        /// 使用 RenderTexture 將來源紋理縮放到目標尺寸，輸出為 PNG 位元組陣列。
        /// </summary>
        /// <summary>透過 RenderTexture 將來源紋理縮放至指定尺寸並輸出 PNG。</summary>
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
                if (readable != null) DestroyTemporaryUnityObject(readable);
            }
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
        private static TextureType GetTextureType(ThingDef thingDef)
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
        //  紋理條目管理
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// 將 Def 的圖形和 UI 圖示加入紋理條目。
        /// </summary>
        private static void FillEntry(TextureType type, BuildableDef def, Graphic graphicOverride = null)
        {
            var graphic = graphicOverride ?? def.graphic;
            AddEntry(type, def, graphic);
            if (def.uiIconPath.NullOrEmpty() is false && def.uiIcon != null)
            {
                if (TryGetTexturePath(def.uiIcon, out var fullPath))
                {
                    AddEntry(TextureType.UI, def, fullPath, def.uiIcon);
                }
            }
        }

        /// <summary>
        /// 遞迴展開 Graphic 物件樹，將所有材質紋理加入條目。
        /// 支援 Graphic_Multi、Graphic_Appearances、Graphic_Single、
        /// Graphic_RandomRotated、Graphic_Linked、Graphic_Collection 等類型。
        /// </summary>
        private static void AddEntry(TextureType type, BuildableDef def, Graphic graphic)
        {
            switch (graphic)
            {
                case Graphic_Multi multi:
                    foreach (var mat in multi.mats) GetMatTexture(type, def, mat);
                    break;
                case Graphic_Appearances appearances:
                    foreach (var subGraphic in appearances.subGraphics) AddEntry(type, def, subGraphic);
                    break;
                case Graphic_Single single:
                    GetMatTexture(type, def, single.mat);
                    break;
                case Graphic_RandomRotated randomRotated:
                    AddEntry(type, def, randomRotated.subGraphic);
                    break;
                case Graphic_Linked linked:
                    AddEntry(type, def, linked.subGraphic);
                    break;
                case Graphic_Collection collection:
                    foreach (var subGraphic in collection.subGraphics) AddEntry(type, def, subGraphic);
                    break;
            }
        }

        /// <summary>
        /// 從 Material 中提取 mainTexture 和 mask texture 加入條目。
        /// </summary>
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

        /// <summary>
        /// 判斷是否應該對此紋理進行降質。
        /// 只對 drawSize 總和 ≤ 8 的 Def 進行降質（避免影響大型物件外觀）。
        /// </summary>
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

        /// <summary>從 WeakReference 快取中重新整理紋理路徑對照表。</summary>
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

        /// <summary>
        /// 根據 Texture 物件尋找其磁碟路徑。先在本地快取查詢，
        /// 找不到時遍歷 WeakReference 快取進行 ReferenceEquals 比對。
        /// </summary>
        private static bool TryGetTexturePath(Texture texture, out string fullPath)
        {
            if (texture != null && texturesByPaths.TryGetValue(texture, out fullPath))
                return true;

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

        /// <summary>將紋理條目加入指定類型的分類中。</summary>
        private static void AddEntry(TextureType type, BuildableDef def, string fullPath, Texture texture)
        {
            var entry = new KeyValuePair<BuildableDef, string>(def, fullPath);
            textures[type].Add(entry);
            texturesByDefs[texture] = entry;
        }
    }
}