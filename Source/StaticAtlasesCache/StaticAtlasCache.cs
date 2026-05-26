using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 靜態圖集快取系統：將烘焙完成的圖集以原始 DXT 位元組儲存到磁碟，
    /// 下次啟動時直接讀取還原，跳過耗時的烘焙過程。
    /// 使用 MD5 hash 驗證 mod 組合與 buildQueue 的一致性。
    /// </summary>
    public static class StaticAtlasCache
    {
        /// <summary>快取儲存目錄。</summary>
        public static string CacheDirectory => Path.Combine(GenFilePaths.SaveDataFolderPath, "FasterGameLoading", "AtlasCache");
        private static string ManifestPath => Path.Combine(CacheDirectory, "manifest.json");

        /// <summary>快取清單版本號。</summary>
        [Serializable]
        public class Manifest
        {
            public int version = 2;
            /// <summary>目前載入的 mod 組合 hash。</summary>
            public string modsHash;
            /// <summary>buildQueue 內容 hash。</summary>
            public string queueHash;
            public List<AtlasInfo> atlases = new List<AtlasInfo>();
        }

        /// <summary>單一圖集的描述資訊（用於 JSON 序列化）。</summary>
        [Serializable]
        public class AtlasInfo
        {
            public int group;
            public bool hasMask;
            public int width;
            public int height;
            public int format;
            public int maskFormat;
            public string colorFile;
            public string maskFile;
            public List<string> textureNames = new List<string>();
            public List<Rect> uvRects = new List<Rect>();
        }

        /// <summary>清除所有圖集快取（目錄 + 檔案）。</summary>
        public static void ClearCache()
        {
            try
            {
                if (Directory.Exists(CacheDirectory))
                {
                    Directory.Delete(CacheDirectory, true);
                    FGLLog.Message("Atlas cache cleared.");
                }
            }
            catch (IOException ex)
            {
                FGLLog.Warning("Failed to clear atlas cache: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                FGLLog.Warning("Failed to clear atlas cache: " + ex.Message);
            }
        }

        /// <summary>計算目前載入 mod 組合的 MD5 hash，用於驗證快取是否過期。</summary>
        private static string ComputeModsHash()
        {
            var activeMods = ModsConfig.ActiveModsInLoadOrder.Select(x => x.packageIdLowerCase).ToList();
            var str = string.Join(",", activeMods);
            using var md5 = MD5.Create();
            return string.Concat(md5.ComputeHash(Encoding.UTF8.GetBytes(str)).Select(b => b.ToString("x2")));
        }

        /// <summary>計算 buildQueue 的 MD5 hash，用於驗證快取是否過期。</summary>
        public static string ComputeQueueHash()
        {
            var sb = new StringBuilder();
            var orderedKeys = GlobalTextureAtlasManager.buildQueue.Keys.OrderBy(k => (int)k.group).ThenBy(k => k.hasMask).ToList();
            foreach (var key in orderedKeys)
            {
                sb.Append((int)key.group).Append('|').Append(key.hasMask).Append('|');
                var texList = GlobalTextureAtlasManager.buildQueue[key].Item1.OrderBy(t => t.name).ToList();
                foreach (var tex in texList)
                {
                    sb.Append(tex.name).Append('|').Append(tex.width).Append('|').Append(tex.height).Append('|');
                }
            }
            using var md5 = MD5.Create();
            return string.Concat(md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())).Select(b => b.ToString("x2")));
        }

        /// <summary>
        /// 嘗試從磁碟快取還原靜態圖集。
        /// 驗證 manifest 版本、mod 組合 hash、buildQueue hash 一致後，
        /// 直接以原始 DXT 位元組重建紋理並插入對應的 buildQueue。
        /// 任一驗證失敗或載入出錯時回傳 false。
        /// </summary>
        public static bool TryLoadFromCache()
        {
            if (!FasterGameLoadingSettings.AtlasCaching || !File.Exists(ManifestPath))
                return false;

            try
            {
                var manifestStr = File.ReadAllText(ManifestPath);
                var manifest = JsonUtility.FromJson<Manifest>(manifestStr);

                if (manifest.version != 2) return false;
                if (manifest.modsHash != ComputeModsHash()) return false;
                if (manifest.queueHash != ComputeQueueHash()) return false;

                var cachedAtlases = new List<StaticTextureAtlas>();

                foreach (var info in manifest.atlases)
                {
                    var key = new TextureAtlasGroupKey { group = (TextureAtlasGroup)info.group, hasMask = info.hasMask };
                    var atlas = new StaticTextureAtlas(key);

                    if (!GlobalTextureAtlasManager.buildQueue.ContainsKey(key))
                    {
                        DestroyAtlases(cachedAtlases);
                        return false;
                    }

                    var queueTextures = GlobalTextureAtlasManager.buildQueue[key].Item1;
                    var queueMasks = GlobalTextureAtlasManager.buildQueueMasks;
                    var texDict = queueTextures.GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());

                    foreach (var texName in info.textureNames)
                    {
                        if (texDict.TryGetValue(texName, out var tex))
                        {
                            Texture2D mask = key.hasMask && queueMasks.TryGetValue(tex, out var m) ? m : null;
                            atlas.Insert(tex, mask);
                        }
                        else
                        {
                            FGLLog.Warning("Texture missing from queue during cache load: " + texName);
                            DestroyAtlases(cachedAtlases);
                            return false;
                        }
                    }

                    var colorPath = Path.Combine(CacheDirectory, info.colorFile);
                    if (!File.Exists(colorPath))
                    {
                        DestroyAtlases(cachedAtlases);
                        return false;
                    }
                    var colorBytes = File.ReadAllBytes(colorPath);
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
                        DestroyAtlases(cachedAtlases);
                        throw;
                    }

                    if (info.hasMask && !string.IsNullOrEmpty(info.maskFile))
                    {
                        var maskPath = Path.Combine(CacheDirectory, info.maskFile);
                        if (!File.Exists(maskPath))
                        {
                            DestroyAtlasTextures(atlas);
                            DestroyAtlases(cachedAtlases);
                            return false;
                        }
                        var maskFormat = info.maskFormat != 0 ? info.maskFormat : info.format;
                        var maskTex = new Texture2D(info.width, info.height, (TextureFormat)maskFormat, false);
                        try
                        {
                            var maskBytes = File.ReadAllBytes(maskPath);
                            maskTex.LoadRawTextureData(maskBytes);
                            maskTex.Apply(true, true);
                            maskTex.name = "StaticAtlasMask_" + info.group;
                            atlas.maskTexture = maskTex;
                        }
                        catch (Exception)
                        {
                            UnityEngine.Object.Destroy(maskTex);
                            DestroyAtlasTextures(atlas);
                            DestroyAtlases(cachedAtlases);
                            throw;
                        }
                    }

                    try
                    {
                        atlas.BuildMeshesForUvs(info.uvRects.ToArray());
                        cachedAtlases.Add(atlas);
                    }
                    catch (Exception)
                    {
                        DestroyAtlasTextures(atlas);
                        DestroyAtlases(cachedAtlases);
                        throw;
                    }
                }

                foreach (var atlas in cachedAtlases)
                {
                    GlobalTextureAtlasManager.staticTextureAtlases.Add(atlas);
                }

                GlobalTextureAtlasManager.buildQueue.Clear();
                GlobalTextureAtlasManager.buildQueueMasks.Clear();

                return true;
            }
            catch (IOException e)
            {
                FGLLog.Warning("Cache load error: " + e);
                return false;
            }
            catch (UnauthorizedAccessException e)
            {
                FGLLog.Warning("Cache load error: " + e);
                return false;
            }
            catch (Exception e)
            {
                FGLLog.Warning("Cache load error, falling back to vanilla atlas baking: " + e);
                return false;
            }
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

        /// <summary>
        /// 將靜態圖集儲存到磁碟快取。使用協程分批寫入以避免單幀卡頓。
        /// 優先使用 GetRawTextureData，失敗時 fallback 到 RenderTexture 方案。
        /// </summary>
        public static IEnumerator SaveToCacheCoroutine(List<StaticTextureAtlas> atlases, string queueHash)
        {
            if (!FasterGameLoadingSettings.AtlasCaching) yield break;

            bool success = true;
            try
            {
                Directory.CreateDirectory(CacheDirectory);
            }
            catch (Exception e)
            {
                FGLLog.Error("Error creating atlas cache directory: " + e);
                yield break;
            }

            var manifest = new Manifest
            {
                modsHash = ComputeModsHash(),
                queueHash = queueHash,
                atlases = new List<AtlasInfo>()
            };

            for (int i = 0; i < atlases.Count; i++)
            {
                var atlas = atlases[i];

                var tilesDict = atlas.tiles;
                var uvRects = new List<Rect>();
                var groupKey = atlas.groupKey;
                var atlasTextures = atlas.textures;

                var texNames = atlasTextures.Select(t => t.name).ToList();

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

                var info = new AtlasInfo
                {
                    group = (int)groupKey.group,
                    hasMask = groupKey.hasMask,
                    width = atlas.colorTexture.width,
                    height = atlas.colorTexture.height,
                    format = (int)atlas.colorTexture.format,
                    textureNames = texNames,
                    uvRects = uvRects,
                    colorFile = $"atlas_{i}_color.raw",
                    maskFile = ""
                };

                byte[] colorBytes = null;
                try
                {
                    colorBytes = GetRawBytesSafe(atlas.colorTexture, out var newFormat);
                    info.format = (int)newFormat;
                }
                catch (Exception e)
                {
                    FGLLog.Error("Error getting raw bytes for color atlas: " + e);
                    success = false;
                    break;
                }

                try
                {
                    File.WriteAllBytes(Path.Combine(CacheDirectory, info.colorFile), colorBytes);
                }
                catch (Exception e)
                {
                    FGLLog.Error("Error saving atlas color bytes: " + e);
                    success = false;
                    break;
                }
                yield return null;

                if (groupKey.hasMask && atlas.maskTexture != null)
                {
                    info.maskFile = $"atlas_{i}_mask.raw";
                    byte[] maskBytes = null;
                    try
                    {
                        maskBytes = GetRawBytesSafe(atlas.maskTexture, out var newFormatMask);
                        info.maskFormat = (int)newFormatMask;
                    }
                    catch (Exception e)
                    {
                        FGLLog.Error("Error getting raw bytes for mask atlas: " + e);
                        success = false;
                        break;
                    }

                    try
                    {
                        File.WriteAllBytes(Path.Combine(CacheDirectory, info.maskFile), maskBytes);
                    }
                    catch (Exception e)
                    {
                        FGLLog.Error("Error saving atlas mask bytes: " + e);
                        success = false;
                        break;
                    }
                    yield return null;
                }

                manifest.atlases.Add(info);
            }

            if (success)
            {
                try
                {
                    File.WriteAllText(ManifestPath, JsonUtility.ToJson(manifest, true));
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
                ClearCache();
            }

            // 不管快取儲存成功與否，快取儲存結束後必須釋放 CPU 端的紋理資料以節省記憶體
            foreach (var atlas in atlases)
            {
                if (atlas.colorTexture != null)
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
                if (atlas.maskTexture != null)
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
        /// </summary>
        private static byte[] GetRawBytesSafe(Texture2D tex, out TextureFormat format)
        {
            format = tex.format;
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

            // 無法直接取得 RawTextureData，改用 RenderTexture 進行 GPU→CPU 複製
            RenderTexture rt = null;
            Texture2D readable = null;
            var previous = RenderTexture.active;
            try
            {
                int targetWidth = AlignToBlockSize(tex.width);
                int targetHeight = AlignToBlockSize(tex.height);

                rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.Default, RenderTextureReadWrite.Default);
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;

                TextureFormat fallbackFormat = TextureFormat.RGBA32;
                readable = new Texture2D(targetWidth, targetHeight, fallbackFormat, false);
                readable.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                readable.Apply(false, false);

                format = fallbackFormat;
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
