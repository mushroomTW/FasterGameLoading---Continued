using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Verse;
namespace FasterGameLoading
{
    /// <summary>
    /// 管理降質紋理快取的生命週期與對照表。
    /// </summary>
    public class TextureCacheManager
    {
        /// <summary>原始路徑 → 降質快取路徑的對照表（會透過 Scribe 持久化）。</summary>
        internal Dictionary<string, string> resizedTextureCache = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>原始路徑 → 降質快取路徑的對照表（唯讀檢視；寫入請走 SetCacheEntry）。</summary>
        public IReadOnlyDictionary<string, string> ResizedTextureCache => resizedTextureCache;
        private readonly object cacheLock = new object();
        /// <summary>
        /// 串行化會動到快取目錄檔案的維護工作：重建、背景清理與清除。
        /// 背景清理若與重建的目錄升級交錯，會把剛搬進正式目錄、尚未登記的新快取檔當成未引用檔刪掉。
        /// </summary>
        private readonly object maintenanceLock = new object();
        /// <summary>
        /// 每執行緒重用的 MD5 實例。MD5 非執行緒安全，故以 ThreadLocal 隔離；
        /// 重用避免每次 GetCachePath 計算雜湊時都 allocate 新 MD5 與其原生資源。
        /// </summary>
        private static readonly ThreadLocal<MD5> md5PerThread =
            new ThreadLocal<MD5>(() => MD5.Create());
        private readonly string baseCacheDir;

        /// <summary>紋理快取的根目錄。</summary>
        public string CacheDirectory => baseCacheDir ?? Path.Combine(GenFilePaths.SaveDataFolderPath, FGLConsts.ModName, FGLConsts.TextureCacheDir);

        /// <summary>與正式快取目錄同層的兄弟目錄（重建用的暫存目錄）。</summary>
        private string BuildSiblingDirectory(string name)
        {
            if (baseCacheDir != null)
            {
                return Path.Combine(Path.GetDirectoryName(baseCacheDir), name);
            }
            return Path.Combine(GenFilePaths.SaveDataFolderPath, FGLConsts.ModName, name);
        }

        public TextureCacheManager()
        {
        }

        internal TextureCacheManager(string customBaseDir)
        {
            this.baseCacheDir = customBaseDir;
        }

        /// <summary>
        /// 根據原始檔案路徑產生正式快取目錄下的 MD5 快取檔案路徑。
        /// 快取鍵結合路徑、檔案大小和最後修改時間，確保原始檔案變更時自動失效。
        /// </summary>
        public string GetCachePath(string originalPath)
        {
            return ComputeCachePathFromKey(GetCacheKey(originalPath), CacheDirectory);
        }

        private static string ComputeCachePathFromKey(string key, string directory)
        {
            var md5 = md5PerThread.Value;
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                sb.Append(b.ToString("x2"));
            }
            return Path.Combine(directory, sb.ToString() + ".png");
        }

