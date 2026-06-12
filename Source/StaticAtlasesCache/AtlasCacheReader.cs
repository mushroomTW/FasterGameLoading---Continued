using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 負責讀取 manifest.json、驗證雜湊、以及以 RawTextureData 還原並加載 Texture2D 到 RimWorld 的圖集系統中。
    /// </summary>
    public static class AtlasCacheReader
    {
        /// <summary>
        /// 嘗試從磁碟快取還原靜態圖集。
        /// </summary>
        public static bool TryLoadFromCache()
        {
            if (!FasterGameLoadingSettings.AtlasCaching || !File.Exists(StaticAtlasCache.ManifestPath))
            {
                return false;
            }

            try
            {
                var manifestStr = File.ReadAllText(StaticAtlasCache.ManifestPath);
                var manifest = JsonUtility.FromJson<StaticAtlasCache.Manifest>(manifestStr);

                if (!ValidateManifest(manifest))
                {
                    return false;
                }

                var cachedAtlases = new List<StaticTextureAtlas>();

                foreach (var info in manifest.atlases)
                {
                    if (!RebuildAtlas(info, cachedAtlases))
                    {
                        DestroyAtlases(cachedAtlases);
                        return false;
                    }
                }

                CommitAtlasesToManager(cachedAtlases);
                return true;
            }
            catch (IOException e)
            {
                FGLLog.Warning("Cache load I/O error: " + e);
                return false;
            }
            catch (UnauthorizedAccessException e)
            {
                FGLLog.Warning("Cache load authorization error: " + e);
                return false;
            }
            catch (Exception e)
            {
                FGLLog.Warning("Cache load error, falling back to vanilla atlas baking: " + e);
                return false;
            }
        }

        private static bool ValidateManifest(StaticAtlasCache.Manifest manifest)
        {
            if (manifest == null) return false;
            if (manifest.version != 3) return false;
            if (manifest.modsHash != AtlasHashCalculator.ComputeModsHash()) return false;
            if (manifest.queueHash != AtlasHashCalculator.ComputeQueueHash()) return false;
            return true;
        }

        private static bool RebuildAtlas(StaticAtlasCache.AtlasInfo info, List<StaticTextureAtlas> cachedAtlases)
        {
            var key = new TextureAtlasGroupKey { group = (TextureAtlasGroup)info.group, hasMask = info.hasMask };
            var atlas = new StaticTextureAtlas(key);

            if (!GlobalTextureAtlasManager.buildQueue.ContainsKey(key))
            {
                return false;
            }

            var queueTextures = GlobalTextureAtlasManager.buildQueue[key].Item1;
            var queueMasks = GlobalTextureAtlasManager.buildQueueMasks;
            var texDict = queueTextures
                .GroupBy(AtlasHashCalculator.GetTextureKey)
                .ToDictionary(g => g.Key, g => g.First());

            var textureKeys = info.textureKeys ?? new List<string>();
            for (var i = 0; i < textureKeys.Count; i++)
            {
                var texKey = textureKeys[i];
                if (texDict.TryGetValue(texKey, out var tex))
                {
                    var mask = key.hasMask && queueMasks.TryGetValue(tex, out var m) ? m : null;
                    atlas.Insert(tex, mask);
                }
                else
                {
                    // 5b：textureNames 可能為 null（舊版快取或序列化遺失）
                    var texName = (info.textureNames != null && i < info.textureNames.Count) ? info.textureNames[i] : texKey;
                    FGLLog.Warning("Texture missing from queue during cache load: " + texName);
                    return false;
                }
            }

            if (!LoadColorAndMaskTextures(info, atlas, key))
            {
                return false;
            }

            try
            {
                atlas.BuildMeshesForUvs(info.uvRects.ToArray());
                cachedAtlases.Add(atlas);
                return true;
            }
            catch (Exception ex)
            {
                FGLLog.Warning($"Failed to build meshes for atlas group {info.group}: {ex.Message}");
                DestroyAtlasTextures(atlas);
                return false;
            }
        }

        /// <summary>計算指定格式與尺寸的 Texture2D 原始位元組預期大小，用於載入前驗證。</summary>
        private static int GetExpectedRawSize(int width, int height, TextureFormat format)
        {
            // 常見的非壓縮格式：每像素位元組數固定
            switch (format)
            {
                case TextureFormat.RGBA32: return width * height * 4;
                case TextureFormat.RGB24:  return width * height * 3;
                case TextureFormat.Alpha8: return width * height;
                case TextureFormat.R8:     return width * height;
                case TextureFormat.RG16:   return width * height * 2;
                case TextureFormat.RGBA64: return width * height * 8;
                // DXT1/BC1：每 4×4 區塊 8 bytes
                case TextureFormat.DXT1:   return Mathf.Max(1, (width + 3) / 4) * Mathf.Max(1, (height + 3) / 4) * 8;
                // DXT5/BC3：每 4×4 區塊 16 bytes
                case TextureFormat.DXT5:   return Mathf.Max(1, (width + 3) / 4) * Mathf.Max(1, (height + 3) / 4) * 16;
                default:                   return -1; // 未知格式，跳過大小驗證
            }
        }

        private static bool LoadColorAndMaskTextures(StaticAtlasCache.AtlasInfo info, StaticTextureAtlas atlas, TextureAtlasGroupKey key)
        {
            var colorPath = Path.Combine(StaticAtlasCache.CacheDirectory, info.colorFile);
            if (!File.Exists(colorPath))
            {
                return false;
            }

            var colorBytes = File.ReadAllBytes(colorPath);

            // 5a：載入前驗證位元組數，防止截斷檔案導致 LoadRawTextureData 崩潰
            var colorExpected = GetExpectedRawSize(info.width, info.height, (TextureFormat)info.format);
            // 僅檢查「不足」:來源紋理若帶有 mip chain,位元組數會多於基底層,屬合法情況
            if (colorExpected > 0 && colorBytes.Length < colorExpected)
            {
                FGLLog.Warning($"Atlas cache file truncated for {info.colorFile}: expected at least {colorExpected} bytes, got {colorBytes.Length}. Skipping cache entry.");
                return false;
            }

            var colorTex = new Texture2D(info.width, info.height, (TextureFormat)info.format, false);
            try
            {
                colorTex.LoadRawTextureData(colorBytes);
                colorTex.Apply(true, true);
                colorTex.name = "StaticAtlas_" + info.group;
                atlas.colorTexture = colorTex;
            }
            catch (Exception)
            {
                UnityEngine.Object.Destroy(colorTex);
                return false;
            }

            if (info.hasMask && !string.IsNullOrEmpty(info.maskFile))
            {
                var maskPath = Path.Combine(StaticAtlasCache.CacheDirectory, info.maskFile);
                if (!File.Exists(maskPath))
                {
                    DestroyAtlasTextures(atlas);
                    return false;
                }
                var maskFormat = info.maskFormat != 0 ? info.maskFormat : info.format;
                var maskTex = new Texture2D(info.width, info.height, (TextureFormat)maskFormat, false);
                try
                {
                    var maskBytes = File.ReadAllBytes(maskPath);

                    // 5a：遮罩位元組數驗證
                    var maskExpected = GetExpectedRawSize(info.width, info.height, (TextureFormat)maskFormat);
                    // 僅檢查「不足」,容許 mip chain 額外位元組
                    if (maskExpected > 0 && maskBytes.Length < maskExpected)
                    {
                        FGLLog.Warning($"Atlas cache file truncated for {info.maskFile}: expected at least {maskExpected} bytes, got {maskBytes.Length}. Skipping cache entry.");
                        UnityEngine.Object.Destroy(maskTex);
                        DestroyAtlasTextures(atlas);
                        return false;
                    }

                    maskTex.LoadRawTextureData(maskBytes);
                    maskTex.Apply(true, true);
                    maskTex.name = "StaticAtlasMask_" + info.group;
                    atlas.maskTexture = maskTex;
                }
                catch (Exception)
                {
                    UnityEngine.Object.Destroy(maskTex);
                    DestroyAtlasTextures(atlas);
                    return false;
                }
            }

            return true;
        }

        private static void CommitAtlasesToManager(List<StaticTextureAtlas> cachedAtlases)
        {
            foreach (var atlas in cachedAtlases)
            {
                GlobalTextureAtlasManager.staticTextureAtlases.Add(atlas);
            }

            GlobalTextureAtlasManager.buildQueue.Clear();
            GlobalTextureAtlasManager.buildQueueMasks.Clear();
        }

        /// <summary>銷毀列表中的所有圖集並清除列表。</summary>
        private static void DestroyAtlases(List<StaticTextureAtlas> atlases)
        {
            foreach (var atlas in atlases)
            {
                DestroyAtlasTextures(atlas);
            }
            atlases.Clear();
        }

        /// <summary>銷毀單一圖集中所關聯的紋理物件。</summary>
        private static void DestroyAtlasTextures(StaticTextureAtlas atlas)
        {
            if (atlas?.colorTexture != null)
            {
                UnityEngine.Object.Destroy(atlas.colorTexture);
                atlas.colorTexture = null;
            }
            if (atlas?.maskTexture != null)
            {
                UnityEngine.Object.Destroy(atlas.maskTexture);
                atlas.maskTexture = null;
            }
        }
    }
}
