using System.Collections.Generic;

namespace FasterGameLoading
{
    /// <summary>
    /// 紋理快取管理器介面。
    /// </summary>
    public interface ITextureCacheManager
    {
        /// <summary>原始路徑 → 降質快取路徑的對照表。</summary>
        System.Collections.Generic.Dictionary<string, string> ResizedTextureCache { get; }

        /// <summary>紋理快取的根目錄。</summary>
        string CacheDirectory { get; }

        /// <summary>目前快取的紋理數量。</summary>
        int CacheCount { get; }

        /// <summary>根據後綴建立快取目錄路徑。</summary>
        string BuildCacheDirectory(string suffix);

        /// <summary>根據原始檔案路徑產生快取檔案路徑。</summary>
        string GetCachePath(string originalPath);

        /// <summary>嘗試取得指定原始路徑對應的快取紋理路徑。</summary>
        bool TryGetCachedTexturePath(string originalPath, out string cachePath);

        /// <summary>移除指定原始路徑的快取項目。</summary>
        void RemoveCachedTexturePath(string originalPath);

        /// <summary>清除所有紋理快取。</summary>
        void ClearCache();

        /// <summary>初始化縮放工作暫存目錄與快取對照表。</summary>
        void SetupResizeStagingDirectory(string stagingDirectory);

        /// <summary>將暫存目錄替換為正式快取目錄，並重建相對路徑對照表。</summary>
        void ReplaceTextureCacheDirectory(string stagingDirectory);

        /// <summary>還原快取狀態到上一次的快取對照表與目錄配置。</summary>
        void RestorePreviousCacheState(
            System.Collections.Generic.Dictionary<string, string> previousCacheMap,
            string previousCacheDirectory,
            string stagingDirectory);

        /// <summary>提供向內部字典新增項目的執行緒安全介面。</summary>
        void SetCacheEntry(string originalPath, string cachePath);

        /// <summary>以執行緒安全方式回傳快取對照表的快照副本。</summary>
        System.Collections.Generic.Dictionary<string, string> GetResizedTextureCacheCopy();

        /// <summary>清理過期與無效的快取檔案及對照項目。</summary>
        void CleanupObsoleteCacheFiles();
    }
}
