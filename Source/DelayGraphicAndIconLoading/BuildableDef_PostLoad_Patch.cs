using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
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
        public static bool Prepare() => FasterGameLoadingSettings.DelayGraphicLoading;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codeInstructions)
        {
            var execute = AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.ExecuteWhenFinished));
            var executeDelayed = AccessTools.Method(typeof(BuildableDef_PostLoad_Patch), nameof(ExecuteDelayed));
            foreach (var code in codeInstructions)
            {
                if (code.Calls(execute))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, executeDelayed);
                }
                else
                {
                    yield return code;
                }
            }
        }

        public static void ExecuteDelayed(Action action, BuildableDef def)
        {
            if (def is ThingDef thingDef && thingDef.ShouldBeLoadedImmediately())
            {
                LongEventHandler.ExecuteWhenFinished(action);
                return;
            }

            FasterGameLoadingMod.delayedActions.EnqueueIcon(def, action);
        }
    }
}
