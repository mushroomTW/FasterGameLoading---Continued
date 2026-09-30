using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 BuildableDef.PostLoad 中的 LongEventHandler.ExecuteWhenFinished 呼叫，
    /// 將非必要圖示載入排入延遲佇列。
    /// </summary>
    [HarmonyPatch(typeof(BuildableDef), "PostLoad")]
    public static class BuildableDef_PostLoad_Patch
    {
        public static bool Prepare() => DelayedActions.DeferredVisualsEnabled;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codeInstructions)
            => ThingDef_PostLoad_Patch.SwapExecuteWhenFinished(codeInstructions, AccessTools.Method(typeof(BuildableDef_PostLoad_Patch), nameof(ExecuteDelayed)));

        /// <summary>
        /// 與 ThingDef_PostLoad_Patch 相同：等 ExecuteWhenFinished 回呼執行、Def 參照解析完畢後才決定延遲與否，
        /// 同一個 ThingDef 的圖形與圖示因此會得到一致的判斷。
        /// </summary>
        public static void ExecuteDelayed(Action action, BuildableDef def)
        {
            LongEventHandler.ExecuteWhenFinished(() => ThingDef_PostLoad_Patch.DeferOrRun(def, action, static (delayedActions, d, a) => delayedActions.EnqueueIcon(d, a)));
        }
    }
}
