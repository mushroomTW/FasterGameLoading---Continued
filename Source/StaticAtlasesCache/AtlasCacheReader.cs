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
            if (manifest.version != 2) return false;
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
            var texDict = queueTextures.GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());

            foreach (var texName in info.textureNames)
            {
                if (texDict.TryGetValue(texName, out var tex))
                {
                    var mask = key.hasMask && queueMasks.TryGetValue(tex, out var m) ? m : null;
                    atlas.Insert(tex, mask);
                }
                else
                {
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

        private static bool LoadColorAndMaskTextures(StaticAtlasCache.AtlasInfo info, StaticTextureAtlas atlas, TextureAtlasGroupKey key)
        {
            var colorPath = Path.Combine(StaticAtlasCache.CacheDirectory, info.colorFile);
            if (!File.Exists(colorPath))
            {
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
