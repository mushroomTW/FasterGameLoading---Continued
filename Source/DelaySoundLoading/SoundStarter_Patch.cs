using System;
using HarmonyLib;
using Verse;
using Verse.Sound;

namespace FasterGameLoading
{
    /// <summary>
    /// 在 SubSoundDef 尚未全部解析完畢前，播放請求會先當場解析用到的 SubSoundDef 再交回原版播放，
    /// 主選單的 UI 音效因此不必等整批解析完成；其餘 SubSoundDef 仍由 DelayedActions 分幀解析。
    /// 全部解析完成後由 DelayedActions 或 World_FinalizeInit_Patch 呼叫 Unpatch()，之後播放不再經過這裡。
    /// </summary>
    [HarmonyPatchCategory("SoundStarter")]
    [HarmonyPatch]
    internal static class SoundStarter_Patch
    {
        /// <summary>
        /// 單次音效：PlayOneShotOnCamera 與 PlayOneShot 最後都會逐一呼叫 SubSoundDef.TryPlay。
        /// 不在主執行緒或仍無法播放時略過這次播放，與解除攔截前的舊行為相同，避免原版記錄「No resolved grains」錯誤。
        /// </summary>
        [HarmonyPatch(typeof(SubSoundDef), nameof(SubSoundDef.TryPlay))]
        [HarmonyPrefix]
        static bool TryPlay_Patch(SubSoundDef __instance) => UnityData.IsInMainThread && EnsureResolved(__instance);

        /// <summary>
        /// 持續性音效：Sustainer 會直接取用各 SubSoundDef 的 resolvedGrains，生成前先解析該 SoundDef 的全部 SubSoundDef。
        /// 不在主執行緒時一律回傳 null（Sustainer 會建立 GameObject），與解除攔截前的舊行為相同。
        /// sustainStartSound 經 PlayOneShot 播放，由 TryPlay_Patch 處理。
        /// </summary>
        [HarmonyPatch(typeof(SoundStarter), nameof(SoundStarter.TrySpawnSustainer))]
        [HarmonyPrefix]
        static bool TrySpawnSustainer_Patch(SoundDef soundDef, ref Sustainer __result)
        {
            if (!UnityData.IsInMainThread)
            {
                __result = null;
                return false;
            }
            if (soundDef?.subSounds == null)
            {
                return true;
            }
            foreach (var subSound in soundDef.subSounds)
            {
                if (!EnsureResolved(subSound))
                {
                    __result = null;
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 在主執行緒當場解析仍在佇列中的 SubSoundDef，回傳它現在是否可以播放。
        /// 尚未排入佇列（例如載入或切換語言重載中，ResolveReferences 之前）或解析後仍沒有 grain 的，比照舊行為略過。
        /// </summary>
        private static bool EnsureResolved(SubSoundDef subSound)
        {
            var delayedActions = FasterGameLoadingMod.delayedActions;
            if (delayedActions != null)
            {
                delayedActions.ResolveSubSoundNow(subSound);
            }
            return ResolvedGrains(subSound).Count > 0;
        }

        // resolvedGrains 為非公開欄位：Publicizer 的存取略過只在遊戲的 Mono 上有效，單元測試的 .NET 會拋 FieldAccessException。
        private static readonly AccessTools.FieldRef<SubSoundDef, System.Collections.Generic.List<ResolvedGrain>> ResolvedGrains =
            AccessTools.FieldRefAccess<SubSoundDef, System.Collections.Generic.List<ResolvedGrain>>(nameof(SubSoundDef.resolvedGrains));

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
            try
            {
                FasterGameLoadingMod.harmony?.UnpatchCategory("SoundStarter");
            }
            catch (Exception ex)
            {
                // Unpatch 失敗不影響遊戲功能，記錄後忽略，避免重複嘗試。
                FGLLog.Warning("Failed to unpatch SoundStarter category:", ex);
            }
        }
    }
}
