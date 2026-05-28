using HarmonyLib;
using RimWorld.IO;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
        /// <summary>已非同步預載入至記憶體的降質快取紋理位元組數據。</summary>
        public static readonly ConcurrentDictionary<string, byte[]> preloadedCacheBytes = new ConcurrentDictionary<string, byte[]>();
        /// <summary>以 WeakReference 快取已載入的 Texture2D，鍵為完整檔案路徑。</summary>
        public static ConcurrentDictionary<string, System.WeakReference<Texture2D>> savedTextures = new ConcurrentDictionary<string, System.WeakReference<Texture2D>>();
        /// <summary>Bionic Icons 的紋理快取，用於 O(1) 快速查詢。</summary>
        public static ConcurrentDictionary<Texture2D, bool> bionicIconTextures = new ConcurrentDictionary<Texture2D, bool>();
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
                bionicIconTextures.Clear();
                preloadedCacheBytes.Clear();
            });

            Startup.RegisterOnStartupCompleted(() =>
            {
                SessionCache.loadedTexturesSinceLastSession = new System.Collections.Generic.Dictionary<string, string>(loadedTexturesThisSession);
                if (cacheLoadHits > 0
                    || cacheLoadFailures > 0
                    || FasterGameLoadingMod.Instance.CacheManager.CacheCount > 0)
                {
                    FGLLog.Message("Texture downscale cache hits: " + cacheLoadHits
                        + ", failures: " + cacheLoadFailures
                        + ", configured entries: " + FasterGameLoadingMod.Instance.CacheManager.CacheCount);
                }
            });
        }

        /// <summary>
        /// 背景異步預讀所有降質紋理快取到記憶體中，以防止主執行緒在載入紋理時阻塞 I/O。
        /// </summary>
        public static void StartPreloadCachedTextures()
        {
            preloadedCacheBytes.Clear();
            var cacheManager = FasterGameLoadingMod.Instance?.CacheManager;
            if (cacheManager == null) return;

            System.Collections.Generic.Dictionary<string, string> cacheCopy;
            lock (cacheManager.ResizedTextureCache)
            {
                cacheCopy = new System.Collections.Generic.Dictionary<string, string>(cacheManager.ResizedTextureCache);
            }

            if (cacheCopy.Count == 0) return;

            Task.Run(() =>
            {
                try
                {
                    // 延遲 150ms 啟動，避免與啟動時最密集的 XML/Def I/O 爭奪頻寬
                    System.Threading.Thread.Sleep(150);
                    var semaphore = new SemaphoreSlim(2); // 限制 2 個並行 I/O 執行緒，防止硬碟頻寬飽和
                    Parallel.ForEach(cacheCopy.Values, cachePath =>
                    {
                        if (string.IsNullOrEmpty(cachePath)) return;
                        semaphore.Wait();
                        try
                        {
                            if (File.Exists(cachePath))
                            {
                                var bytes = File.ReadAllBytes(cachePath);
                                preloadedCacheBytes[cachePath] = bytes;
                            }
                        }
                        catch
                        {
                            // 忽略個別快取讀取錯誤
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });
                }
                catch (Exception ex)
                {
                    FGLLog.Warning("Error preloading cached textures: " + ex.Message);
                }
            });
        }

        public static void RegisterBionicIconIfApplicable(string path, Texture2D tex)
        {
            if (tex != null && path.IndexOf("bionicicons", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                bionicIconTextures[tex] = true;
            }
        }

        public static bool Prefix(VirtualFile file, out bool __state, ref Texture2D __result)
        {
            if (ImageOptCompat.IsImageOptActive)
            {
                __state = false;
                return true;
            }

            var fullPath = file.FullPath;

            // 優先檢查 WeakReference 快取中是否已有此紋理
            if (savedTextures.TryGetValue(fullPath, out var weakRef) && weakRef.TryGetTarget(out __result))
            {
                RegisterBionicIconIfApplicable(fullPath, __result);
                __state = false;
                return false;
            }

            // 檢查是否應該執行依需求延遲加載材質
            if (FasterGameLoadingSettings.LazyTextureLoading && LazyTextureLoader.ShouldLazyLoad(fullPath))
            {
                // 檢查是否有降質快取可用
                FasterGameLoadingMod.Instance.CacheManager.TryGetCachedTexturePath(fullPath, out var lazyCachePath);

                var tex = new Texture2D(FGLConsts.PlaceholderTextureSize, FGLConsts.PlaceholderTextureSize, TextureFormat.RGBA32, false);
                tex.name = Path.GetFileNameWithoutExtension(fullPath);

                LazyTextureLoader.RegisterLazyTexture(tex, lazyCachePath ?? fullPath);
                savedTextures[fullPath] = new System.WeakReference<Texture2D>(tex);
                RegisterBionicIconIfApplicable(fullPath, tex);

                __result = tex;
                __state = false;
                return false; // 跳過實際讀取硬碟圖片
            }


            // 檢查是否有降質快取版本的紋理可用
            if (FasterGameLoadingMod.Instance.CacheManager.TryGetCachedTexturePath(fullPath, out var cachePath))
            {
                try
                {
                    byte[] data;
                    if (!preloadedCacheBytes.TryRemove(cachePath, out data))
                    {
                        data = File.ReadAllBytes(cachePath);
                    }
                    bool useMipmaps = !fullPath.NormalizePath().Contains(FGLConsts.UIDirSlash);
                    var tex = new Texture2D(FGLConsts.PlaceholderTextureSize, FGLConsts.PlaceholderTextureSize, TextureFormat.RGBA32, useMipmaps);
                    var textureAccepted = false;

                    try
                    {
                        if (tex.LoadImage(data) && tex.width > 0 && tex.height > 0)
                        {
                            tex.name = Path.GetFileNameWithoutExtension(fullPath);
                            tex.Compress(true);
                            tex.Apply(true, true);
                            savedTextures[fullPath] = new System.WeakReference<Texture2D>(tex);
                            RegisterBionicIconIfApplicable(fullPath, tex);
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

                    FasterGameLoadingMod.Instance.CacheManager.RemoveCachedTexturePath(fullPath);
                    Interlocked.Increment(ref cacheLoadFailures);
                }
                catch (Exception ex)
                {
                    if (FasterGameLoadingSettings.VerboseLogging)
                    {
                        FGLLog.Warning("Exception loading cached texture for: " + fullPath, ex);
                    }
                    FasterGameLoadingMod.Instance.CacheManager.RemoveCachedTexturePath(fullPath);
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
                RegisterBionicIconIfApplicable(file.FullPath, __result);
            }
        }
    }
}
