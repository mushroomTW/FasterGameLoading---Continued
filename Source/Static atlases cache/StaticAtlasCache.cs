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
    public static class StaticAtlasCache
    {
        public static string CacheDirectory => Path.Combine(GenFilePaths.SaveDataFolderPath, "FasterGameLoading", "AtlasCache");
        private static string ManifestPath => Path.Combine(CacheDirectory, "manifest.json");

        [Serializable]
        public class Manifest
        {
            public int version = 2;
            public string modsHash;
            public string queueHash;
            public List<AtlasInfo> atlases = new List<AtlasInfo>();
        }

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

        public static void ClearCache()
        {
            try
            {
                if (Directory.Exists(CacheDirectory))
                {
                    Directory.Delete(CacheDirectory, true);
                    Log.Message("[FasterGameLoading] Atlas cache cleared.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[FasterGameLoading] Failed to clear atlas cache: " + ex.Message);
            }
        }

        private static string ComputeModsHash()
        {
            var activeMods = ModsConfig.ActiveModsInLoadOrder.Select(x => x.packageIdLowerCase).ToList();
            var str = string.Join(",", activeMods);
            using var md5 = MD5.Create();
            return string.Concat(md5.ComputeHash(Encoding.UTF8.GetBytes(str)).Select(b => b.ToString("x2")));
        }

        public static string ComputeQueueHash()
        {
            var sb = new StringBuilder();
            var orderedKeys = GlobalTextureAtlasManager.buildQueue.Keys.OrderBy(k => (int)k.group).ThenBy(k => k.hasMask).ToList();
            foreach (var key in orderedKeys)
            {
                sb.Append((int)key.group).Append(key.hasMask);
                var texList = GlobalTextureAtlasManager.buildQueue[key].Item1.OrderBy(t => t.name).ToList();
                foreach (var tex in texList)
                {
                    sb.Append(tex.name).Append(tex.width).Append(tex.height);
                }
            }
            using var md5 = MD5.Create();
            return string.Concat(md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())).Select(b => b.ToString("x2")));
        }

        public static bool TryLoadFromCache()
        {
            if (!FasterGameLoadingSettings.atlasCaching || !File.Exists(ManifestPath))
                return false;

            try
            {
                var manifestStr = File.ReadAllText(ManifestPath);
                var manifest = JsonUtility.FromJson<Manifest>(manifestStr);

                if (manifest.version != 2) return false;
                if (manifest.modsHash != ComputeModsHash()) return false;
                if (manifest.queueHash != ComputeQueueHash()) return false;

                var cachedAtlases = new List<StaticTextureAtlas>();
                var buildMeshesMethod = AccessTools.Method(typeof(StaticTextureAtlas), "BuildMeshesForUvs", new[] { typeof(Rect[]) });

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
                            Log.Warning("[FasterGameLoading] Texture missing from queue during cache load: " + texName);
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
                    catch
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
                        catch
                        {
                            UnityEngine.Object.Destroy(maskTex);
                            DestroyAtlasTextures(atlas);
                            DestroyAtlases(cachedAtlases);
                            throw;
                        }
                    }

                    try
                    {
                        buildMeshesMethod.Invoke(atlas, new object[] { info.uvRects.ToArray() });
                        cachedAtlases.Add(atlas);
                    }
                    catch
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
            catch (Exception e)
            {
                Log.Warning("[FasterGameLoading] Cache load error: " + e);
                return false;
            }
        }

        private static void DestroyAtlases(List<StaticTextureAtlas> atlases)
        {
            foreach (var atlas in atlases)
            {
                DestroyAtlasTextures(atlas);
            }
            atlases.Clear();
        }

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

        public static IEnumerator SaveToCacheCoroutine(List<StaticTextureAtlas> atlases, string queueHash)
        {
            if (!FasterGameLoadingSettings.atlasCaching) yield break;

            Directory.CreateDirectory(CacheDirectory);

            var manifest = new Manifest
            {
                modsHash = ComputeModsHash(),
                queueHash = queueHash,
                atlases = new List<AtlasInfo>()
            };

            for (int i = 0; i < atlases.Count; i++)
            {
                var atlas = atlases[i];

                var tilesDictField = AccessTools.Field(typeof(StaticTextureAtlas), "tiles");
                var tilesDict = (Dictionary<Texture, StaticTextureAtlasTile>)tilesDictField.GetValue(atlas);

                var uvRects = new List<Rect>();
                var groupKeyField = AccessTools.Field(typeof(StaticTextureAtlas), "groupKey");
                var groupKey = (TextureAtlasGroupKey)groupKeyField.GetValue(atlas);

                var atlasTexturesField = AccessTools.Field(typeof(StaticTextureAtlas), "textures");
                var atlasTextures = (List<Texture2D>)atlasTexturesField.GetValue(atlas);

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

                try
                {
                    var colorBytes = GetRawBytesSafe(atlas.colorTexture, out var newFormat);
                    info.format = (int)newFormat;
                    File.WriteAllBytes(Path.Combine(CacheDirectory, info.colorFile), colorBytes);
                }
                catch (Exception e)
                {
                    Log.Error("[FasterGameLoading] Error saving atlas color bytes: " + e);
                    yield break;
                }
                yield return null;

                if (groupKey.hasMask && atlas.maskTexture != null)
                {
                    info.maskFile = $"atlas_{i}_mask.raw";
                    try
                    {
                        var maskBytes = GetRawBytesSafe(atlas.maskTexture, out var newFormatMask);
                        info.maskFormat = (int)newFormatMask;
                        File.WriteAllBytes(Path.Combine(CacheDirectory, info.maskFile), maskBytes);
                    }
                    catch (Exception e)
                    {
                        Log.Error("[FasterGameLoading] Error saving atlas mask bytes: " + e);
                        yield break;
                    }
                    yield return null;
                }

                manifest.atlases.Add(info);
            }

            File.WriteAllText(ManifestPath, JsonUtility.ToJson(manifest, true));
            Log.Message("[FasterGameLoading] Saved static atlas cache.");
        }

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
            catch
            {
                // Ignore and fall back to RenderTexture
            }

            // Fallback for unreadable textures
            RenderTexture rt = null;
            Texture2D readable = null;
            var previous = RenderTexture.active;
            try
            {
                rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.Default, RenderTextureReadWrite.Default);
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;

                TextureFormat fallbackFormat = TextureFormat.RGBA32;
                readable = new Texture2D(tex.width, tex.height, fallbackFormat, false);
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
