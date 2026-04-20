using HarmonyLib;
using System;
using Verse;

namespace FasterGameLoading
{
    [HarmonyPatch(typeof(Log), nameof(Log.Warning), new Type[] { typeof(string) })]
    public static class Log_Warning_Patch
    {
        public static bool Prefix(string text)
        {
            // FGL 的紋理快取機制保證紋理不會被實際載入兩次，這些 warning 是無害噪音
            if (text.StartsWith("Tried to load duplicate"))
                return false;
            return true;
        }
    }
}
