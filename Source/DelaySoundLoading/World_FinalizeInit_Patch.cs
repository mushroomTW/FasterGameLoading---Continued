using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 在世界完成初始化後，將延遲的聲音解析任務放到主執行緒執行。
    /// 完成後取消 SoundStarter 的攔截 patch，恢復正常聲音播放。
    /// </summary>
    [HarmonyPatch(typeof(World), "FinalizeInit")]
    public static class World_FinalizeInit_Patch
    {
        /// <summary>
        /// 於世界初始化完成後，觸發所有累積的聲音解析任務，並在完成後取消 SoundStarter 補丁的攔截。
        /// </summary>
        public static void Postfix()
        {
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                var delayedActions = FasterGameLoadingMod.delayedActions;
                if (delayedActions != null)
                {
                    // 所有 SubSoundDef 解析完畢後才取消攔截，避免中途播放聲音
                    delayedActions.ResolvePendingSubSounds();
                }
                else
                {
                    SoundStarter_Patch.Unpatch();
                }
            });
        }
    }
}

