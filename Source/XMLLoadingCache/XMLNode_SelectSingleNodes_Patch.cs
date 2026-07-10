using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Xml;
using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 XmlNode.SelectSingleNode，利用跨 session 快取跳過已知不存在的 XPath 查詢。
    /// 上次 session 中回傳 null 的 XPath 路徑會在本次直接被攔截，節省重複的 XML 走訪時間。
    /// </summary>
    [HarmonyPatch(typeof(XmlNode), nameof(XmlNode.SelectSingleNode), new Type[] { typeof(string) })]
    public static class XmlNode_SelectSingleNode_Patch
    {
        /// <summary>
        /// 記錄本次 session 所有 XPath 查詢結果：true=節點存在、false=查無節點
        /// 使用 ConcurrentDictionary 以確保多執行緒環境下安全
        /// </summary>
        public static ConcurrentDictionary<string, bool> xmlPathsThisSession = new ConcurrentDictionary<string, bool>();
        private static volatile bool patchEnabled = true;

        /// <summary>
        /// 背景 XML 檔案掃描與雜湊比對是否已完成。
        /// </summary>
        public static volatile bool isXmlScanComplete = false;

        /// <summary>
        /// 標記當前執行緒是否處於補丁套用（PatchOperation.Apply）流程中。
        /// </summary>
        [ThreadStatic]
        public static bool isInPatchOperation;

        static XmlNode_SelectSingleNode_Patch()
        {
            CacheResetter.Register(() =>
            {
                isXmlScanComplete = false;
                xmlPathsThisSession.Clear();
                isXmlExtensionsActive = null;
            });

            Startup.RegisterOnStartupCompleted(() =>
            {
                foreach (var kvp in xmlPathsThisSession)
                {
                    if (!kvp.Value)
                    {
                        SessionCache.xmlPathsSinceLastSession.TryAdd(kvp.Key, 0);
                    }
                }
                xmlPathsThisSession.Clear();

                if (XmlChangeDetector.needWriteSettings)
                {
                    XmlChangeDetector.needWriteSettings = false;
                    try
                    {
                        LoadedModManager.GetMod<FasterGameLoadingMod>().WriteSettings();
                        if (FasterGameLoadingSettings.VerboseLogging)
                        {
                            FGLLog.Message("XML cache invalidated and new hash saved to settings on main thread at startup completion.");
                        }
                    }
                    catch (Exception ex)
                    {
                        FGLLog.Warning("Failed to save updated XML combined hash at startup completion:", ex);
                    }
                }
            });
        }

        public static void DisableAndClear()
        {
            patchEnabled = false;
            isXmlScanComplete = false;
            xmlPathsThisSession.Clear();
            SessionCache.xmlPathsSinceLastSession.Clear();
        }

        private static readonly object xmlExtensionsLock = new object();
        private static bool? isXmlExtensionsActive;
        public static bool IsXmlExtensionsActive
        {
            get
            {
                if (!isXmlExtensionsActive.HasValue)
                {
                    lock (xmlExtensionsLock)
                    {
                        if (!isXmlExtensionsActive.HasValue)
                        {
                            try
                            {
                                isXmlExtensionsActive = ModsConfig.IsActive("krafs.xmlextensions");
                            }
                            catch
                            {
                                isXmlExtensionsActive = false;
                            }
                        }
                    }
                }
                return isXmlExtensionsActive.Value;
            }
        }

        internal static bool IsCacheableXpath(string xpath)
        {
            if (string.IsNullOrEmpty(xpath)) return false;
            // 含有屬性篩選的 XPath (如 [@...) 屬於補丁或特定定位，不安全，不應進行快取
            if (xpath.Contains("[@")) return false;

            // 只有包含 '/'，或者以 'Defs'、'/'、'[' 開頭的 XPath 查詢才被認為是定位用的 XPath，可以安全地進行快取。
            // 避免誤快取像是 'settingsKey', 'match', 'nomatch', 'value', 'xpath' 這樣的局部子節點欄位名稱。
            return xpath.Contains("/") ||
                   xpath.StartsWith("Defs", StringComparison.OrdinalIgnoreCase) ||
                   xpath.StartsWith("/") ||
                   xpath.StartsWith("[");
        }

        public static bool Prefix(string xpath, ref XmlNode __result)
        {
            if (isInPatchOperation || !isXmlScanComplete || !patchEnabled || !FasterGameLoadingSettings.XPathCaching || IsXmlExtensionsActive || Utils.IsMissileGirlActive)
            {
                return true;
            }

            if (!IsCacheableXpath(xpath))
            {
                return true;
            }

            bool found = SessionCache.xmlPathsSinceLastSession.ContainsKey(xpath);

            if (found)
            {
                __result = null;
                return false;
            }
            return true;
        }


        public static void Postfix(string xpath, XmlNode __result, bool __runOriginal)
        {
            if (isInPatchOperation || !__runOriginal || !patchEnabled || !FasterGameLoadingSettings.XPathCaching || IsXmlExtensionsActive || Utils.IsMissileGirlActive)
            {
                return;
            }

            if (!IsCacheableXpath(xpath))
            {
                return;
            }

            // 同一 XPath 可在多份 XML 中有不同結果；只要曾命中就不可持久化為不存在。
            xmlPathsThisSession.AddOrUpdate(xpath, __result is not null, (_, wasEverMatched) => wasEverMatched || __result is not null);
        }
    }

    /// <summary>
    /// 攔截 ModContentPack.LoadPatches，在補丁套用期間標記 isInPatchOperation，
    /// 以免 XPath 查詢被錯誤地全域快取為 null，導致補丁失效。
    /// </summary>
    [HarmonyPatch(typeof(ModContentPack), nameof(ModContentPack.LoadPatches))]
    public static class ModContentPack_LoadPatches_Patch
    {
        public static void Prefix()
        {
            XmlNode_SelectSingleNode_Patch.isInPatchOperation = true;
        }

        public static void Postfix()
        {
            XmlNode_SelectSingleNode_Patch.isInPatchOperation = false;
        }

        public static void Finalizer()
        {
            XmlNode_SelectSingleNode_Patch.isInPatchOperation = false;
        }
    }
}
