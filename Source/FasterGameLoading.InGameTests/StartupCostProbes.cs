using System;
using System.Diagnostics;
using System.Globalization;
using HarmonyLib;
using RimWorld.IO;
using Verse;

namespace FasterGameLoading.InGameTests
{
    /// <summary>
    /// 比較不同 FGL 版本用的啟動成本量測，結果寫進第 1 輪的 timing log。總啟動時間受背景負載影響很大，
    /// 這裡直接量單一環節：主執行緒等貼圖檔讀取的時間（背景預讀要省掉的部分），以及把所有組件的型別再列舉一遍的時間
    /// （型別列舉只做一次所省下的部分）。只呼叫兩個版本都有的 API，舊版 FGL 也能量。
    /// </summary>
    [HarmonyPatch(typeof(FilesystemFile), nameof(FilesystemFile.ReadAllBytes))]
    internal static class MainThreadFileReadProbe
    {
        private static long ticks;
        private static int calls;

        public static string Summary => string.Format(CultureInfo.InvariantCulture,
            "mainThreadReadAllBytes={0} calls / {1:F0} ms", calls, ticks * 1000.0 / Stopwatch.Frequency);

        // 排在 FGL 的 prefix 之前：FGL 從記憶體取用而略過原方法時也算進來，postfix 仍會執行。
        [HarmonyPriority(Priority.First)]
        public static void Prefix(out long __state)
        {
            __state = UnityData.IsInMainThread ? Stopwatch.GetTimestamp() : 0;
        }

        public static void Postfix(long __state)
        {
            if (__state == 0) return;
            ticks += Stopwatch.GetTimestamp() - __state;
            calls++;
        }
    }

    internal static class TypeEnumerationCost
    {
        /// <summary>直接以 Harmony 列舉所有組件的型別一遍（不經 FGL 的快取），回傳毫秒數。</summary>
        public static string Measure()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var watch = Stopwatch.StartNew();
            int types = 0;
            foreach (var assembly in assemblies)
            {
                try
                {
                    types += AccessTools.GetTypesFromAssembly(assembly).Length;
                }
                catch (Exception)
                {
                    // 個別組件列舉失敗不影響量測
                }
            }
            return string.Format(CultureInfo.InvariantCulture,
                "extraTypeEnumerationPass={0} assemblies / {1} types / {2} ms", assemblies.Length, types, watch.ElapsedMilliseconds);
        }
    }
}
