using HarmonyLib;
using RimWorld.IO;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    [HarmonyPatch(typeof(ModContentLoader<Texture2D>), "LoadTexture")]
    public static class ModContentLoaderTexture2D_LoadTexture_Patch
    {
        public static ConcurrentDictionary<string, string> loadedTexturesThisSession = new ConcurrentDictionary<string, string>();
        public static ConcurrentDictionary<string, System.WeakReference<Texture2D>> savedTextures = new ConcurrentDictionary<string, System.WeakReference<Texture2D>>();
        public static int cacheLoadHits;
        public static int cacheLoadFailures;

        public static bool Prefix(VirtualFile file, out bool __state, ref Texture2D __result)
        {
            var fullPath = file.FullPath;
            if (savedTextures.TryGetValue(fullPath, out var weakRef) && weakRef.TryGetTarget(out __result))
            {
                __state = false;
                return false;
            }

            if (TextureResize.TryGetCachedTexturePath(fullPath, out var cachePath))
            {
                try
                {
                    var data = File.ReadAllBytes(cachePath);
                    bool useMipmaps = !fullPath.Contains("/UI/") && !fullPath.Contains("\\UI\\");
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, useMipmaps);
                    var textureAccepted = false;

                    try
                    {
                        if (tex.LoadImage(data) && tex.width > 0 && tex.height > 0)
                        {
                            tex.name = Path.GetFileNameWithoutExtension(fullPath);
                            tex.Compress(true);
                            tex.Apply(true, true);
                            savedTextures[fullPath] = new System.WeakReference<Texture2D>(tex);
                            Interlocked.Increment(ref cacheLoadHits);
                            __result = tex;
                            __state = false;
                            textureAccepted = true;
                            return false;
                        }
                    }
                    finally
                    {
                        if (!textureAccepted)
                        {
                            UnityEngine.Object.Destroy(tex);
                        }
                    }

                    TextureResize.RemoveCachedTexturePath(fullPath);
                    Interlocked.Increment(ref cacheLoadFailures);
                }
                catch (Exception)
                {
                    TextureResize.RemoveCachedTexturePath(fullPath);
                    Interlocked.Increment(ref cacheLoadFailures);
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
