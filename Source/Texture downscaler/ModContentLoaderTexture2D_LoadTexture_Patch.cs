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
    /// <summary>
    /// 攔截 ModContentLoader&lt;Texture2D&gt;.LoadTexture，提供紋理快取與降質紋理替換。
    /// 使用 WeakReference 追蹤已載入的紋理，避免強參考導致記憶體洩漏。
    /// </summary>
    [HarmonyPatch(typeof(ModContentLoader<Texture2D>), "LoadTexture")]
    public static class ModContentLoaderTexture2D_LoadTexture_Patch
    {
        /// <summary>本次 session 中所有已載入的紋理路徑映射。</summary>
        public static ConcurrentDictionary<string, string> loadedTexturesThisSession = new ConcurrentDictionary<string, string>();
        /// <summary>以 WeakReference 快取已載入的 Texture2D，鍵為完整檔案路徑。</summary>
        public static ConcurrentDictionary<string, System.WeakReference<Texture2D>> savedTextures = new ConcurrentDictionary<string, System.WeakReference<Texture2D>>();
        /// <summary>紋理快取命中次數。</summary>
        public static int cacheLoadHits;
        /// <summary>紋理快取失敗次數。</summary>
        public static int cacheLoadFailures;

        static ModContentLoaderTexture2D_LoadTexture_Patch()
        {
            CacheResetter.Register(() =>
            {
                savedTextures.Clear();
                loadedTexturesThisSession.Clear();
            });
        }

        public static bool Prefix(VirtualFile file, out bool __state, ref Texture2D __result)
        {
            var fullPath = file.FullPath;

            // 優先檢查 WeakReference 快取中是否已有此紋理
            if (savedTextures.TryGetValue(fullPath, out var weakRef) && weakRef.TryGetTarget(out __result))
            {
                __state = false;
                return false;
            }

            // 檢查是否有降質快取版本的紋理可用
            if (TextureCacheManager.TryGetCachedTexturePath(fullPath, out var cachePath))
            {
                try
                {
                    var data = File.ReadAllBytes(cachePath);
                    bool useMipmaps = !fullPath.Contains(FGLConsts.UIDirSlash) && !fullPath.Contains(FGLConsts.UIDirBackslash);
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

                    TextureCacheManager.RemoveCachedTexturePath(fullPath);
                    Interlocked.Increment(ref cacheLoadFailures);
                }
                catch (Exception)
                {
                    TextureCacheManager.RemoveCachedTexturePath(fullPath);
                    Interlocked.Increment(ref cacheLoadFailures);
                }
            }

            // 沒有快取命中，讓原始方法載入紋理
            __state = true;
            var searchPath = fullPath.Replace('\\', '/');
            var index = searchPath.IndexOf(FGLConsts.TexturesDirSlash);
            if (index >= 0)
            {
                var path = fullPath.Substring(index);
                loadedTexturesThisSession[path] = fullPath;
            }
            return true;
        }

        /// <summary>
        /// 載入成功後將紋理加入 WeakReference 快取，供後續查詢使用。
        /// </summary>
        public static void Postfix(VirtualFile file, bool __state, Texture2D __result)
        {
            if (__state && __result != null)
            {
                savedTextures[file.FullPath] = new System.WeakReference<Texture2D>(__result);
            }
        }
    }
}
