using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 負責將 StaticTextureAtlas 圖集分幀寫入快取，包含 DXT 位元組的抓取及寫入。
    /// </summary>
    public static class AtlasCacheWriter
    {
        /// <summary>
        /// 將靜態圖集儲存到磁碟快取。使用協程分批寫入以避免單幀卡頓。
        /// 優先使用 GetRawTextureData，失敗時 fallback 到 RenderTexture 方案。
        /// </summary>
        public static IEnumerator SaveToCacheCoroutine(List<StaticTextureAtlas> atlases, string queueHash)
        {
            if (!FasterGameLoadingSettings.AtlasCaching) yield break;

            var success = true;
            try
            {
                Directory.CreateDirectory(StaticAtlasCache.CacheDirectory);
            }
            catch (Exception e)
            {
                FGLLog.Error("Error creating atlas cache directory: " + e);
                yield break;
            }

            var manifest = new StaticAtlasCache.Manifest
            {
                modsHash = AtlasHashCalculator.ComputeModsHash(),
                queueHash = queueHash,
                atlases = new List<StaticAtlasCache.AtlasInfo>()
            };

            for (var i = 0; i < atlases.Count; i++)
            {
                var atlas = atlases[i];
                var saveSingle = SaveSingleAtlas(atlas, i, manifest);
                while (saveSingle.MoveNext())
                {
                    if (saveSingle.Current is bool state && !state)
                    {
                        success = false;
                        break;
                    }
                    yield return null;
                }

                if (!success) break;
            }

            if (success)
            {
                try
                {
                    IORetryHelper.WriteAllTextWithRetry(StaticAtlasCache.ManifestPath, JsonUtility.ToJson(manifest, true));
                    FGLLog.Message("Static atlas cache saved.");
                }
                catch (Exception e)
                {
                    FGLLog.Error("Error saving atlas manifest: " + e);
                    success = false;
                }
            }

            if (!success)
            {
                StaticAtlasCache.ClearCache();
            }

            ReleaseAtlasTextures(atlases);
        }

        private static IEnumerator SaveSingleAtlas(StaticTextureAtlas atlas, int index, StaticAtlasCache.Manifest manifest)
        {
            var tilesDict = atlas.tiles;
            var uvRects = new List<Rect>();
            var groupKey = atlas.groupKey;
            var atlasTextures = atlas.textures;

            var texNames = atlasTextures.Select(t => t.name).ToList();
            var texKeys = atlasTextures.Select(AtlasHashCalculator.GetTextureKey).ToList();

            foreach (var tex in atlasTextures)
            {
                if (tilesDict.TryGetValue(tex, out var tile))
                {
                    uvRects.Add(tile.uvRect);
                }
                else
                {
                    uvRects.Add(new Rect(0, 0, 0, 0));
                }
            }

            var info = new StaticAtlasCache.AtlasInfo
            {
                group = (int)groupKey.group,
                hasMask = groupKey.hasMask,
                width = atlas.colorTexture.width,
                height = atlas.colorTexture.height,
                format = (int)atlas.colorTexture.format,
                textureNames = texNames,
                textureKeys = texKeys,
                uvRects = uvRects,
                colorFile = $"atlas_{index}_color.raw",
                maskFile = ""
            };

            byte[] colorBytes = null;
            bool colorLoadFailed = false;
            try
            {
                colorBytes = GetRawBytesSafe(atlas.colorTexture, out var newFormat, out var colorActualW, out var colorActualH);
                info.format = (int)newFormat;
                // fallback 路徑可能對齊尺寸，確保 manifest 與實際位元組數一致
                info.width = colorActualW;
                info.height = colorActualH;
            }
            catch (Exception e)
            {
                FGLLog.Error("Error getting raw bytes for color atlas: " + e);
                colorLoadFailed = true;
            }

            if (colorLoadFailed)
            {
                yield return false;
                yield break;
            }

            bool colorSaveFailed = false;
            try
            {
                IORetryHelper.WriteAllBytesWithRetry(Path.Combine(StaticAtlasCache.CacheDirectory, info.colorFile), colorBytes);
            }
            catch (Exception e)
            {
                FGLLog.Error("Error saving atlas color bytes: " + e);
                colorSaveFailed = true;
            }

            if (colorSaveFailed)
            {
                yield return false;
                yield break;
            }
            yield return true;

            if (groupKey.hasMask && atlas.maskTexture != null)
            {
                info.maskFile = $"atlas_{index}_mask.raw";
                byte[] maskBytes = null;
                bool maskLoadFailed = false;
                try
                {
                    maskBytes = GetRawBytesSafe(atlas.maskTexture, out var newFormatMask, out _, out _);
                    info.maskFormat = (int)newFormatMask;
                    // 遮罩紋理與彩色紋理共用相同尺寸，尺寸已於彩色步驟更新，此處不重複寫入
                }
                catch (Exception e)
                {
                    FGLLog.Error("Error getting raw bytes for mask atlas: " + e);
                    maskLoadFailed = true;
                }

                if (maskLoadFailed)
                {
                    yield return false;
                    yield break;
                }

                bool maskSaveFailed = false;
                try
                {
                    IORetryHelper.WriteAllBytesWithRetry(Path.Combine(StaticAtlasCache.CacheDirectory, info.maskFile), maskBytes);
                }
                catch (Exception e)
                {
                    FGLLog.Error("Error saving atlas mask bytes: " + e);
                    maskSaveFailed = true;
                }

                if (maskSaveFailed)
                {
                    yield return false;
                    yield break;
                }
                yield return true;
            }

            manifest.atlases.Add(info);
        }

        private static void ReleaseAtlasTextures(List<StaticTextureAtlas> atlases)
        {
            // 快取儲存結束後釋放 CPU 端的紋理資料以節省記憶體。
            // 注意：單一紋理批次路徑（AdaptiveAtlasBaker）會直接將 atlas.colorTexture 指向原始遊戲紋理，
            // 此時不可呼叫 Apply(true,true)，以免使原始紋理失去可讀性。
            // 參考 DestroyAtlasTextures 中的相同保護邏輯（atlas.textures.Contains）。
            foreach (var atlas in atlases)
            {
                if (atlas.colorTexture != null && !atlas.textures.Contains(atlas.colorTexture))
                {
                    try
                    {
                        atlas.colorTexture.Apply(true, true);
                    }
                    catch (Exception ex)
                    {
                        FGLLog.Warning("Failed to make color atlas texture non-readable: " + ex.Message);
                    }
                }
                if (atlas.maskTexture != null && !atlas.textures.Contains(atlas.maskTexture))
                {
                    try
                    {
                        atlas.maskTexture.Apply(true, true);
                    }
                    catch (Exception ex)
                    {
                        FGLLog.Warning("Failed to make mask atlas texture non-readable: " + ex.Message);
                    }
                }
            }
        }

        private static int AlignToBlockSize(int size)
        {
            // DXT/BC 壓縮紋理以 4x4 像素為單位，寬高必須是 4 的倍數
            return (size + 3) & ~3;
        }

        /// <summary>
        /// 安全地從 Texture2D 取得原始位元組。
        /// 優先使用 GetRawTextureData，若無法直接讀取則透過 RenderTexture 進行 GPU→CPU 複製。
        /// actualWidth/actualHeight 回傳實際寫入資料所對應的尺寸（fallback 路徑可能因對齊而與原始尺寸不同）。
        /// </summary>
        private static byte[] GetRawBytesSafe(Texture2D tex, out TextureFormat format, out int actualWidth, out int actualHeight)
        {
            format = tex.format;
            actualWidth = tex.width;
            actualHeight = tex.height;
            try
            {
                var bytes = tex.GetRawTextureData();
                if (bytes != null && bytes.Length > 0)
                {
                    return bytes;
                }
            }
            catch (Exception ex)
            {
                // 無法直接讀取紋理資料，改用 RenderTexture fallback
                if (FasterGameLoadingSettings.VerboseLogging)
                {
                    FGLLog.Warning("Cannot read raw texture data directly, falling back to RenderTexture for: " + tex.name, ex);
                }
            }

            // 無法直接取得 RawTextureData，改用 RenderTexture 進行 GPU→CPU 複製。
            // fallback 路徑將尺寸對齊至 4 的倍數，actualWidth/actualHeight 需反映此變更，
            // 以確保 manifest 記錄的尺寸與實際寫入的位元組數一致，避免讀取時 LoadRawTextureData 失敗。
            RenderTexture rt = null;
            Texture2D readable = null;
            var previous = RenderTexture.active;
            try
            {
                var targetWidth = AlignToBlockSize(tex.width);
                var targetHeight = AlignToBlockSize(tex.height);

                rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.Default, RenderTextureReadWrite.Default);
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;

                var fallbackFormat = TextureFormat.RGBA32;
                readable = new Texture2D(targetWidth, targetHeight, fallbackFormat, false);
                readable.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                readable.Apply(false, false);

                format = fallbackFormat;
                actualWidth = targetWidth;
                actualHeight = targetHeight;
                return readable.GetRawTextureData();
            }
            finally
            {
                RenderTexture.active = previous;
                if (rt != null)
                {
                    RenderTexture.ReleaseTemporary(rt);
                }
                if (readable != null)
                {
                    UnityEngine.Object.Destroy(readable);
                }
            }
        }
    }
}
