using HarmonyLib;
using System;
using System.Collections.Concurrent;
using System.Xml;

namespace FasterGameLoading
{
    [HarmonyPatch(typeof(XmlNode), nameof(XmlNode.SelectSingleNode), new Type[] { typeof(string) })]
    public static class XmlNode_SelectSingleNode_Patch
    {
        /// <summary>
        /// 記錄本次 session 所有 XPath 查詢結果：true=節點存在、false=查無節點
        /// 使用 ConcurrentDictionary 以確保多執行緒環境下安全
        /// </summary>
        public static ConcurrentDictionary<string, bool> xmlPathsThisSession = new ConcurrentDictionary<string, bool>();

        static XmlNode_SelectSingleNode_Patch()
        {
            CacheResetter.Register(() => xmlPathsThisSession.Clear());
        }

        public static bool Prefix(string xpath)
        {
            // 單一次 TryGetValue 查詢，取代原先兩次 Contains 呼叫
            if (SessionCache.xmlPathsSinceLastSession.TryGetValue(xpath, out bool succeeded) && !succeeded)
            {
                return false;
            }
            return true;
        }

        public static void Postfix(string xpath, XmlNode __result)
        {
            xmlPathsThisSession[xpath] = __result is not null;
        }
    }
}

