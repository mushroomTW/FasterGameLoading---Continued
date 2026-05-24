using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 跨 session 的載入快取資料與執行期查詢快取。
    /// 這些不是「使用者設定」，而是自動記錄的載入歷程，
    /// 僅因需要 Scribe 持久化而存放於此。
    /// </summary>
    internal static class SessionCache
    {
        // ── 跨 session 持久化資料（由 Scribe 存檔） ──

        /// <summary>
        /// 上一次 session 中所有已載入的紋理路徑映射。
        /// </summary>
        internal static Dictionary<string, string> loadedTexturesSinceLastSession = new();

        /// <summary>
        /// 上一次 session 中所有已查詢的完整型別名稱映射。
        /// </summary>
        internal static Dictionary<string, string> loadedTypesByFullNameSinceLastSession = new();

        /// <summary>
        /// 上一次 session 中啟用的 mod 列表（packageIdLowerCase）。
        /// </summary>
        internal static List<string> modsInLastSession = new();

        /// <summary>
        /// 上一次 session 中所有 XPath 查詢結果。
        /// </summary>
        internal static Dictionary<string, bool> xmlPathsSinceLastSession = new();

        /// <summary>
        /// 歷次靜態圖集烘焙速度記錄（用於自適應批次調整）。
        /// </summary>
        internal static List<float> historicalBakeSpeeds = new();

        /// <summary>
        /// 加權移動平均的權重。
        /// </summary>
        internal static readonly float[] WEIGHTS = { 0.4f, 0.3f, 0.2f, 0.1f };

        /// <summary>
        /// 保留的歷史記錄筆數上限。
        /// 需與 WEIGHTS 長度一致，確保加權平均能正確計算。
        /// </summary>
        internal const int HISTORY_SIZE = 4;

        // ── 執行期查詢快取（不持久化） ──

        private static Dictionary<string, ModContentPack> modsByPackageIds = new();

        static SessionCache()
        {
            CacheResetter.Register(() => modsByPackageIds.Clear());
            if (WEIGHTS.Length != HISTORY_SIZE)
            {
                FGLLog.Error("WEIGHTS length must match HISTORY_SIZE!");
            }
        }

        /// <summary>
        /// 根據 packageId 取得對應 of ModContentPack（含快取）。
        /// </summary>
        internal static ModContentPack GetModContent(string packageId)
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
        internal static void ExposeData()
        {
            Scribe_Collections.Look(ref loadedTexturesSinceLastSession, FGLConsts.LoadedTexturesKey, LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref loadedTypesByFullNameSinceLastSession, FGLConsts.LoadedTypesKey, LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref xmlPathsSinceLastSession, FGLConsts.XmlPathsKey, LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref modsInLastSession, FGLConsts.ModsInLastSessionKey, LookMode.Value);
            Scribe_Collections.Look(ref historicalBakeSpeeds, FGLConsts.HistoricalBakeSpeedsKey, LookMode.Value);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                loadedTexturesSinceLastSession ??= new Dictionary<string, string>();
                loadedTypesByFullNameSinceLastSession ??= new Dictionary<string, string>();
                xmlPathsSinceLastSession ??= new Dictionary<string, bool>();
                modsInLastSession ??= new List<string>();
                historicalBakeSpeeds ??= new List<float>();

                // 使用 MD5 雜湊比較偵測 mod 組合變更，避免 GetHashCode 跨平台與版本間的隨機雜湊種子碰撞問題
                string currentModsStr = string.Join(",", ModsConfig.ActiveModsInLoadOrder.Select(x => x.packageIdLowerCase));
                string lastModsStr = string.Join(",", modsInLastSession);

                string currentHash;
                string lastHash;
                using (var md5 = MD5.Create())
                {
                    currentHash = string.Concat(md5.ComputeHash(Encoding.UTF8.GetBytes(currentModsStr)).Select(b => b.ToString("x2")));
                    lastHash = string.Concat(md5.ComputeHash(Encoding.UTF8.GetBytes(lastModsStr)).Select(b => b.ToString("x2")));
                }

                if (currentHash != lastHash)
                {
                    loadedTexturesSinceLastSession.Clear();
                    loadedTypesByFullNameSinceLastSession.Clear();
                    xmlPathsSinceLastSession.Clear();
                    TextureCacheManager.ClearCache();
                    StaticAtlasCache.ClearCache();
                }
            }
        }
    }
}
