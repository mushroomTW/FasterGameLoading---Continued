using HarmonyLib;
using Verse;
using Verse.Sound;

namespace FasterGameLoading
{
    /// <summary>
    /// 在 SubSoundDef 尚未解析完畢前，攔截所有聲音播放請求。
    /// 避免提早播放導致 NullReferenceException 或播放損壞的音效。
    /// 解析完成後由 World_FinalizeInit_Patch 呼叫 Unpatch() 恢復正常。
    /// </summary>
    [HarmonyPatchCategory("SoundStarter")]
    [HarmonyPatch]
    internal static class SoundStarter_Patch
    {
        [HarmonyPatch(typeof(SoundStarter), "PlayOneShotOnCamera")]
        [HarmonyPrefix]
        static bool PlayOneShotOnCamera_Patch() => false;

        [HarmonyPatch(typeof(SoundStarter), "PlayOneShot")]
        [HarmonyPrefix]
        static bool PlayOneShot_Patch() => false;

        [HarmonyPatch(typeof(SoundStarter), "TrySpawnSustainer")]
        [HarmonyPrefix]
        static bool TrySpawnSustainer_Patch(ref Sustainer __result)
        {
            __result = null;
            return false;
        }

        [HarmonyPatch(typeof(SubSoundDef), nameof(SubSoundDef.TryPlay))]
        [HarmonyPrefix]
        static bool TryPlay_Patch() => false;

        private static bool unpatched = false;

        internal static void ResetUnpatchedStatus()
        {
            unpatched = false;
        }

        /// <summary>
        /// 取消此類別中所有 Harmony patch，恢復正常聲音播放。
        /// </summary>
        internal static void Unpatch()
        {
            if (unpatched) return;
            unpatched = true;
            FasterGameLoadingMod.harmony.UnpatchCategory("SoundStarter");
        }
    }
}