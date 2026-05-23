using HarmonyLib;
using RimWorld.Planet;
using System;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 在世界完成初始化後，將延遲的聲音解析任務放到主執行緒執行。
    /// 完成後取消 SoundStarter 的攔截 patch，恢復正常聲音播放。
    /// </summary>
    [HarmonyPatch(typeof(World), "FinalizeInit")]
    public class World_FinalizeInit_Patch
    {
        public static void Postfix()
        {
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                while (FasterGameLoadingMod.delayedActions.subSoundDefToResolve.Count > 0)
                {
                    var (def, action) = FasterGameLoadingMod.delayedActions.subSoundDefToResolve.Dequeue();
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        FasterGameLoadingMod.delayedActions.Error("[FasterGameLoading] Error resolving AudioGrain for " + def, ex);
                    }
                }
                // 所有 SubSoundDef 解析完畢後才取消攔截，避免中途播放聲音
                SoundStarter_Patch.Unpatch();
            });
        }
    }
}

