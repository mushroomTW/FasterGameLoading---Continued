using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimWorld.IO;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 ModContentLoader&lt;Texture2D&gt;.LoadTexture：背景執行緒的載入轉交 <see cref="MainThreadTextureLoader"/>，
    /// 主執行緒的載入先查 <see cref="LoadedTextureRegistry"/> 與降質快取，原始載入的結果登記回 registry。
    /// </summary>
    [HarmonyPatch(typeof(ModContentLoader<Texture2D>), "LoadTexture")]
    [HarmonyBefore(TextureOwnership.GraphicsSettingsHarmonyId)]
    public static class ModContentLoaderTexture2D_LoadTexture_Patch
    {
        /// <summary>已非同步預載入至記憶體的降質快取紋理位元組數據。</summary>
        public static ConcurrentDictionary<string, byte[]> preloadedCacheBytes { get; } = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        /// <summary>主執行緒已取用過的快取檔；預讀端看到就跳過，避免同一檔案讀兩次且預讀結果無人取用。</summary>
        private static readonly ConcurrentDictionary<string, byte> _servedCachePaths = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        /// <summary>啟動完成後設為 true，讓仍在執行的預讀迴圈提早結束。</summary>
        private static volatile bool _preloadStopped;
        private static Task _preloadTask = Task.CompletedTask;
        /// <summary>紋理快取命中次數。</summary>
        private static int cacheLoadHitsValue;
        public static int cacheLoadHits
        {
            get => Volatile.Read(ref cacheLoadHitsValue);
            set => Interlocked.Exchange(ref cacheLoadHitsValue, value);
        }
        /// <summary>紋理快取失敗次數。</summary>
        private static int cacheLoadFailuresValue;
        public static int cacheLoadFailures
        {
            get => Volatile.Read(ref cacheLoadFailuresValue);
            set => Interlocked.Exchange(ref cacheLoadFailuresValue, value);
        }

        static ModContentLoaderTexture2D_LoadTexture_Patch()
        {
            SessionLifecycle.On(LifecyclePhase.LanguageReloading, static () => preloadedCacheBytes.Clear());

            SessionLifecycle.On(LifecyclePhase.StartupCompleted, static () =>
            {
                ReleasePreloadedCacheBytes();
                int configuredEntries = FasterGameLoadingMod.Instance?.CacheManager?.CacheCount ?? 0;
                if (cacheLoadHits > 0 || cacheLoadFailures > 0 || configuredEntries > 0)
                {
                    FGLLog.Message($"Texture downscale cache hits: {cacheLoadHits.ToString(CultureInfo.InvariantCulture)}, failures: {cacheLoadFailures.ToString(CultureInfo.InvariantCulture)}, configured entries: {configuredEntries.ToString(CultureInfo.InvariantCulture)}");
                }
            });
        }

        /// <summary>
        /// 背景異步預讀所有降質紋理快取到記憶體中，以防止主執行緒在載入紋理時阻塞 I/O。
        /// </summary>
        public static void StartPreloadCachedTextures()
        {
            preloadedCacheBytes.Clear();
            _servedCachePaths.Clear();
            _preloadStopped = false;
            // 外部工具接手貼圖載入時 Prefix 一律交給原始流程，預讀的位元組永遠不會被取用。
            if (!TextureOwnership.FglOwnsTextureLoading) return;
            var cacheManager = FasterGameLoadingMod.Instance?.CacheManager;
            if (cacheManager == null) return;

            // 透過執行緒安全介面取得快照，避免與 TextureCacheManager 內部的 cacheLock 競爭
            var cacheCopy = cacheManager.GetResizedTextureCacheCopy();

            if (cacheCopy.Count is 0) return;

            _preloadTask = Task.Run(() =>
            {
                try
                {
                    // 延遲 150ms 啟動，避免與啟動時最密集的 XML/Def I/O 爭奪頻寬
                    Thread.Sleep(FGLConsts.TexturePreloadDelayMs);
                    foreach (var cachePath in cacheCopy.Values)
                    {
                        if (_preloadStopped) break;
                        if (string.IsNullOrEmpty(cachePath) || _servedCachePaths.ContainsKey(cachePath)) continue;
                        try
                        {
                            if (File.Exists(cachePath))
                            {
                                var bytes = File.ReadAllBytes(cachePath);
                                // 讀檔期間主執行緒可能已自行讀取同一檔案，那份就不必保留。
                                if (!_servedCachePaths.ContainsKey(cachePath))
                                {
                                    preloadedCacheBytes[cachePath] = bytes;
                                }
                            }
                        }
                        catch
                        {
                            // 忽略個別快取讀取錯誤
                        }
                    }
                }
                catch (Exception ex)
                {
                    FGLLog.Warning("Error preloading cached textures:", ex);
                }
            });
        }

        /// <summary>
        /// 取出快取檔內容：優先使用背景預讀的位元組，否則直接讀檔。
        /// 先標記為已取用，讓尚未讀到這個檔案的預讀端跳過它。
        /// </summary>
        internal static byte[] TakeCachedTextureBytes(string cachePath)
        {
            _servedCachePaths.TryAdd(cachePath, 0);
            return preloadedCacheBytes.TryRemove(cachePath, out var data) ? data : File.ReadAllBytes(cachePath);
        }

        /// <summary>
        /// 啟動完成時呼叫：停止預讀並釋放所有未被取用的位元組（對應貼圖已經載入，或這個 session 不會載入）。
        /// 預讀迴圈可能仍在執行，所以等它結束後再清一次，確保之後寫入的也不會殘留。
        /// </summary>
        internal static void ReleasePreloadedCacheBytes()
        {
            _preloadStopped = true;
            preloadedCacheBytes.Clear();
            _preloadTask.ContinueWith(static _ =>
            {
                preloadedCacheBytes.Clear();
                _servedCachePaths.Clear();
            }, TaskScheduler.Default);
        }

        private static bool TryServeCachedTexture(string fullPath, out Texture2D result)
        {
            if (ProtectedMods.IsProtectedTexturePath(fullPath))
            {
                result = null;
                return false;
            }

            return TryServeFromWeakReferenceCache(fullPath, out result)
                || TryServeFromDownscaleCache(fullPath, out result);
        }

        public static bool Prefix(VirtualFile file, out bool __state, ref Texture2D __result)
        {
            if (!TextureOwnership.FglOwnsTextureLoading)
            {
                __state = false;
                return true;
            }

            if (!UnityData.IsInMainThread)
            {
                // Unity 的資源載入 API 只能在主執行緒呼叫；逾時、失敗一律回傳 null 並跳過原始方法。
                __state = false;
                __result = MainThreadTextureLoader.Load(file);
                return false;
            }

            var fullPath = file.FullPath;
            if (TryServeCachedTexture(fullPath, out __result))
            {
                __state = false;
                return false;
            }

            // 沒有快取命中，讓原始方法載入紋理
            __state = true;
            return true;
        }

        /// <summary>本 session 已載入過同一路徑時，直接沿用 registry 中的紋理。</summary>
        private static bool TryServeFromWeakReferenceCache(string fullPath, out Texture2D result)
        {
            if (LoadedTextureRegistry.TryGetTexture(fullPath, out result))
            {
                LoadedTextureRegistry.MarkSkipBakingIfProtected(fullPath, result);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 嘗試以磁碟上的降質快取取代原始紋理。
        /// 快取檔損毀或載入失敗時移除該快取項目並回報未命中，由原始方法接手。
        /// </summary>
        private static bool TryServeFromDownscaleCache(string fullPath, out Texture2D result)
        {
            result = null;
            if (!FasterGameLoadingMod.Instance.CacheManager.TryGetCachedTexturePath(fullPath, out var cachePath))
            {
                return false;
            }

            try
            {
                var data = TakeCachedTextureBytes(cachePath);
                // 延續既有行為：/UI/ 圖示不產生 mipmap，避免縮小顯示時變模糊。
                bool useMipmaps = fullPath.NormalizePath().IndexOf(FGLConsts.UIDirSlash, StringComparison.Ordinal) < 0;
                var tex = new Texture2D(FGLConsts.PlaceholderTextureSize, FGLConsts.PlaceholderTextureSize, TextureFormat.Alpha8, useMipmaps);
                var textureAccepted = false;

                try
                {
                    if (tex.LoadImage(data) && tex.width > 0 && tex.height > 0)
                    {
                        tex = FinishLoadingLikeVanilla(tex, data, useMipmaps);
                        tex.name = Path.GetFileNameWithoutExtension(fullPath);
                        LoadedTextureRegistry.Record(fullPath, tex);
                        LoadedTextureRegistry.MarkSkipBakingIfProtected(fullPath, tex);
                        Interlocked.Increment(ref cacheLoadHitsValue);
                        result = tex;
                        textureAccepted = true;
                        return true;
                    }
                }
                finally
                {
                    if (!textureAccepted)
                    {
                        UnityEngine.Object.Destroy(tex);
                    }
                }
            }
            catch (Exception ex)
            {
                if (FasterGameLoadingSettings.VerboseLogging)
                {
                    FGLLog.Warning($"Exception loading cached texture for: {fullPath}", ex);
                }
            }

            FasterGameLoadingMod.Instance.CacheManager.RemoveCachedTexturePath(fullPath);
            Interlocked.Increment(ref cacheLoadFailuresValue);
            return false;
        }

        /// <summary>
        /// 依原版 ModContentLoader.LoadTextureViaImageConversion 的流程完成貼圖：
        /// 尊重 Prefs.TextureCompression、使用 Trilinear 與 anisoLevel 2，
        /// 並在支援 compute shader 時改用 GPU 壓縮（FastCompressDXT），
        /// 避免降質貼圖與原版貼圖外觀不同，也避免在主執行緒上做昂貴的 CPU 高品質壓縮。
        /// 回傳值可能是新的 Texture2D（傳入的實體已被銷毀）。
        /// 拋出例外時，若已換成新貼圖，會先銷毀新貼圖；傳入的實體仍由呼叫端負責清理。
        /// </summary>
        private static Texture2D FinishLoadingLikeVanilla(Texture2D texture, byte[] data, bool useMipmaps)
        {
            var original = texture;
            try
            {
                if (useMipmaps && Prefs.TextureCompression && !UnityData.ComputeShadersSupported
                    && (texture.width < 4 || texture.height < 4 || !Mathf.IsPowerOfTwo(texture.width) || !Mathf.IsPowerOfTwo(texture.height)))
                {
                    // 非 2 的冪次尺寸：限制 mipmap 層數，確保每一層都能做 DXT 區塊壓縮。
                    int mipCount = StaticTextureAtlas.CalculateMaxMipmapsForDxtSupport(texture);
                    var reduced = new Texture2D(texture.width, texture.height, TextureFormat.Alpha8, mipCount, linear: false);
                    UnityEngine.Object.DestroyImmediate(texture);
                    texture = reduced;
                    texture.LoadImage(data);
                }

                texture.filterMode = FilterMode.Trilinear;
                texture.anisoLevel = 2;
                bool blockAligned = texture.width % 4 is 0 && texture.height % 4 is 0;
                if (Prefs.TextureCompression && blockAligned)
                {
                    if (!UnityData.ComputeShadersSupported)
                    {
                        texture.Compress(highQuality: true);
                        texture.Apply(updateMipmaps: true, makeNoLongerReadable: true);
                        return texture;
                    }

                    texture.Apply(updateMipmaps: true, makeNoLongerReadable: true);
                    return StaticTextureAtlas.FastCompressDXT(texture, deleteOriginal: true);
                }

                texture.Apply(updateMipmaps: true, makeNoLongerReadable: true);
                return texture;
            }
            catch
            {
                // 呼叫端手上只有原本的實體，這裡換上的新貼圖若不釋放就會洩漏。
                if (!ReferenceEquals(texture, original))
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
                throw;
            }
        }

        /// <summary>
        /// 原始方法載入成功後將紋理登記到 registry，供後續查詢使用。
        /// </summary>
        public static void Postfix(VirtualFile file, bool __state, Texture2D __result)
        {
            if (__result != null)
            {
                LoadedTextureRegistry.MarkSkipBakingIfProtected(file.FullPath, __result);
            }

            if (__state && __result != null)
            {
                if (ProtectedMods.IsProtectedTexturePath(file.FullPath)) return;

                LoadedTextureRegistry.Record(file.FullPath, __result);
            }
        }
    }
}
