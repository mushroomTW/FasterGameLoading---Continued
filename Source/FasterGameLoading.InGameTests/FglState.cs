using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimTestRedux;
using Verse;

namespace FasterGameLoading.InGameTests
{
    /// <summary>測試共用的 FGL 狀態查詢與斷言輔助。</summary>
    internal static class FglState
    {
        public const string HarmonyId = "FasterGameLoadingMod";

        /// <summary>
        /// 第 <paramref name="round"/> 次載入的 DelayedActions.PerformActions 主協程是否已整個跑完（含圖集烘焙與音效解析）。
        /// 切換語言重載會再跑一次 CallAll 與整條延遲管線，因此先確認已啟動到第幾輪；
        /// Phase 只反映最近一次啟動的協程（語言切換時重置為 Idle），先前被 StopAllCoroutines 中斷的不影響判斷。
        /// </summary>
        public static bool DeferredPipelineFinished(int round)
            => PerformActionsTracker.Started >= round && FasterGameLoadingMod.delayedActions.Phase == DeferredPhase.Completed;

        /// <summary>以 -quicktest 啟動：跳過主選單直接生成地圖。</summary>
        public static bool Quicktest => GenCommandLine.CommandLineArgPassed("quicktest");

        public static string SettingsProfile =>
            $"delayGraphicLoading={FasterGameLoadingSettings.DelayGraphicLoading}, "
            + $"staticAtlasesBaking={FasterGameLoadingSettings.StaticAtlasesBaking}, "
            + $"earlyModContentLoading={FasterGameLoadingSettings.earlyModContentLoading}, "
            + $"typeLookupCache={FasterGameLoadingSettings.TypeLookupCache}, "
            + $"enableMultiThreading={FasterGameLoadingSettings.EnableMultiThreading}";

        public static bool HasFglPatch(MethodBase method)
        {
            var info = Harmony.GetPatchInfo(method);
            return info != null && info.Owners.Contains(HarmonyId);
        }

        /// <summary>
        /// 集合式斷言：逐一檢查大量 Def 後一次回報。失敗時列出總數與前幾筆，
        /// 避免只看到第一個失敗就停下，也避免訊息長到無法閱讀。
        /// </summary>
        public static void AssertNone(ICollection<string> failures, string what, int maxListed = 15)
        {
            if (failures.Count == 0)
            {
                return;
            }
            var listed = string.Join("; ", failures.Take(maxListed));
            var more = failures.Count > maxListed ? $" (+{failures.Count - maxListed} more)" : string.Empty;
            throw new AssertionException($"{failures.Count} {what}: {listed}{more}");
        }
    }

    /// <summary>計算 PerformActions 主協程被啟動的次數，用來對應第幾輪載入；完成與否看 <see cref="DelayedActions.Phase"/>。</summary>
    [HarmonyPatch(typeof(DelayedActions), nameof(DelayedActions.PerformActions))]
    internal static class PerformActionsTracker
    {
        public static int Started { get; private set; }

        public static void Postfix()
        {
            Started++;
        }
    }
}
