using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 靜態圖集快取系統入口：定義快取目錄與描述結構，並提供快取清理介面。
    /// </summary>
    public static class StaticAtlasCache
    {
        /// <summary>快取儲存目錄。</summary>
        public static string CacheDirectory => Path.Combine(GenFilePaths.SaveDataFolderPath, "FasterGameLoading", "AtlasCache");
        
        /// <summary>快取清單檔案路徑。</summary>
        public static string ManifestPath => Path.Combine(CacheDirectory, "manifest.json");

        /// <summary>介面抽象的實體單例。</summary>
        public static IAtlasCacheManager Instance { get; } = new AtlasCacheManagerImpl();

        /// <summary>快取清單描述結構。</summary>
        [Serializable]
        public class Manifest
        {
            // version 4：ComputeModsHash 新增 BakingSkipList 根目錄、Unity/遊戲版本、壓縮設定折入，
            //            AtlasInfo 新增 mipCount、maskWidth、maskHeight 欄位。
            public int version = 4;
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
            public List<string> textureKeys = new List<string>();
            public List<Rect> uvRects = new List<Rect>();
            /// <summary>
            /// 彩色紋理的 mip 層數（0 或 1 均視為無 mip，向後相容舊 manifest）。
            /// 原版 CalcRectsForAtlasNew 會產生帶 mip chain 的 colorTexture，
            /// 快取還原時須使用相同 mipCount 以保留遠距離渲染品質。
            /// </summary>
            public int mipCount;
            /// <summary>
            /// 遮罩紋理的實際寬度（0 表示與 colorTexture 相同，向後相容）。
            /// fallback 路徑因 4 對齊可能與彩色紋理尺寸不同，需分開記錄。
            /// </summary>
            public int maskWidth;
            /// <summary>
            /// 遮罩紋理的實際高度（0 表示與 colorTexture 相同，向後相容）。
            /// </summary>
            public int maskHeight;
        }

        /// <summary>清除所有圖集快取（目錄 + 檔案）。</summary>
        public static void ClearCache()
        {
            Instance.ClearCache();
        }

        /// <summary>
        /// 內部清除快取邏輯。
        /// </summary>
        internal static void ClearCacheInternal()
        {
            try
            {
                if (Directory.Exists(CacheDirectory))
                {
                    Directory.Delete(CacheDirectory, true);
                    FGLLog.Message("FGL_Log_AtlasCacheCleared".TranslateWithFallback("Atlas cache cleared."));
                }
            }
            catch (IOException ex)
            {
                FGLLog.Warning("FGL_Log_FailedToClearAtlasCache".TranslateWithFallback("Failed to clear atlas cache: {0}", ex.Message));
            }
            catch (UnauthorizedAccessException ex)
            {
                FGLLog.Warning("FGL_Log_FailedToClearAtlasCache".TranslateWithFallback("Failed to clear atlas cache: {0}", ex.Message));
            }
        }

        private class AtlasCacheManagerImpl : IAtlasCacheManager
        {
            public string CacheDirectory => StaticAtlasCache.CacheDirectory;
            public string ManifestPath => StaticAtlasCache.ManifestPath;

            public void ClearCache()
            {
                ClearCacheInternal();
            }
        }
    }
}
