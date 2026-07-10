using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;
using Verse.Sound;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 SubSoundDef.ResolveReferences 中的 LongEventHandler.ExecuteWhenFinished 呼叫，
    /// 將音效解析延遲到世界初始化完成後執行，減少啟動時的 I/O 壓力。
    /// </summary>
    [HarmonyPatch]
    static class SubSoundDef_ResolvePatch
    {
        /// <summary>
        /// 轉譯器：攔截並修改 ResolveReferences 中調用 ExecuteWhenFinished 的 IL 代碼，
        /// 將其導向我們自訂的延遲執行邏輯。
        /// </summary>
        [HarmonyPatch(typeof(SubSoundDef), "ResolveReferences")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> LateExecute(IEnumerable<CodeInstruction> codeInstructions)
        {
            var execute = AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.ExecuteWhenFinished));
            foreach (var ci in codeInstructions)
            {
                if (ci.Calls(execute))
                {
                    // 將 ExecuteWhenFinished(action) 替換為 ExecuteDelayed(action, this)
                    yield return CodeInstruction.LoadArgument(0);
                    yield return CodeInstruction.Call(typeof(SubSoundDef_ResolvePatch), nameof(ExecuteDelayed));
                    continue;
                }
                yield return ci;
            }
        }

        /// <summary>
        /// 將音效解析動作排入延遲佇列，之後由 DelayedActions 的協程批次處理。
        /// </summary>
        static void ExecuteDelayed(Action action, SubSoundDef def)
        {
            if (action == null)
            {
                return;
            }

            var delayedActions = FasterGameLoadingMod.delayedActions;
            if (delayedActions != null)
            {
                delayedActions.EnqueueSubSound(def, action);
                return;
            }

            LongEventHandler.ExecuteWhenFinished(action);
        }
    }
}
