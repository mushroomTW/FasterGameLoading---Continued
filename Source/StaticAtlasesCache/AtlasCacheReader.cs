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
            if (manifest.version != 4) return false;
            if (manifest.modsHash != AtlasHashCalculator.ComputeModsHash()) return false;
            if (manifest.queueHash != AtlasHashCalculator.ComputeQueueHash()) return false;
            return true;
        }

        /// <summary>
        /// 從快取 manifest 還原單一圖集並插入 cachedAtlases。
        ///
        /// 時序契約：此方法必須在 PerformAdaptiveStaticAtlasBake 清空 buildQueue 之前執行，
        /// 即 TryLoadFromCache() 必須在烘焙協程啟動前完成。
        /// RebuildAtlas 依賴 buildQueue 中紋理的「位置配對」還原 uvRects：
        /// textures[i] 對應 uvRects[i]，順序由 Insert 呼叫順序決定，
        /// 與 StaticTextureAtlas.Insert（只做 textures.Add）保持一致。
        /// </summary>
        private static bool RebuildAtlas(StaticAtlasCache.AtlasInfo info, List<StaticTextureAtlas> cachedAtlases)
        {
            var key = new TextureAtlasGroupKey { group = (TextureAtlasGroup)info.group, hasMask = info.hasMask };
            var atlas = new StaticTextureAtlas(key);

            if (!GlobalTextureAtlasManager.buildQueue.ContainsKey(key))
            {
                // textureKey 不在 buildQueue：可能是 BakingSkipList 或佇列尚未填入
                FGLLog.Warning($"Atlas cache: buildQueue does not contain key (group={info.group}, hasMask={info.hasMask}). Cache entry skipped.");
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
                    // textureKey 不在 buildQueue：可能是 BakingSkipList 在本次啟動攔截了該紋理，
                    // 或 mod 組合改變導致紋理缺失；應放棄此快取條目。
                    var texName = (info.textureNames != null && i < info.textureNames.Count) ? info.textureNames[i] : texKey;
                    FGLLog.Warning($"Atlas cache: texture missing from buildQueue during cache load (key={texKey}, name={texName}). Cache entry skipped.");
                    return false;
                }
            }

            // UV 配對防護：Insert 完成後驗證插入的紋理數與 uvRects 數量一致。
            // StaticTextureAtlas.Insert 只做 textures.Add，故 atlas.textures.Count == 插入次數。
            // BuildMeshesForUvs 逐索引配對 textures[i] ↔ uvRects[i]，數量不符會造成越界或錯誤配對。
            if (atlas.textures.Count != info.uvRects.Count)
            {
                FGLLog.Warning($"Atlas cache: texture count ({atlas.textures.Count}) != uvRects count ({info.uvRects.Count}) for group={info.group}. Cache entry skipped.");
                return false;
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
                // 項目7：BuildMeshesForUvs 可能已部分建立 tiles 中的 mesh，
                // 呼叫 atlas.Destroy() 確保所有 mesh 被銷毀，再清理紋理。
                // Unity Object.Destroy 對已銷毀的物件是安全的，重複呼叫不會崩潰。
                try { atlas.Destroy(); } catch { /* 忽略 Destroy 例外，下方仍清理紋理 */ }
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

            // 項目2：原版 colorTexture 帶 mip chain（mipCount > 1），還原時須保持等價以維持遠距離渲染品質。
            // mipCount <= 1 或欄位缺失（舊版 manifest，值為 0）時，以 false 建構（無 mip），向後相容。
            bool colorHasMips = info.mipCount > 1;
            var colorTex = new Texture2D(info.width, info.height, (TextureFormat)info.format, colorHasMips);
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
                // 項目3：mask fallback 路徑因 4 對齊可能與 color 尺寸不同，
                // maskWidth/maskHeight 為 0 時（舊版 manifest 或尺寸相同）回退使用 color 尺寸，向後相容。
                var maskW = info.maskWidth > 0 ? info.maskWidth : info.width;
                var maskH = info.maskHeight > 0 ? info.maskHeight : info.height;
                var maskTex = new Texture2D(maskW, maskH, (TextureFormat)maskFormat, false);
                try
                {
                    var maskBytes = File.ReadAllBytes(maskPath);

                    // 5a：遮罩位元組數驗證，使用 mask 實際尺寸
                    var maskExpected = GetExpectedRawSize(maskW, maskH, (TextureFormat)maskFormat);
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
