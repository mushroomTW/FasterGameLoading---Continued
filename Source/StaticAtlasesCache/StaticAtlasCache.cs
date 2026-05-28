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
            public int version = 2;
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
            public List<Rect> uvRects = new List<Rect>();
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
                    FGLLog.Message("Atlas cache cleared.");
                }
            }
            catch (IOException ex)
            {
                FGLLog.Warning("Failed to clear atlas cache: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                FGLLog.Warning("Failed to clear atlas cache: " + ex.Message);
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
