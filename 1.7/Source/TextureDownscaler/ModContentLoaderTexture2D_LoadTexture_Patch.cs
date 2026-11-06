using System;
using System.Globalization;
using System.IO;
using System.Threading;
using HarmonyLib;
using RimWorld.IO;
using UnityEngine;
using Verse;

namespace FasterGameLoading;

/// <summary>
/// 攔截 ModContentLoader&lt;Texture2D&gt;.LoadTexture：背景執行緒的載入轉交 <see cref="MainThreadTextureLoader"/>，
/// 主執行緒的載入先查 <see cref="LoadedTextureRegistry"/> 與降質快取，原始載入的結果登記回 registry。
/// </summary>
[HarmonyPatch(typeof(ModContentLoader<Texture2D>), "LoadTexture")]
[HarmonyBefore(TextureOwnership.GraphicsSettingsHarmonyId)]
public static class ModContentLoaderTexture2D_LoadTexture_Patch
{
    // 原版 ModContentLoader<Texture2D>.LoadTextureViaImageConversion 為 private，直接呼叫會觸發 MethodAccessException，故經由委派呼叫。
    private static readonly Func<VirtualFile, Texture2D> LoadTextureViaImageConversion =
        AccessTools.MethodDelegate<Func<VirtualFile, Texture2D>>(
            AccessTools.Method(typeof(ModContentLoader<Texture2D>), "LoadTextureViaImageConversion"));

    /// <summary>
    /// 以原版的檔案載入流程載入降質快取檔。測試以替身取代，因為 headless 環境沒有圖形裝置。
    /// 經 Texture2D.LoadFromFile 載入的貼圖帶有來源路徑：1.7 原版圖集要靠它計算磁碟快取鍵，
    /// 引擎也靠它延遲上傳、寫入壓縮快取，並在烘焙後釋放圖集來源貼圖。
    /// </summary>
    internal static Func<string, Texture2D> LoadCacheFile { get; set; } =
        static cachePath => LoadTextureViaImageConversion(new FilesystemFile(new FileInfo(cachePath)));

    // 紋理快取命中次數。公開唯讀供診斷與測試讀取；寫入一律經 Interlocked。
    public static int CacheLoadHits
    {
        get => Volatile.Read(ref cacheLoadHits);
        internal set => Interlocked.Exchange(ref cacheLoadHits, value);
    }
    // 紋理快取失敗次數。公開唯讀供診斷與測試讀取；寫入一律經 Interlocked。
    public static int CacheLoadFailures
    {
        get => Volatile.Read(ref cacheLoadFailures);
        internal set => Interlocked.Exchange(ref cacheLoadFailures, value);
    }
    private static int cacheLoadHits;
    private static int cacheLoadFailures;

    static ModContentLoaderTexture2D_LoadTexture_Patch()
    {
        SessionLifecycle.On(LifecyclePhase.StartupCompleted, static () =>
        {
            int configuredEntries = FasterGameLoadingMod.Instance?.CacheManager?.CacheCount ?? 0;
            if (cacheLoadHits > 0 || cacheLoadFailures > 0 || configuredEntries > 0)
            {
                FGLLog.Message($"Texture downscale cache hits: {cacheLoadHits.ToString(CultureInfo.InvariantCulture)}, failures: {cacheLoadFailures.ToString(CultureInfo.InvariantCulture)}, configured entries: {configuredEntries.ToString(CultureInfo.InvariantCulture)}");
            }
        });
    }

    private static bool TryServeCachedTexture(string fullPath, out Texture2D result)
    {
        if (ProtectedMods.IsProtectedTexturePath(fullPath))
        {
            result = null;
            return false;
        }

        return LoadedTextureRegistry.TryGetTexture(fullPath, out result)
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

    /// <summary>
    /// 嘗試以磁碟上的降質快取取代原始紋理，載入流程與原版載入原始檔相同（見 <see cref="LoadCacheFile"/>）。
    /// 1.7 的 LoadFromFile 只讀中繼資料、像素延後才解碼，壞檔不會在這裡失敗，
    /// 因此先檢查 PNG 標頭：快取檔不是有效 PNG 或載入失敗時移除該快取項目並回報未命中，由原始方法接手。
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
            int width = 0;
            int height = 0;
            var tex = PngUtils.TryGetImageDimensions(cachePath, ref width, ref height) ? LoadCacheFile(cachePath) : null;
            if (tex != null)
            {
                tex.name = Path.GetFileNameWithoutExtension(fullPath);
                // 原版 LoadFromFile 只對官方內容強制 Clamp；快取檔不在官方目錄，依原始路徑補上。
                if (UnityStubs.ImportDefaults.IsOfficial(fullPath))
                {
                    tex.wrapMode = TextureWrapMode.Clamp;
                }
                LoadedTextureRegistry.Record(fullPath, tex);
                Interlocked.Increment(ref cacheLoadHits);
                result = tex;
                return true;
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
        Interlocked.Increment(ref cacheLoadFailures);
        return false;
    }

    // 原始方法載入成功後將紋理登記到 registry，供後續查詢使用。
    public static void Postfix(VirtualFile file, bool __state, Texture2D __result)
    {
        if (__state && __result != null)
        {
            if (ProtectedMods.IsProtectedTexturePath(file.FullPath)) return;

            LoadedTextureRegistry.Record(file.FullPath, __result);
        }
    }
}
