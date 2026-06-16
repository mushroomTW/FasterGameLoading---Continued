using HarmonyLib;
using System;
using System.Reflection;

namespace FasterGameLoading
{
    /// <summary>
    /// 修復 FGL 進度條在 Loading Progress mod (ilyvion/loading-progress) 
    /// 接手 mod 載入期間不更新的問題。
    /// 
    /// ── 根本原因 ──
    /// Loading Progress 的 ExecuteToExecuteWhenFinished 處理序中會將
    /// _pauseFasterGameLoading_DelayedActions_LateUpdate 設為 true，
    /// 以暫停我們的 DelayedActions.LateUpdate 避免雙方同時載入 mod。
    /// 但此 flag 同時被其 FasterGameLoadingEarlyModContentLoadingIsFinished
    /// 屬性納入判斷，導致 FGL 進度條在 execute 期間被隱藏。
    /// 
    /// 實際上 Loading Progress 在此階段會持續載入 mod 並寫入我們的
    /// loadedMods 集合，進度條應保持可見才能反映真實載入進度。
    /// 
    /// ── 修復方式 ──
    /// Patch FasterGameLoadingEarlyModContentLoadingIsFinished 的 getter，
    /// 在 pause flag 為 true 時強制回傳 false（尚未完成），讓進度條在
    /// execute 期間保持顯示並隨著 loadedMods 增長更新。
    /// Loading Progress 完成後會自行恢復 pause flag，進度條正常消失。
    /// </summary>
    [HarmonyPatch]
    internal static class FGLProgressReporter
    {
        private static readonly Func<bool> GetIsPaused;

        static FGLProgressReporter()
        {
            // 解析 loading-progress 的 pause flag 以備 Postfix 使用
            var type = AccessTools.TypeByName(
                "ilyvion.LoadingProgress.FasterGameLoading." +
                "FasterGameLoading_DelayedActions_LateUpdate_Patches");
            if (type != null)
            {
                var pauseField = AccessTools.Field(
                    type, "_pauseFasterGameLoading_DelayedActions_LateUpdate");
                if (pauseField != null)
                {
                    try
                    {
                        // 使用 DynamicMethod 動態生成 IL 讀取方法，消除反射 GetValue 開銷
                        var dm = new System.Reflection.Emit.DynamicMethod("GetIsPaused", typeof(bool), null, type, true);
                        var il = dm.GetILGenerator();
                        il.Emit(System.Reflection.Emit.OpCodes.Ldsfld, pauseField);
                        il.Emit(System.Reflection.Emit.OpCodes.Ret);
                        GetIsPaused = (Func<bool>)dm.CreateDelegate(typeof(Func<bool>));
                    }
                    catch (Exception ex)
                    {
                        FGLLog.Warning("Failed to compile fast delegate for FGLProgressReporter pause field:",ex);
                    }
                }
            }
        }

        /// <summary>只在此 patch 依賴的 loading-progress 型別存在時啟用。</summary>
        internal static bool Prepare()
        {
            return AccessTools.TypeByName(
                "ilyvion.LoadingProgress.FasterGameLoading.FasterGameLoadingUtils") != null;
        }

        /// <summary>回傳目標 property getter，patch 不應用於不存在的型別。</summary>
        internal static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName(
                "ilyvion.LoadingProgress.FasterGameLoading.FasterGameLoadingUtils");
            if (type == null) return null;
            return AccessTools.PropertyGetter(
                type, "FasterGameLoadingEarlyModContentLoadingIsFinished");
        }

        /// <summary>
        /// 當 loading-progress 已暫停我們的 LateUpdate 但載入尚未完成時，
        /// 覆蓋回傳值為 false，讓進度條持續顯示並反映 loadedMods 的變化。
        /// </summary>
        internal static void Postfix(ref bool __result)
        {
            if (!__result) return;          // 原本就 false → 不用改
            if (GetIsPaused == null) return; // 找不到 flag → 安全跳過

            // 如果沒有啟用提早載入，或者提早載入已經完成，則不修改 __result，讓進度條可以消失。
            if (!FasterGameLoadingSettings.earlyModContentLoading) return;
            if (FasterGameLoadingMod.delayedActions != null && FasterGameLoadingMod.delayedActions.earlyLoadingComplete)
            {
                return;
            }

            try
            {
                if (GetIsPaused())
                {
                    // Loading Progress 正在接手載入中，FGL 內容尚未完成
                    __result = false;
                }
            }
            catch
            {
                // 任何 reflection 錯誤都安全忽略，使用原始結果
            }
        }
    }
}
