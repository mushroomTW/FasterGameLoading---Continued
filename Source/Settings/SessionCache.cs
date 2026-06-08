using System;
using System.Collections.Concurrent;
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
        internal static ConcurrentDictionary<string, string> loadedTypesByFullNameSinceLastSession = new();

        /// <summary>
        /// 上一次 session 中啟用的 mod 列表（packageIdLowerCase）。
        /// </summary>
        internal static List<string> modsInLastSession = new();

        /// <summary>
        /// 上一次 session 中被 Harmony patch 的組件名稱清單。
        /// </summary>
        internal static List<string> patchedAssembliesLastSession = new();
        internal static readonly object patchedAssembliesLock = new();

        /// <summary>
        /// 上一次 session 中所有 XPath 查詢結果（僅存缺失的 XPath 查詢）。
        /// </summary>
        internal static ConcurrentDictionary<string, byte> xmlPathsSinceLastSession = new();

        /// <summary>
        /// 上一次 session 中所有第三方 Mod 的 XML 檔案的累積雜湊值。
        /// </summary>
        internal static long xmlCombinedHashSinceLastSession = 0;


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

        static SessionCache()
        {
            if (WEIGHTS.Length != HISTORY_SIZE)
            {
                FGLLog.Error("WEIGHTS length must match HISTORY_SIZE!");
            }
        }

        /// <summary>
        /// 由 FasterGameLoadingSettings.ExposeData() 委派呼叫，
        /// 處理所有跨 session 快取資料的序列化。
        /// </summary>
        internal static void ExposeData()
        {
            Scribe_Collections.Look(ref loadedTexturesSinceLastSession, FGLConsts.LoadedTexturesKey, LookMode.Value, LookMode.Value);

            Dictionary<string, string> tempTypes = null;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                tempTypes = new Dictionary<string, string>(loadedTypesByFullNameSinceLastSession);
            }
            Scribe_Collections.Look(ref tempTypes, FGLConsts.LoadedTypesKey, LookMode.Value, LookMode.Value);

            Dictionary<string, bool> tempXmlPaths = null;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                tempXmlPaths = new Dictionary<string, bool>();
                foreach (var kvp in xmlPathsSinceLastSession)
                {
                    tempXmlPaths[kvp.Key] = false;
                }
            }
            Scribe_Collections.Look(ref tempXmlPaths, FGLConsts.XmlPathsKey, LookMode.Value, LookMode.Value);

            Scribe_Values.Look(ref xmlCombinedHashSinceLastSession, "FGL_XmlCombinedHash", 0L);
            Scribe_Collections.Look(ref modsInLastSession, FGLConsts.ModsInLastSessionKey, LookMode.Value);
            Scribe_Collections.Look(ref historicalBakeSpeeds, FGLConsts.HistoricalBakeSpeedsKey, LookMode.Value);
            Scribe_Collections.Look(ref patchedAssembliesLastSession, "patchedAssembliesLastSession", LookMode.Value);


            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                loadedTexturesSinceLastSession ??= new Dictionary<string, string>();
                
                if (tempTypes != null)
                {
                    loadedTypesByFullNameSinceLastSession = new ConcurrentDictionary<string, string>(tempTypes);
                }
                loadedTypesByFullNameSinceLastSession ??= new ConcurrentDictionary<string, string>();

                xmlPathsSinceLastSession ??= new ConcurrentDictionary<string, byte>();
                if (tempXmlPaths != null)
                {
                    xmlPathsSinceLastSession.Clear();
                    foreach (var kvp in tempXmlPaths)
                    {
                        if (!kvp.Value && XmlNode_SelectSingleNode_Patch.IsCacheableXpath(kvp.Key))
                        {
                            // 排除之前因 Bug 錯誤快取的 Ayameduki/WRelicK 相關補丁 XPath，或是包含定位符的 XPath
                            if (kvp.Key.Contains("AT_Tag_") || 
                                kvp.Key.Contains("KeyedSettings") || 
                                kvp.Key.Contains("FactionDef") ||
                                kvp.Key.Contains("[@"))
                            {
                                continue;
                            }
                            xmlPathsSinceLastSession.TryAdd(kvp.Key, 0);
                        }
                    }
                }
                
                modsInLastSession ??= new List<string>();
                historicalBakeSpeeds ??= new List<float>();
                lock (patchedAssembliesLock)
                {
                    patchedAssembliesLastSession ??= new List<string>();
                }

                // 零分配偵測 mod 組合變更，避免 GetHashCode 隨機雜湊種子碰撞與 MD5 重複記憶體配發
                var currentActiveMods = ModsConfig.ActiveModsInLoadOrder.ToList();
                bool modsChanged = false;
                if (modsInLastSession == null || currentActiveMods.Count != modsInLastSession.Count)
                {
                    modsChanged = true;
                }
                else
                {
                    for (int i = 0; i < modsInLastSession.Count; i++)
                    {
                        if (currentActiveMods[i].packageIdLowerCase != modsInLastSession[i])
                        {
                            modsChanged = true;
                            break;
                        }
                    }
                }

                if (modsChanged)
                {
                    lock (loadedTexturesSinceLastSession)
                    {
                        loadedTexturesSinceLastSession.Clear();
                    }
                    loadedTypesByFullNameSinceLastSession.Clear();
                    xmlPathsSinceLastSession.Clear();
                    lock (patchedAssembliesLock)
                    {
                        patchedAssembliesLastSession.Clear();
                    }
                    FasterGameLoadingMod.Instance.CacheManager.ClearCache();
                    StaticAtlasCache.ClearCache();
                }
            }
        }
    }
}
