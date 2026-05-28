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
        /// <summary>
        /// 攔截主鏡頭單次音效播放。
        /// </summary>
        [HarmonyPatch(typeof(SoundStarter), "PlayOneShotOnCamera")]
        [HarmonyPrefix]
        static bool PlayOneShotOnCamera_Patch() => false;

        /// <summary>
        /// 攔截一般單次音效播放。
        /// </summary>
        [HarmonyPatch(typeof(SoundStarter), "PlayOneShot")]
        [HarmonyPrefix]
        static bool PlayOneShot_Patch() => false;

        /// <summary>
        /// 攔截持續性音效 (Sustainer) 生成，返回 null。
        /// </summary>
        [HarmonyPatch(typeof(SoundStarter), "TrySpawnSustainer")]
        [HarmonyPrefix]
        static bool TrySpawnSustainer_Patch(ref Sustainer __result)
        {
            __result = null;
            return false;
        }

        /// <summary>
        /// 攔截 SubSoundDef 的播放嘗試。
        /// </summary>
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