using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 管理降質紋理快取的生命週期與對照表。
    /// </summary>
    public class TextureCacheManager
    {
        /// <summary>原始路徑 → 降質快取路徑的對照表（會透過 Scribe 持久化）。</summary>
        internal Dictionary<string, string> resizedTextureCache = new Dictionary<string, string>();
        private readonly object cacheLock = new object();
        private readonly ConcurrentDictionary<string, string> md5HashCache = new ConcurrentDictionary<string, string>();
        private readonly string baseCacheDir;

        /// <summary>紋理快取的根目錄。</summary>
        public string CacheDirectory => baseCacheDir ?? Path.Combine(GenFilePaths.SaveDataFolderPath, FGLConsts.ModName, FGLConsts.TextureCacheDir);
        
        internal string BuildCacheDirectory(string suffix)
        {
            if (baseCacheDir != null)
            {
                return Path.Combine(Path.GetDirectoryName(baseCacheDir), suffix);
            }
            return Path.Combine(GenFilePaths.SaveDataFolderPath, FGLConsts.ModName, suffix);
        }
        
        private string activeCacheDirectory;

        public TextureCacheManager()
        {
            activeCacheDirectory = CacheDirectory;
        }

        internal TextureCacheManager(string customBaseDir)
        {
            this.baseCacheDir = customBaseDir;
            activeCacheDirectory = CacheDirectory;
        }

        /// <summary>
        /// 根據原始檔案路徑產生 MD5 快取檔案路徑。
        /// 快取鍵結合路徑、檔案大小和最後修改時間，確保原始檔案變更時自動失效。
        /// </summary>
        public string GetCachePath(string originalPath)
        {
            return md5HashCache.GetOrAdd(GetCacheKey(originalPath), key =>
            {
                using (var md5 = MD5.Create())
                {
                    var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
                    var sb = new StringBuilder();
                    foreach (var b in hash)
                    {
                        sb.Append(b.ToString("x2"));
                    }
                    return Path.Combine(activeCacheDirectory, sb.ToString() + ".png");
                }
            });
        }

        private string GetCacheKey(string originalPath)
        {
            try
            {
                var file = new FileInfo(originalPath);
                if (file.Exists)
                {
                    return originalPath + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
                }
            }
            catch (IOException ex)
            {
                // 無法讀取檔案資訊（路徑過長、權限不足等），改用純路徑作為快取鍵
                if (FasterGameLoadingSettings.VerboseLogging)
                {
                    FGLLog.Warning("IOException when getting cache key for: " + originalPath, ex);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                // 權限不足，改用純路徑作為快取鍵
                if (FasterGameLoadingSettings.VerboseLogging)
                {
                    FGLLog.Warning("UnauthorizedAccessException when getting cache key for: " + originalPath, ex);
                }
            }
            return originalPath;
        }

        /// <summary>目前快取的紋理數量（執行緒安全）。</summary>
        public int CacheCount
        {
            get
            {
                lock (cacheLock)
                {
                    return resizedTextureCache.Count;
                }
            }
        }

        /// <summary>
        /// 嘗試取得指定原始路徑對應的快取紋理路徑。
        /// 自動檢查快取是否過期（原始檔案比快取檔案新時視為失效）。
        /// </summary>
        public bool TryGetCachedTexturePath(string originalPath, out string cachePath)
        {
            lock (cacheLock)
            {
                if (resizedTextureCache.TryGetValue(originalPath, out cachePath)
                    && File.Exists(cachePath)
                    && IsCacheFresh(originalPath, cachePath))
                {
                    return true;
                }

                if (cachePath != null)
                {
                    resizedTextureCache.Remove(originalPath);
                }
            }

            cachePath = null;
            return false;
        }

        /// <summary>
        /// 檢查快取是否比原始檔案更新。若無法讀取檔案時間則視為失效。
        /// </summary>
        private bool IsCacheFresh(string originalPath, string cachePath)
        {
            try
            {
                if (!File.Exists(originalPath))
                {
                    return true;
                }
                return File.GetLastWriteTimeUtc(cachePath) >= File.GetLastWriteTimeUtc(originalPath);
            }
            catch (IOException ex)
            {
                if (FasterGameLoadingSettings.VerboseLogging)
                {
                    FGLLog.Warning("IOException checking cache freshness for: " + originalPath + " and " + cachePath, ex);
                }
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                if (FasterGameLoadingSettings.VerboseLogging)
                {
                    FGLLog.Warning("UnauthorizedAccessException checking cache freshness for: " + originalPath + " and " + cachePath, ex);
                }
                return false;
            }
        }

        /// <summary>移除指定原始路徑的快取項目。</summary>
        public void RemoveCachedTexturePath(string originalPath)
        {
            lock (cacheLock)
            {
                resizedTextureCache.Remove(originalPath);
            }
        }

        /// <summary>清除所有紋理快取（檔案 + 記憶體對照表）。</summary>
        public void ClearCache()
        {
            try
            {
                if (Directory.Exists(CacheDirectory))
                {
                    Directory.Delete(CacheDirectory, true);
                }
                lock (cacheLock)
                {
                    resizedTextureCache.Clear();
                }
                FGLLog.Message("Texture cache cleared.");
            }
            catch (IOException ex)
            {
                FGLLog.Error("Failed to clear texture cache: ", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                FGLLog.Error("Failed to clear texture cache: ", ex);
            }
        }

        /// <summary>初始化縮放工作暫存目錄與快取對照表。</summary>
        internal void SetupResizeStagingDirectory(string stagingDirectory)
        {
            md5HashCache.Clear();
            activeCacheDirectory = stagingDirectory;
            try
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, true);
                }
                Directory.CreateDirectory(stagingDirectory);
            }
            catch (Exception ex)
            {
                FGLLog.Error("Failed to setup staging directory: " + stagingDirectory, ex);
            }
            lock (cacheLock)
            {
                resizedTextureCache.Clear();
            }
        }

        /// <summary>還原快取狀態到上一次的快取對照表與目錄配置。</summary>
        internal void RestorePreviousCacheState(
            Dictionary<string, string> previousCacheMap,
            string previousCacheDirectory,
            string stagingDirectory)
        {
            lock (cacheLock) { resizedTextureCache = previousCacheMap; }
            activeCacheDirectory = previousCacheDirectory;
            md5HashCache.Clear();
            try
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, true);
                }
            }
            catch (Exception ex)
            {
                FGLLog.Warning("Failed to clean up staging directory: " + stagingDirectory + " error: " + ex.Message);
            }
        }

        /// <summary>將暫存目錄替換為正式快取目錄，並重建相對路徑對照表。</summary>
        internal void ReplaceTextureCacheDirectory(string stagingDirectory)
        {
            bool moved = false;
            try
            {
                if (Directory.Exists(CacheDirectory))
                {
                    Directory.Delete(CacheDirectory, true);
                }
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Move(stagingDirectory, CacheDirectory);
                    moved = true;
                }
            }
            catch (Exception ex)
            {
                FGLLog.Error("Failed to replace texture cache directory. Falling back.", ex);
            }

            if (moved)
            {
                var updatedCacheMap = new Dictionary<string, string>();
                foreach (var kvp in resizedTextureCache)
                {
                    updatedCacheMap[kvp.Key] = Path.Combine(CacheDirectory, Path.GetFileName(kvp.Value));
                }
                lock (cacheLock) { resizedTextureCache = updatedCacheMap; }
                activeCacheDirectory = CacheDirectory;
            }
            else
            {
                lock (cacheLock) { resizedTextureCache.Clear(); }
                activeCacheDirectory = CacheDirectory;
            }
            md5HashCache.Clear();
        }

        /// <summary>提供向內部字典新增項目的執行緒安全介面。</summary>
        internal void SetCacheEntry(string originalPath, string cachePath)
        {
            lock (cacheLock)
            {
                resizedTextureCache[originalPath] = cachePath;
            }
        }
    }
}
