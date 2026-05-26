using HarmonyLib;
using System;
using System.Collections.Concurrent;
using System.Xml;

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
        private static bool patchEnabled = true;
        
        /// <summary>
        /// 背景 XML 檔案掃描與雜湊比對是否已完成。
        /// </summary>
        public static volatile bool isXmlScanComplete = false;

        static XmlNode_SelectSingleNode_Patch()
        {
            CacheResetter.Register(() =>
            {
                isXmlScanComplete = false;
                xmlPathsThisSession.Clear();
            });

            Startup.RegisterOnStartupCompleted(() =>
            {
                lock (SessionCache.xmlPathsLock)
                {
                    SessionCache.xmlPathsSinceLastSession.Clear();
                    foreach (var kvp in xmlPathsThisSession)
                    {
                        if (!kvp.Value)
                        {
                            SessionCache.xmlPathsSinceLastSession.Add(kvp.Key);
                        }
                    }
                }
                DisableAndClear();
            });
        }

        public static void DisableAndClear()
        {
            patchEnabled = false;
            isXmlScanComplete = false;
            xmlPathsThisSession.Clear();
            lock (SessionCache.xmlPathsLock)
            {
                SessionCache.xmlPathsSinceLastSession.Clear();
            }
        }

        public static bool Prefix(string xpath, ref XmlNode __result)
        {
            if (!isXmlScanComplete || !patchEnabled || !FasterGameLoadingSettings.XPathCaching)
            {
                return true;
            }

            bool found = false;
            lock (SessionCache.xmlPathsLock)
            {
                found = SessionCache.xmlPathsSinceLastSession.Contains(xpath);
            }

            if (found)
            {
                __result = null;
                return false;
            }
            return true;
        }


        public static void Postfix(string xpath, XmlNode __result, bool __runOriginal)
        {
            if (!__runOriginal || !patchEnabled || !FasterGameLoadingSettings.XPathCaching)
            {
                return;
            }
            xmlPathsThisSession[xpath] = __result is not null;
        }
    }
}
