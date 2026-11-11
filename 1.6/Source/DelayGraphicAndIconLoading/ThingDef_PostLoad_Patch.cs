using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 ThingDef.PostLoad 中的 LongEventHandler.ExecuteWhenFinished 呼叫，
    /// 將非必要圖形的載入延遲到遊戲進入後執行。
    /// </summary>
    [HarmonyPatch(typeof(ThingDef), "PostLoad")]
    public static class ThingDef_PostLoad_Patch
    {
        // 決定是否啟用此補丁（當延遲圖形載入設定開啟時）。
        public static bool Prepare() => DelayedActions.DeferredVisualsEnabled;

        /// <summary>
        /// 轉譯器：攔截並修改 ThingDef.PostLoad 中調用 ExecuteWhenFinished 的 IL 代碼，
        /// 將其導向我們自訂的延遲執行邏輯。
        /// </summary>
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codeInstructions)
            => SwapExecuteWhenFinished(codeInstructions, AccessTools.Method(typeof(ThingDef_PostLoad_Patch), nameof(ExecuteDelayed)));

        /// <summary>
        /// 三份 ExecuteWhenFinished→ExecuteDelayed 轉譯器的共用後端：
        /// 將 ExecuteWhenFinished(action) 替換為 ExecuteDelayed(action, this)。
        /// </summary>
        internal static IEnumerable<CodeInstruction> SwapExecuteWhenFinished(IEnumerable<CodeInstruction> codeInstructions, MethodInfo executeDelayed)
        {
            var execute = AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.ExecuteWhenFinished));
            foreach (var code in codeInstructions)
            {
                if (code.Calls(execute))
                {
                    // 將 ExecuteWhenFinished(action) 替換為 ExecuteDelayed(action, this)
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, executeDelayed);
                }
                else
                {
                    yield return code;
                }
            }
        }

        /// <summary>
        /// 仍在原本的時機以 ExecuteWhenFinished 排程，等回呼執行時才快照圖形輸入，並以
        /// ShouldBeLoadedImmediately 決定立即載入或排入延遲佇列。
        ///
        /// 回呼執行時機正好是快照的正確位置：Def 交互參照（designationCategory、thingCategories、
        /// orderedTakeGroup、graphicData.shaderType）已經解析完成，而其他 Mod 的
        /// StaticConstructorOnStartup 靜態建構子還沒開始改寫圖形。若提前在 PostLoad 擷取，
        /// 這些參照仍是 null，會被誤判成改寫；若晚到延遲佇列才擷取，看到的已經是改寫後的內容。
        /// </summary>
        public static void ExecuteDelayed(Action action, ThingDef def)
        {
            LongEventHandler.ExecuteWhenFinished(() => DeferOrRun(
                def,
                action,
                static (delayedActions, d, a) => delayedActions.EnqueueGraphic(d, a, GraphicSnapshot.Capture(d))));
        }

        /// <summary>
        /// 圖形／圖示延遲的共用後端：Def 參照解析完畢後才決定去向。
        /// 只有 ThingDef 且 ShouldBeLoadedImmediately 才立即執行，其餘（一律含 BuildableDef）排入延遲佇列。
        /// </summary>
        internal static void DeferOrRun<TDef>(TDef def, Action action, Action<DelayedActions, TDef, Action> enqueue)
        {
            var delayedActions = FasterGameLoadingMod.delayedActions;
            if (delayedActions == null || (def is ThingDef thingDef && thingDef.ShouldBeLoadedImmediately()))
            {
                action();
                return;
            }
            enqueue(delayedActions, def, action);
        }
    }
}
