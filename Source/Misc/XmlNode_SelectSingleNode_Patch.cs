using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Xml;

namespace FasterGameLoading
{
    [HarmonyPatch(typeof(XmlNode), nameof(XmlNode.SelectSingleNode), new Type[] {typeof(string)})]
    public static class XmlNode_SelectSingleNode_Patch
    {
        public static HashSet<string> failedXMLPathsThisSession = new HashSet<string>();
        public static HashSet<string> successfulXMLPathsThisSession = new HashSet<string>();
        internal static readonly object syncLock = new object();

        public static bool Prefix(string xpath)
        {
            if (FasterGameLoadingSettings.failedXMLPathsSinceLastSession.Contains(xpath) && !FasterGameLoadingSettings.successfulXMLPathsSinceLastSession.Contains(xpath))
            {
                return false;
            }
            return true;
        }
        public static void Postfix(string xpath, XmlNode __result, bool __runOriginal)
        {
            if (!__runOriginal) return;
            lock (syncLock)
            {
                if (__result is null)
                {
                    failedXMLPathsThisSession.Add(xpath);
                }
                else
                {
                    successfulXMLPathsThisSession.Add(xpath);
                }
            }
        }
    }
}
