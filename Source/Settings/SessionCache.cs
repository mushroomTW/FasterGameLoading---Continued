using System.Collections.Generic;
using System.Linq;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 跨 session 的載入快取資料與執行期查詢快取。
    /// 這些不是「使用者設定」，而是自動記錄的載入歷程，
    /// 僅因需要 Scribe 持久化而存放於此。
    /// </summary>
    public static class SessionCache
    {
        // ── 跨 session 持久化資料（由 Scribe 存檔） ──

        /// <summary>
        /// 上一次 session 中所有已載入的紋理路徑映射。
        /// </summary>
        public static Dictionary<string, string> loadedTexturesSinceLastSession = new();

        /// <summary>
        /// 上一次 session 中所有已查詢的完整型別名稱映射。
        /// </summary>
        public static Dictionary<string, string> loadedTypesByFullNameSinceLastSession = new();

        /// <summary>
        /// 上一次 session 中啟用的 mod 列表（packageIdLowerCase）。
        /// </summary>
        public static List<string> modsInLastSession = new();

        /// <summary>
        /// 上一次 session 中所有 XPath 查詢結果。
        /// </summary>
        public static Dictionary<string, bool> xmlPathsSinceLastSession = new();

        /// <summary>
        /// 歷次靜態圖集烘焙速度記錄（用於自適應批次調整）。
        /// </summary>
        public static List<float> historicalBakeSpeeds = new();

        /// <summary>
        /// 加權移動平均的權重。
        /// </summary>
        public static readonly float[] WEIGHTS = { 0.4f, 0.3f, 0.2f, 0.1f };

        /// <summary>
        /// 保留的歷史記錄筆數上限。
        /// 需與 WEIGHTS 長度一致，確保加權平均能正確計算。
        /// </summary>
        public const int HISTORY_SIZE = 4;

        // ── 執行期查詢快取（不持久化） ──

        private static Dictionary<string, ModContentPack> modsByPackageIds = new();

        static SessionCache()
        {
            CacheResetter.Register(() => modsByPackageIds.Clear());
        }

        /// <summary>
        /// 根據 packageId 取得對應的 ModContentPack（含快取）。
        /// </summary>
        public static ModContentPack GetModContent(string packageId)
        {
            var packageLower = packageId.ToLower();
            if (!modsByPackageIds.TryGetValue(packageLower, out var mod))
            {
                modsByPackageIds[packageLower] = mod = LoadedModManager.RunningModsListForReading.FirstOrDefault(x =>
                    x.PackageIdPlayerFacing.ToLower() == packageLower);
            }
            return mod;
        }

        /// <summary>
        /// 由 FasterGameLoadingSettings.ExposeData() 委派呼叫，
        /// 處理所有跨 session 快取資料的序列化。
        /// </summary>
        public static void ExposeData()
        {
            Scribe_Collections.Look(ref loadedTexturesSinceLastSession, "loadedTexturesSinceLastSession", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref loadedTypesByFullNameSinceLastSession, "loadedTypesByFullNameSinceLastSession", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref xmlPathsSinceLastSession, "xmlPathsSinceLastSession", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref modsInLastSession, "modsInLastSession", LookMode.Value);
            Scribe_Collections.Look(ref historicalBakeSpeeds, "historicalBakeSpeeds", LookMode.Value, LookMode.Value);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                loadedTexturesSinceLastSession ??= new Dictionary<string, string>();
                loadedTypesByFullNameSinceLastSession ??= new Dictionary<string, string>();
                xmlPathsSinceLastSession ??= new Dictionary<string, bool>();
                modsInLastSession ??= new List<string>();
                historicalBakeSpeeds ??= new List<float>();

                // 使用 hash 比較偵測 mod 組合變更，避免 O(n) SequenceEqual
                int currentHash = 0;
                foreach (var mod in ModsConfig.ActiveModsInLoadOrder)
                    unchecked { currentHash = currentHash * 31 + mod.packageIdLowerCase.GetHashCode(); }

                int lastHash = 0;
                foreach (var modId in modsInLastSession)
                    unchecked { lastHash = lastHash * 31 + modId.GetHashCode(); }

                if (currentHash != lastHash)
                {
                    loadedTexturesSinceLastSession.Clear();
                    loadedTypesByFullNameSinceLastSession.Clear();
                    xmlPathsSinceLastSession.Clear();
                    TextureResize.ClearCache();
                    StaticAtlasCache.ClearCache();
                }
            }
        }
    }
}