        private static string GetCacheKey(string originalPath)
        {
            try
            {
                var file = new FileInfo(originalPath);
                if (file.Exists)
                {
                    // 快取鍵必須與執行時語系無關，故數值一律以 InvariantCulture 格式化，
                    // 否則不同語系下同一檔案會產生不同的鍵而重複降質。
                    return originalPath.NormalizePath()
                        + "|" + file.Length.ToString(CultureInfo.InvariantCulture)
                        + "|" + file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // 無法讀取檔案資訊或權限不足時，改用純路徑作為快取鍵
                if (FasterGameLoadingSettings.VerboseLogging)
                {
                    FGLLog.Warning($"{ex.GetType().Name} when getting cache key for: {originalPath}", ex);
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
            bool fresh = IsManagedCacheFile(candidatePath) && File.Exists(candidatePath) && IsCacheFresh(originalPath, candidatePath);

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
        /// 檢查快取是否仍對應目前原始檔案；若原始檔較新，重新核對包含大小與修改時間的快取鍵。
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

                // 原始檔案的修改時間比快取新。只有目前路徑、大小和修改時間算出的鍵仍與快取路徑一致時，
                // 才能更新快取時間；原始檔的大小或修改時間變更會產生不同鍵，使舊快取失效。
                var currentExpectedPath = GetCachePath(originalPath);
                if (string.Equals(currentExpectedPath, cachePath, StringComparison.OrdinalIgnoreCase))
                {
                    File.SetLastWriteTimeUtc(cachePath, originalTime);
                    return true;
                }

                return false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (FasterGameLoadingSettings.VerboseLogging)
                {
                    FGLLog.Warning($"{ex.GetType().Name} checking cache freshness for: {originalPath} and {cachePath}", ex);
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

        /// <summary>
        /// 移除原始檔不在任何指定根目錄下的快取項目（例如已不在 mod 清單中的 mod），回傳移除數量。
        /// 被移除項目的快取檔不再被引用，之後由 <see cref="CleanupObsoleteCacheFiles"/> 刪除。
        /// 沒有任何根目錄時不做事，避免在執行中的 mod 清單尚未就緒時等同於清空快取。
        /// </summary>
        public int RemoveEntriesOutside(IEnumerable<string> rootDirectories)
        {
            var roots = new List<string>();
            foreach (var root in rootDirectories)
            {
                if (!string.IsNullOrEmpty(root))
                {
                    // 補上結尾斜線，避免 "Mods/A" 被當成 "Mods/AB/x.png" 的上層目錄。
                    roots.Add(root.NormalizePath().TrimEnd('/') + "/");
                }
            }
            if (roots.Count is 0)
            {
                return 0;
            }

            lock (cacheLock)
            {
                var keysToRemove = new List<string>();
                foreach (var originalPath in resizedTextureCache.Keys)
                {
                    var normalizedPath = originalPath.NormalizePath();
                    if (!roots.Exists(root => normalizedPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                    {
                        keysToRemove.Add(originalPath);
                    }
                }
                foreach (var key in keysToRemove)
                {
                    resizedTextureCache.Remove(key);
                }
                return keysToRemove.Count;
            }
        }

        /// <summary>清除所有紋理快取（檔案 + 記憶體對照表）。會等進行中的背景清理結束，主執行緒呼叫端應放在長事件內。</summary>
        public void ClearCache()
        {
            lock (maintenanceLock)
            {
                try
                {
                    if (Directory.Exists(CacheDirectory))
                    {
                        Directory.Delete(CacheDirectory, recursive: true);
                    }
                    lock (cacheLock)
                    {
                        resizedTextureCache.Clear();
                    }
                    FGLLog.Message("Texture cache cleared.");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    FGLLog.Error("Failed to clear texture cache:", ex);
                }
            }
        }

        /// <summary>
        /// 重建期間的寫入區：快取檔寫在暫存目錄，對照項目累積在自己的表內。
        /// 正式快取目錄與對照表在 <see cref="Rebuild"/> 升級成功前都不會被動到。
        /// </summary>
        public sealed class CacheRebuild
        {
            internal CacheRebuild(string stagingDirectory)
            {
                StagingDirectory = stagingDirectory;
            }

            internal string StagingDirectory { get; }

            internal Dictionary<string, string> Entries { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>原始檔對應的暫存快取檔路徑（檔名與升級後的正式路徑相同）。</summary>
            public string GetCachePath(string originalPath)
            {
                return ComputeCachePathFromKey(GetCacheKey(originalPath), StagingDirectory);
            }

            /// <summary>登記一筆已寫入暫存目錄的快取檔。</summary>
            public void Add(string originalPath, string cachePath)
            {
                Entries[originalPath] = cachePath;
            }
        }

        /// <summary>
        /// 以交易方式重建降質快取：<paramref name="populate"/> 把新快取寫進全新的暫存目錄並逐筆登記，
        /// 結束後整個暫存目錄升級為正式快取，對照表換成本次登記的項目。
        /// 回傳 true 表示已升級；沒有登記任何項目或升級失敗時回傳 false，
        /// <paramref name="populate"/> 拋出的例外原樣外傳。兩種失敗都保留原本的快取，並刪除暫存目錄。
        /// 重建期間正式快取照常可查詢，背景清理與清除會等重建結束才執行。
        /// 反過來本方法也會等進行中的背景清理結束；主執行緒呼叫端應放在長事件內，等待期間才有載入畫面。
        /// </summary>
        public bool Rebuild(Action<CacheRebuild> populate)
        {
            lock (maintenanceLock)
            {
                var staging = BuildSiblingDirectory(FGLConsts.TextureCacheStagingDir);
                try
                {
                    // 上一輪中斷留下的暫存檔必須清空，否則會被誤認為本輪產物而升級為正式快取。
                    if (Directory.Exists(staging))
                    {
                        Directory.Delete(staging, recursive: true);
                    }
                    Directory.CreateDirectory(staging);

                    var rebuild = new CacheRebuild(staging);
                    populate(rebuild);
                    return rebuild.Entries.Count > 0 && PromoteStaging(staging, rebuild.Entries);
                }
                finally
                {
                    DeleteStagingDirectory(staging);
                }
            }
        }

        /// <summary>刪除殘留的暫存目錄；升級成功時暫存目錄已被搬走，這裡不做事。</summary>
        private static void DeleteStagingDirectory(string stagingDirectory)
        {
            try
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
            catch (Exception ex)
            {
                FGLLog.Warning($"Failed to clean up staging directory: {stagingDirectory} error: {ex.Message}");
            }
        }

        /// <summary>將暫存目錄替換為正式快取目錄，並以重新指向正式目錄的新項目取代對照表。</summary>
        private bool PromoteStaging(string stagingDirectory, Dictionary<string, string> stagedEntries)
        {
            string backupDirectory = CacheDirectory + "_Backup";
            bool movedPreviousCache = false;
            bool movedStagingCache = false;
            try
            {
                if (Directory.Exists(backupDirectory))
                {
                    Directory.Delete(backupDirectory, recursive: true);
                }
                if (Directory.Exists(CacheDirectory))
                {
                    Directory.Move(CacheDirectory, backupDirectory);
                    movedPreviousCache = true;
                }
                Directory.Move(stagingDirectory, CacheDirectory);
                movedStagingCache = true;

                var promotedMap = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kvp in stagedEntries)
                {
                    promotedMap[kvp.Key] = Path.Combine(CacheDirectory, Path.GetFileName(kvp.Value));
                }
                lock (cacheLock)
                {
                    resizedTextureCache = promotedMap;
                }

                if (movedPreviousCache && Directory.Exists(backupDirectory))
                {
                    Directory.Delete(backupDirectory, recursive: true);
                }
                return true;
            }
            catch (Exception ex)
            {
                RollbackPromotion(movedStagingCache, movedPreviousCache, backupDirectory);
                FGLLog.Error("Failed to replace texture cache directory; previous cache was restored.", ex);
                return false;
            }
        }

        /// <summary>
        /// 升級失敗時的回滾：先移除已就位的暫存目錄，再把備份目錄搬回正式位置。
        /// 回滾本身失敗只記錄，不再向上拋出，避免掩蓋原始失敗原因。
        /// </summary>
        private void RollbackPromotion(bool movedStagingCache, bool movedPreviousCache, string backupDirectory)
        {
            try
            {
                if (movedStagingCache && Directory.Exists(CacheDirectory))
                {
                    Directory.Delete(CacheDirectory, recursive: true);
                }
                if (movedPreviousCache && Directory.Exists(backupDirectory) && !Directory.Exists(CacheDirectory))
                {
                    Directory.Move(backupDirectory, CacheDirectory);
                }
            }
            catch (Exception rollbackEx)
            {
                FGLLog.Error("Failed to restore previous texture cache directory.", rollbackEx);
            }
        }

        /// <summary>以讀檔結果取代整份對照表（執行緒安全）；設定檔沒有這份資料時換成空表。</summary>
        internal void ReplaceCacheMap(Dictionary<string, string> cacheMap)
        {
            lock (cacheLock)
            {
                resizedTextureCache = cacheMap ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }
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
        // MA0016: 刻意回傳具體的 Dictionary。回傳的是全新的私有副本，
        // 呼叫端修改它不會影響內部狀態；而設定存檔的 Scribe_Collections.Look
        // 只接受 ref Dictionary，改回唯讀介面只會逼出一次多餘的複製。
#pragma warning disable MA0016
        public Dictionary<string, string> GetResizedTextureCacheCopy()
#pragma warning restore MA0016
        {
            lock (cacheLock)
            {
                return new Dictionary<string, string>(resizedTextureCache, StringComparer.Ordinal);
            }
        }

        /// <summary>
        /// 清理過期與無效的快取檔案及對照項目。重建或清除進行中時會等它們結束。
        /// </summary>
        public void CleanupObsoleteCacheFiles()
        {
            lock (maintenanceLock)
            {
                CleanupObsoleteCacheFilesLocked();
            }
        }

        private void CleanupObsoleteCacheFilesLocked()
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

                var validCacheFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var keysToRemove = TriageCacheEntries(cacheEntries, validCacheFiles, out int deletedObsoleteFiles);

                RemoveCacheEntries(keysToRemove);

                // 刪除未引用檔案前，重新取得最新的有效路徑集合，
                // 防止快照拍攝後由 SetCacheEntry 併發新增的項目被誤刪。
                CollectValidCacheFilePaths(validCacheFiles);

                int deletedUnreferencedFiles = DeleteUnreferencedFiles(files, validCacheFiles);

                if (keysToRemove.Count > 0 || deletedObsoleteFiles > 0 || deletedUnreferencedFiles > 0)
                {
                    FGLLog.Message($"Cache cleanup completed. Removed {keysToRemove.Count.ToString(CultureInfo.InvariantCulture)} obsolete cache map entries, deleted {deletedObsoleteFiles.ToString(CultureInfo.InvariantCulture)} obsolete files and {deletedUnreferencedFiles.ToString(CultureInfo.InvariantCulture)} unreferenced files.");
                }
            }
            catch (Exception ex)
            {
                FGLLog.Error("Error during obsolete cache files cleanup:", ex);
            }
        }

        /// <summary>
        /// 逐一檢視快取對照表的項目，回傳應從對照表移除的鍵，
        /// 並把仍然有效的快取檔路徑收集到 <paramref name="validCacheFiles"/>。
        /// 原始檔已不存在者，其快取檔會就地刪除。
        /// </summary>
        private List<string> TriageCacheEntries(
            List<KeyValuePair<string, string>> cacheEntries,
            HashSet<string> validCacheFiles,
            out int deletedObsoleteFiles)
        {
            var keysToRemove = new List<string>();
            deletedObsoleteFiles = 0;

            foreach (var entry in cacheEntries)
            {
                if (!IsManagedCacheFile(entry.Value))
                {
                    // 指向 FGL 資料夾以外的項目不可信任：只從對照表移除，絕不刪檔。
                    keysToRemove.Add(entry.Key);
                }
                else if (!SafeFileExists(entry.Key))
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
                else if (!SafeFileExists(entry.Value))
                {
                    keysToRemove.Add(entry.Key);
                }
                else
                {
                    AddResolvedPath(validCacheFiles, entry.Value);
                }
            }

            return keysToRemove;
        }

        /// <summary>以執行緒安全方式從快取對照表移除指定的鍵。</summary>
        private void RemoveCacheEntries(List<string> keysToRemove)
        {
            if (keysToRemove.Count is 0)
            {
                return;
            }

            lock (cacheLock)
            {
                foreach (var key in keysToRemove)
                {
                    resizedTextureCache.Remove(key);
                }
            }
        }

        /// <summary>把目前對照表中所有快取檔路徑補進有效路徑集合。</summary>
        private void CollectValidCacheFilePaths(HashSet<string> validCacheFiles)
        {
            lock (cacheLock)
            {
                foreach (var kvp in resizedTextureCache)
                {
                    AddResolvedPath(validCacheFiles, kvp.Value);
                }
            }
        }

        /// <summary>刪除快取目錄中未被對照表引用的檔案，回傳實際刪除數量。</summary>
        private static int DeleteUnreferencedFiles(string[] files, HashSet<string> validCacheFiles)
        {
            int deleted = 0;
            foreach (var file in files)
            {
                try
                {
                    string fullPath = Path.GetFullPath(file);
                    if (!validCacheFiles.Contains(fullPath))
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }
                catch (Exception ex)
                {
                    FGLLog.Warning($"Failed to delete unreferenced cache file {file}: {ex.Message}");
                }
            }
            return deleted;
        }

        /// <summary>
        /// 快取檔是否位於 FGL 自己的資料夾內（正式快取、降質暫存與備份目錄的上一層）。
        /// 對照表的值來自設定檔，刪除或改動檔案前必須確認，避免損毀或被手動編輯的設定檔
        /// 讓清理流程動到其他檔案。
        /// </summary>
        private bool IsManagedCacheFile(string path)
        {
            try
            {
                var root = Path.GetDirectoryName(Path.GetFullPath(CacheDirectory));
                if (string.IsNullOrEmpty(root))
                {
                    return false;
                }
                // GetFullPath 會消去 ".."，避免 "TextureCache/../../x" 這類路徑以字首比對混過檢查。
                var fullPath = Path.GetFullPath(path).NormalizePath();
                return fullPath.StartsWith(root.NormalizePath().TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // 路徑格式無效時一律視為不受管理，交由呼叫端當作失效項目。
                return false;
            }
        }

        /// <summary>
        /// 檢查檔案是否存在；路徑無效或權限不足時一律視為「不存在」，
        /// 交由呼叫端把對應的快取項目當作失效處理。
        /// </summary>
        private static bool SafeFileExists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>把路徑正規化後加入集合；無法正規化時退回使用原始字串。</summary>
        private static void AddResolvedPath(HashSet<string> set, string path)
        {
            try
            {
                set.Add(Path.GetFullPath(path));
            }
            catch
            {
                set.Add(path);
            }
        }
    }
}
