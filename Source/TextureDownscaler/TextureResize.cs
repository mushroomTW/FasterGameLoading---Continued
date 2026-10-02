using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using RimWorld;
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
            None, Building, Pawn, Weapon, Apparel, Item, Plant, Tree, Terrain, Mote, Filth, Projectile,
        }

        private readonly TextureCacheManager cacheManager;
        private readonly TextureScanner scanner;

        private long lastOriginalPixelCount;
        private long lastDownscaledPixelCount;

        /// <summary>單一紋理的縮放候選資訊。</summary>
        internal struct TextureResizeCandidate
        {
            public Texture source;
            public string path;
            public int targetSize;
            public int originalWidth;
            public int originalHeight;
        }

        /// <summary>同時在背景讀取的原圖張數。</summary>
        private const int OriginalReadAhead = 8;

        /// <summary>主執行緒已縮放完成、正在背景編碼與寫檔的一張紋理。</summary>
        private sealed class PendingWrite
        {
            public TextureResizeCandidate Candidate;
            public string CachePath;
            public long SourcePixels;
            public long DownscaledPixels;
            public Task Write;
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
        /// 掃描所有已載入的紋理 → 計算縮放候選 → 在快取重建交易中批次降質並升級為正式快取。
        /// 沒有候選、沒有任何紋理寫入成功、任何快取檔寫入失敗或中途失敗時，原本的快取保持不變。
        /// </summary>
        public void DoTextureResizing()
        {
            lastOriginalPixelCount = 0;
            lastDownscaledPixelCount = 0;
            try
            {
                scanner.BuildTextureScanData();

                var texturesToResize = BuildResizeCandidates();
                if (!texturesToResize.Any())
                {
                    return;
                }

                int resizedCount = 0;
                bool promoted = cacheManager.Rebuild(rebuild => resizedCount = ResizeAll(texturesToResize, rebuild));

                if (promoted)
                {
                    FGLLog.Message($"Downscaled {resizedCount.ToString(CultureInfo.InvariantCulture)} textures (cached, originals untouched)");
                    LogResizeSummary(resizedCount);
                    // 只在目錄與對照表一同升級成功後持久化。
                    LoadedModManager.GetMod<FasterGameLoadingMod>().WriteSettings();
                }
                else
                {
                    FGLLog.Warning("Texture cache was not replaced; previous cache was kept.");
                }
            }
            catch (Exception ex)
            {
                FGLLog.Error("Texture downscale failed, keeping previous cache:", ex);
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
            // 不同 Texture 物件可能對應同一個檔案（例如重複載入或語言重載後重建）；同一路徑只處理一次，
            // 否則背景寫檔會同時寫同一個快取檔與 .tmp 而互相衝突。
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var texture in scanner.texturesByPaths)
            {
                if (ProtectedMods.IsProtectedTexturePath(texture.Value) || seenPaths.Contains(texture.Value)) continue;

                var sourceWidth = texture.Key.width;
                var sourceHeight = texture.Key.height;
                PngUtils.TryGetImageDimensions(texture.Value, ref sourceWidth, ref sourceHeight);

                if (scanner.texturesByDefs.TryGetValue(texture.Key, out var value)
                    && TextureResizer.TryGetResizeTarget(texture.Key, value.Key, out var targetSize)
                    && (sourceWidth > targetSize || sourceHeight > targetSize))
                {
                    seenPaths.Add(texture.Value);
                    texturesToResize.Add(new TextureResizeCandidate
                    {
                        source = texture.Key,
                        path = texture.Value,
                        targetSize = targetSize,
                        originalWidth = sourceWidth,
                        originalHeight = sourceHeight,
                    });
                }
            }
            return texturesToResize;
        }

        /// <summary>
        /// 流水線降質：背景先讀接下來幾張原圖，主執行緒只做解碼、GPU 縮放與讀回像素，PNG 編碼與寫檔交給背景執行緒。
        /// 結果依候選順序在主執行緒登記到重建交易。任何快取檔寫入失敗時，等所有背景寫入結束後才往外拋，
        /// 讓 <see cref="TextureCacheManager.Rebuild"/> 放棄整批並保留原本的快取。
        /// </summary>
        private int ResizeAll(List<TextureResizeCandidate> candidates, TextureCacheManager.CacheRebuild rebuild)
        {
            var reads = new Task<byte[]>[candidates.Count];
            var writes = new Queue<PendingWrite>();
            int maxPendingWrites = Math.Max(1, Environment.ProcessorCount - 1) * 2;
            int resizedCount = 0;
            Exception fatal = null;

            void Finish(PendingWrite pending)
            {
                if (Complete(pending, out var writeFailure))
                {
                    rebuild.Add(pending.Candidate.path, pending.CachePath);
                    resizedCount++;
                }
                fatal ??= writeFailure;
            }

            try
            {
                for (int i = 0; i < candidates.Count && fatal == null; i++)
                {
                    ScheduleReadsAhead(reads, candidates, i);

                    var pending = Render(candidates[i], rebuild.GetCachePath(candidates[i].path), reads[i].Result);
                    reads[i] = null;
                    if (pending != null) writes.Enqueue(pending);
                    while (writes.Count > maxPendingWrites && fatal == null) Finish(writes.Dequeue());
                }
            }
            finally
            {
                // 放棄整批時也要等背景寫入結束，否則重建交易清掉暫存目錄後仍有檔案寫進去。
                while (writes.Count > 0) Finish(writes.Dequeue());
            }

            if (fatal != null) ExceptionDispatchInfo.Capture(fatal).Throw();
            return resizedCount;
        }

        private static void ScheduleReadsAhead(Task<byte[]>[] reads, List<TextureResizeCandidate> candidates, int startIndex)
        {
            for (int ahead = startIndex; ahead < Math.Min(candidates.Count, startIndex + OriginalReadAhead); ahead++)
            {
                if (reads[ahead] != null) continue;
                var path = candidates[ahead].path;
                reads[ahead] = Task.Run(() => ReadOriginalBytes(path));
            }
        }

        /// <summary>
        /// 執行單一紋理降質：載入原始 PNG → 按比例縮放 → 輸出 PNG 到 <paramref name="cachePath"/>，步驟與 <see cref="ResizeAll"/> 相同。
        /// 供遊戲內整合測試（InGameTests）作為單張紋理降質驗證進入點使用。
        /// 寫入成功才回傳 true，由呼叫端登記快取項目；個別紋理無法處理時回傳 false（略過該張），
        /// 寫入快取檔的 IO 錯誤則往外拋，讓 <see cref="TextureCacheManager.Rebuild"/> 放棄整批並保留原本的快取。
        /// </summary>
        internal bool ResizeTexture(TextureResizeCandidate candidate, string cachePath)
        {
            var pending = Render(candidate, cachePath, ReadOriginalBytes(candidate.path));
            if (pending == null) return false;
            if (Complete(pending, out var fatal)) return true;
            if (fatal != null) ExceptionDispatchInfo.Capture(fatal).Throw();
            return false;
        }

        /// <summary>
        /// 主執行緒上的步驟：解碼原圖（讀不到時改用記憶體中的版本）、換算尺寸、以 GPU 縮放並讀回像素，
        /// 再把 PNG 編碼與寫檔排到背景執行緒。這張無法處理時回傳 null。
        /// </summary>
        private static PendingWrite Render(TextureResizeCandidate candidate, string cachePath, byte[] originalBytes)
        {
            Texture2D originalTexture = null;
            try
            {
                var resizeSource = TryDecodeOriginalTexture(candidate.path, originalBytes, out originalTexture) ? originalTexture : candidate.source;
                if (resizeSource == null || resizeSource.width <= 0 || resizeSource.height <= 0) return null;

                var sourceWidth = originalTexture != null ? resizeSource.width : candidate.originalWidth;
                var sourceHeight = originalTexture != null ? resizeSource.height : candidate.originalHeight;
                double ratio = sourceHeight > sourceWidth
                    ? (double)candidate.targetSize / sourceHeight
                    : (double)candidate.targetSize / sourceWidth;
                // 明確指定 ToEven：與 Math.Round 的預設捨入模式相同，不改變既有結果。
                int newWidth = Math.Max(1, (int)Math.Round(sourceWidth * ratio, MidpointRounding.ToEven));
                int newHeight = Math.Max(1, (int)Math.Round(sourceHeight * ratio, MidpointRounding.ToEven));
                // 將寬高對齊至 4 的倍數，確保 Unity 桌面端 DXT 區塊壓縮 (DXT1/DXT5) 正常運作。
                newWidth = AlignToBlockSize(newWidth, sourceWidth);
                newHeight = AlignToBlockSize(newHeight, sourceHeight);
                var pixels = TextureResizer.ReadResizedPixels(resizeSource, newWidth, newHeight);
                return new PendingWrite
                {
                    Candidate = candidate,
                    CachePath = cachePath,
                    SourcePixels = (long)sourceWidth * sourceHeight,
                    DownscaledPixels = (long)newWidth * newHeight,
                    Write = Task.Run(() => IORetryHelper.WriteAllBytesWithRetry(cachePath, TextureResizer.EncodePng(pixels))),
                };
            }
            catch (Exception ex)
            {
                FGLLog.Warning($"Failed to downscale texture {candidate.path}:", ex);
                return null;
            }
            finally
            {
                if (originalTexture != null) TextureResizer.DestroyTemporaryUnityObject(originalTexture);
            }
        }

        /// <summary>
        /// 等這張的背景編碼與寫檔結束。成功時計入摘要並回傳 true；寫入失敗（磁碟已滿、權限不足）時以
        /// <paramref name="fatal"/> 回傳例外：這批快取已不完整，呼叫端必須放棄整個重建，
        /// 否則只寫成功幾張也會取代掉原本完整的快取。其他錯誤只略過這張。
        /// </summary>
        private bool Complete(PendingWrite pending, out Exception fatal)
        {
            fatal = null;
            try
            {
                pending.Write.Wait();
            }
            catch (AggregateException ex)
            {
                var error = ex.InnerException ?? ex;
                if (error is IOException || error is UnauthorizedAccessException)
                {
                    FGLLog.Error($"Failed to downscale texture {pending.Candidate.path}:", error);
                    fatal = error;
                }
                else
                {
                    FGLLog.Warning($"Failed to downscale texture {pending.Candidate.path}:", error);
                }
                return false;
            }
            // 寫入成功才計入，摘要的像素節省量只反映實際進入快取的紋理。
            lastOriginalPixelCount += pending.SourcePixels;
            lastDownscaledPixelCount += pending.DownscaledPixels;
            return true;
        }

        /// <summary>
        /// 將單一邊長對齊到 4 的倍數（DXT 區塊大小）。
        /// 採「就近取整」而非一律向下取整：targetSize 本身是 4 的倍數，長邊必定落在
        /// targetSize 上不受影響，但短邊若一律向下取整會壓扁長寬比
        /// （例如 1024×40→128×5 會被砍成 128×4，垂直壓縮 20%）；就近取整可把誤差
        /// 控制在半個區塊內。向上取整會超過來源邊長時改為向下取整，
        /// 不足 4 像素時保留原值，兩者都是為了不讓結果大於原圖。
        /// </summary>
        private static int AlignToBlockSize(int length, int sourceLength)
        {
            if (length < 4) return length;
            int aligned = (length + 2) & ~3;
            return aligned > sourceLength ? length & ~3 : aligned;
        }

        /// <summary>從磁碟讀取原始 PNG，可在背景執行緒呼叫。讀不到時回傳空陣列，由呼叫端使用記憶體中的版本。</summary>
        private static byte[] ReadOriginalBytes(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
            }
            catch (Exception ex)
            {
                // 無法從磁碟讀取原始紋理，caller 會改用記憶體中的版本作為 fallback
                FGLLog.Warning("Cannot load original texture from disk, using in-memory copy:", ex);
                return Array.Empty<byte>();
            }
        }

        /// <summary>在主執行緒解碼 <see cref="ReadOriginalBytes"/> 讀到的原圖。失敗時回傳 false，由呼叫端使用記憶體中的版本。</summary>
        private static bool TryDecodeOriginalTexture(string path, byte[] data, out Texture2D texture)
        {
            texture = null;
            if (data == null || data.Length is 0) return false;
            try
            {
                texture = new Texture2D(FGLConsts.PlaceholderTextureSize, FGLConsts.PlaceholderTextureSize, TextureFormat.RGBA32, mipChain: false);
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
            FGLLog.Message($"Texture downscale summary: resized={resizedCount.ToString(CultureInfo.InvariantCulture)}, sourcePixels={lastOriginalPixelCount.ToString(CultureInfo.InvariantCulture)}, downscaledPixels={lastDownscaledPixelCount.ToString(CultureInfo.InvariantCulture)}, estimatedSaved={FormatBytes((lastOriginalPixelCount - lastDownscaledPixelCount) * 4)}");
        }

        private static string FormatBytes(long bytes)
        {
            // 診斷日誌為英文，數值一律以 InvariantCulture 格式化，避免部分語系輸出逗號小數點。
            return (bytes / 1024f / 1024f).ToString("F1", CultureInfo.InvariantCulture) + " MiB";
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
            if (bodyType == BodyTypeDefOf.Baby && !def.apparel.developmentalStageFilter.HasFlag(DevelopmentalStage.Baby)
                || bodyType == BodyTypeDefOf.Child && !def.apparel.developmentalStageFilter.HasFlag(DevelopmentalStage.Child))
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
                && !RenderAsPack(def) && !string.Equals(wornGraphicPath, BaseContent.PlaceholderImagePath, StringComparison.Ordinal)
                && !string.Equals(wornGraphicPath, BaseContent.PlaceholderGearImagePath, StringComparison.Ordinal)) ? (wornGraphicPath + "_" + bodyType.defName)
                : wornGraphicPath);
            Shader shader = ShaderDatabase.Cutout;
            if (def.apparel.useWornGraphicMask)
            {
                shader = ShaderDatabase.CutoutComplex;
            }
            var drawSize = def.graphicData?.drawSize ?? UnityEngine.Vector2.one;
            rec = GraphicDatabase.Get<Graphic_Multi>(path, shader, drawSize, Color.white);
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
            if (thingDef.category is ThingCategory.Mote) return TextureType.Mote;
            if (thingDef.category is ThingCategory.Filth) return TextureType.Filth;
            if (thingDef.category is ThingCategory.Item) return TextureType.Item;
            if (thingDef.race != null) return TextureType.Pawn;
            return TextureType.None;
        }
    }
}
