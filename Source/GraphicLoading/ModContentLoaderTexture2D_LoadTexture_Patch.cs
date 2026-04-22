using HarmonyLib;
using RimWorld.IO;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    [HarmonyPatch(typeof(ModContentLoader<Texture2D>), "LoadTexture")]
    public static class ModContentLoaderTexture2D_LoadTexture_Patch
    {
        public static ConcurrentDictionary<string, string> loadedTexturesThisSession = new ConcurrentDictionary<string, string>();
        public static ConcurrentDictionary<string, Texture2D> savedTextures = new ConcurrentDictionary<string, Texture2D>();
        public static bool Prefix(VirtualFile file, out bool __state, ref Texture2D __result)
        {
            var fullPath = file.FullPath;
            if (savedTextures.TryGetValue(fullPath, out __result))
            {
                __state = false;
                return false;
            }

            // 檢查是否有磁碟快取的縮放版本
            string cachePath = null;
            lock (TextureResize.cacheLock)
            {
                TextureResize.resizedTextureCache.TryGetValue(fullPath, out cachePath);
            }
            if (cachePath != null && File.Exists(cachePath))
            {
                try
                {
                    var data = File.ReadAllBytes(cachePath);
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                    if (tex.LoadImage(data) && tex.width > 0 && tex.height > 0)
                    {
                        tex.name = Path.GetFileNameWithoutExtension(fullPath);
                        tex.Compress(true);
                        savedTextures[fullPath] = tex;
                        __result = tex;
                        __state = false;
                        return false;
                    }
                    // 載入出來的紋理無效，移除快取記錄
                    lock (TextureResize.cacheLock)
                    {
                        TextureResize.resizedTextureCache.Remove(fullPath);
                    }
                }
                catch (Exception)
                {
                    // 快取檔案損壞，移除快取記錄，走正常載入
                    lock (TextureResize.cacheLock)
                    {
                        TextureResize.resizedTextureCache.Remove(fullPath);
                    }
                }
            }

            __state = true;
            var searchPath = fullPath.Replace('\\', '/');
            var index = searchPath.IndexOf("Textures/");
            if (index >= 0)
            {
                var path = fullPath.Substring(index);
                loadedTexturesThisSession[path] = fullPath;
            }
            return true;
        }

        public static void Postfix(VirtualFile file, bool __state, Texture2D __result)
        {
            if (__state)
            {
                savedTextures[file.FullPath] = __result;
            }
        }
    }
}
