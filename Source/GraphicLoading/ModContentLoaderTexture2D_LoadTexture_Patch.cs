using HarmonyLib;
using RimWorld.IO;
using System;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    [HarmonyPatch(typeof(ModContentLoader<Texture2D>), "LoadTexture")]
    public static class ModContentLoaderTexture2D_LoadTexture_Patch
    {
        public static ConcurrentDictionary<string, string> loadedTexturesThisSession = new ConcurrentDictionary<string, string>();
        public static ConcurrentDictionary<string, System.WeakReference<Texture2D>> savedTextures = new ConcurrentDictionary<string, System.WeakReference<Texture2D>>();
        public static bool Prefix(VirtualFile file, out bool __state, ref Texture2D __result)
        {
            var fullPath = file.FullPath;
            if (savedTextures.TryGetValue(fullPath, out var weakRef) && weakRef.TryGetTarget(out __result))
            {
                __state = false;
                return false;
            }

            // 檢查是否有磁碟快取的縮放版本
            if (TextureResize.resizedTextureCache.TryGetValue(fullPath, out var cachePath)
                && File.Exists(cachePath))
            {
                try
                {
                    var data = File.ReadAllBytes(cachePath);
                    // UI 紋理通常不需要 Mipmaps，且關閉可節省顯存與提升清晰度
                    bool useMipmaps = !fullPath.Contains("/UI/") && !fullPath.Contains("\\UI\\");
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, useMipmaps);
                    if (tex.LoadImage(data) && tex.width > 0 && tex.height > 0)
                    {
                        tex.name = Path.GetFileNameWithoutExtension(fullPath);
                        tex.Compress(true);
                        // 關鍵優化：完成壓縮後將其設為不可讀，釋放 RAM 佔用
                        tex.Apply(true, true);
                        savedTextures[fullPath] = new System.WeakReference<Texture2D>(tex);
                        __result = tex;
                        __state = false;
                        return false;
                    }
                    // 載入出來的紋理無效，移除快取記錄
                    TextureResize.resizedTextureCache.Remove(fullPath);
                }
                catch (Exception)
                {
                    // 快取檔案損壞，移除快取記錄，走正常載入
                    TextureResize.resizedTextureCache.Remove(fullPath);
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
            if (__state && __result != null)
            {
                savedTextures[file.FullPath] = new System.WeakReference<Texture2D>(__result);
            }
        }
    }
}
