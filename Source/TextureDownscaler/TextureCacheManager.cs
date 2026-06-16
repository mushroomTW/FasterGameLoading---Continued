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
    public class TextureCacheManager : ITextureCacheManager
    {
        /// <summary>原始路徑 → 降質快取路徑的對照表（會透過 Scribe 持久化）。</summary>
        internal Dictionary<string, string> resizedTextureCache = new Dictionary<string, string>();

        /// <summary>原始路徑 → 降質快取路徑的對照表（實作介面屬性）。</summary>
        public Dictionary<string, string> ResizedTextureCache => resizedTextureCache;
        private readonly object cacheLock = new object();
        private readonly ConcurrentDictionary<string, string> md5HashCache = new ConcurrentDictionary<string, string>();
        private readonly string baseCacheDir;

        /// <summary>紋理快取的根目錄。</summary>
        public string CacheDirectory => baseCacheDir ?? Path.Combine(GenFilePaths.SaveDataFolderPath, FGLConsts.ModName, FGLConsts.TextureCacheDir);
        
        public string BuildCacheDirectory(string suffix)
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
                    return originalPath + "|" + file.Length;
                }
            }
            catch (IOException ex)
            {
                // 無法讀取檔案資訊（路徑過長、權限不足等），改用純路徑作為快取鍵
                if (FasterGameLoadingSettings.VerboseLogging)
                {
                    FGLLog.Warning($"IOException when getting cache key for: {originalPath}", ex);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                // 權限不足，改用純路徑作為快取鍵
                if (FasterGameLoadingSettings.VerboseLogging)
                {
                    FGLLog.Warning($"UnauthorizedAccessException when getting cache key for: {originalPath}", ex);
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
        /// 磁碟 I/O（包括 SetLastWriteTimeUtc）在鎖定範圍外執行，避免阻塞並發紋理載入。
        /// </summary>
        public bool TryGetCachedTexturePath(string originalPath, out string cachePath)
        {
            // 第一步：在鎖內讀取對照表，取得候選快取路徑
            string candidatePath;
            bool hadEntry;
            lock (cacheLock)
            {
                hadEntry = resizedTextureCache.TryGetValue(originalPath, out candidatePath);
            }

            if (!hadEntry || candidatePath == null)
            {
                cachePath = null;
                return false;
            }

            // 第二步：在鎖外執行磁碟 I/O（存在性檢查 + 時間更新）
            bool fresh = File.Exists(candidatePath) && IsCacheFresh(originalPath, candidatePath);

            if (fresh)
            {
                cachePath = candidatePath;
                return true;
            }

            // 快取失效：移除對照表項目
            lock (cacheLock)
            {
                // 再次確認項目仍指向同一路徑，避免在鎖外期間已被更新
                if (resizedTextureCache.TryGetValue(originalPath, out var currentPath)
                    && string.Equals(currentPath, candidatePath, StringComparison.OrdinalIgnoreCase))
                {
                    resizedTextureCache.Remove(originalPath);
                }
            }

            cachePath = null;
            return false;
        }

        /// <summary>
        /// 檢查快取是否比原始檔案更新。若無法讀取檔案時間則視為失效。
        /// 此方法在 cacheLock 鎖定範圍外呼叫，可安全執行阻塞式磁碟 I/O。
        /// </summary>
        private bool IsCacheFresh(string originalPath, string cachePath)
        {
            try
            {
                if (!File.Exists(originalPath))
                {
                    return true;
                }

                var originalTime = File.GetLastWriteTimeUtc(originalPath);
                var cacheTime = File.GetLastWriteTimeUtc(cachePath);

                if (cacheTime >= originalTime)
                {
                    return true;
                }

                // 原始檔案的修改時間比快取新。若快取路徑的檔名雜湊（基於目前檔案長度）與已儲存的快取路徑一致，
                // 代表檔案長度並未改變（實質內容未變），此時只需將快取檔案的時間更新為原始檔案時間即可。
                var currentExpectedPath = GetCachePath(originalPath);
                if (string.Equals(currentExpectedPath, cachePath, StringComparison.OrdinalIgnoreCase))
                {
                    File.SetLastWriteTimeUtc(cachePath, originalTime);
                    return true;
                }

                return false;
            }
            catch (IOException ex)
            {
                if (FasterGameLoadingSettings.VerboseLogging)
                {
                    FGLLog.Warning($"IOException checking cache freshness for: {originalPath} and {cachePath}", ex);
                }
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                if (FasterGameLoadingSettings.VerboseLogging)
                {
                    FGLLog.Warning($"UnauthorizedAccessException checking cache freshness for: {originalPath} and {cachePath}", ex);
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
                FGLLog.Error("Failed to clear texture cache:", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                FGLLog.Error("Failed to clear texture cache:", ex);
            }
        }

        /// <summary>初始化縮放工作暫存目錄與快取對照表。</summary>
        public void SetupResizeStagingDirectory(string stagingDirectory)
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
                FGLLog.Error($"Failed to setup staging directory: {stagingDirectory}", ex);
            }
            lock (cacheLock)
            {
                resizedTextureCache.Clear();
            }
        }

        /// <summary>還原快取狀態到上一次的快取對照表與目錄配置。</summary>
        public void RestorePreviousCacheState(
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
                FGLLog.Warning($"Failed to clean up staging directory: {stagingDirectory} error: {ex.Message}");
            }
        }

        /// <summary>將暫存目錄替換為正式快取目錄，並重建相對路徑對照表。</summary>
        public void ReplaceTextureCacheDirectory(string stagingDirectory)
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
                lock (cacheLock)
                {
                    foreach (var kvp in resizedTextureCache)
                    {
                        updatedCacheMap[kvp.Key] = Path.Combine(CacheDirectory, Path.GetFileName(kvp.Value));
                    }
                    resizedTextureCache = updatedCacheMap;
                }
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
        public void SetCacheEntry(string originalPath, string cachePath)
        {
            lock (cacheLock)
            {
                resizedTextureCache[originalPath] = cachePath;
            }
        }

        /// <summary>以執行緒安全方式回傳快取對照表的快照副本。</summary>
        public Dictionary<string, string> GetResizedTextureCacheCopy()
        {
            lock (cacheLock)
            {
                return new Dictionary<string, string>(resizedTextureCache);
            }
        }

        /// <summary>
        /// 清理過期與無效的快取檔案及對照項目。
        /// </summary>
        public void CleanupObsoleteCacheFiles()
        {
            try
            {
                string activeDir = CacheDirectory;
                if (!Directory.Exists(activeDir))
                {
                    return;
                }

                string[] files = Directory.GetFiles(activeDir, "*.png");
                List<KeyValuePair<string, string>> cacheEntries;
                lock (cacheLock)
                {
                    cacheEntries = new List<KeyValuePair<string, string>>(resizedTextureCache);
                }

                var keysToRemove = new List<string>();
                var validCacheFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int deletedObsoleteFiles = 0;

                foreach (var entry in cacheEntries)
                {
                    bool originalExists = false;
                    try
                    {
                        originalExists = File.Exists(entry.Key);
                    }
                    catch { }

                    if (!originalExists)
                    {
                        keysToRemove.Add(entry.Key);
                        try
                        {
                            if (File.Exists(entry.Value))
                            {
                                File.Delete(entry.Value);
                                deletedObsoleteFiles++;
                            }
                        }
                        catch (Exception ex)
                        {
                            FGLLog.Warning($"Failed to delete obsolete cache file {entry.Value}: {ex.Message}");
                        }
                    }
                    else
                    {
                        bool cacheExists = false;
                        try
                        {
                            cacheExists = File.Exists(entry.Value);
                        }
                        catch { }

                        if (!cacheExists)
                        {
                            keysToRemove.Add(entry.Key);
                        }
                        else
                        {
                            try
                            {
                                validCacheFiles.Add(Path.GetFullPath(entry.Value));
                            }
                            catch
                            {
                                validCacheFiles.Add(entry.Value);
                            }
                        }
                    }
                }

                if (keysToRemove.Count > 0)
                {
                    lock (cacheLock)
                    {
                        foreach (var key in keysToRemove)
                        {
                            resizedTextureCache.Remove(key);
                        }
                    }
                }

                int deletedUnreferencedFiles = 0;
                foreach (var file in files)
                {
                    try
                    {
                        string fullPath = Path.GetFullPath(file);
                        if (!validCacheFiles.Contains(fullPath))
                        {
                            File.Delete(file);
                            deletedUnreferencedFiles++;
                        }
                    }
                    catch (Exception ex)
                    {
                        FGLLog.Warning($"Failed to delete unreferenced cache file {file}: {ex.Message}");
                    }
                }

                if (keysToRemove.Count > 0 || deletedObsoleteFiles > 0 || deletedUnreferencedFiles > 0)
                {
                    FGLLog.Message($"Cache cleanup completed. Removed {keysToRemove.Count} obsolete cache map entries, deleted {deletedObsoleteFiles} obsolete files and {deletedUnreferencedFiles} unreferenced files.");
                }
            }
            catch (Exception ex)
            {
                FGLLog.Error($"Error during obsolete cache files cleanup: {ex.Message}", ex);
            }
        }
    }
}
