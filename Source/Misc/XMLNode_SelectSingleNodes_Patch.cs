using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Xml;

namespace FasterGameLoading
{
    [HarmonyPatch(typeof(XmlNode), nameof(XmlNode.SelectSingleNode), new Type[] {typeof(string)})]
    public static class XmlNode_SelectSingleNode_Patch
    {
        public static HashSet<string> failedXMLPathesThisSession = new HashSet<string>();
        public static HashSet<string> successfulXMLPathesThisSession = new HashSet<string>();
        public static bool Prefix(string xpath)
        {
            if (FasterGameLoadingSettings.failedXMLPathesSinceLastSession.Contains(xpath) && !FasterGameLoadingSettings.successfulXMLPathesSinceLastSession.Contains(xpath))
            {
                return false;
            }
            return true;
        }
        public static void Postfix(string xpath, XmlNode __result)
        {
            if (__result is null)
            {
                failedXMLPathesThisSession.Add(xpath);
            }
            else
            {
                successfulXMLPathesThisSession.Add(xpath);
            }
        }
    }
}

