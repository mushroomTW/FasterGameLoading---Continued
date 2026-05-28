namespace FasterGameLoading
{
    /// <summary>
    /// 靜態圖集快取系統介面。
    /// </summary>
    public interface IAtlasCacheManager
    {
        /// <summary>快取儲存目錄。</summary>
        string CacheDirectory { get; }

        /// <summary>快取清單檔案路徑。</summary>
        string ManifestPath { get; }

        /// <summary>清除所有圖集快取。</summary>
        void ClearCache();
    }
}
