using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;

namespace FasterGameLoading;

/// <summary>
/// 在背景預先解碼原版（Core）音效檔。1.7 的 ContentFinder&lt;AudioClip&gt; 經由 UnityEngine.Resources 在主執行緒逐檔同步解碼，
/// 約耗時 4 秒，且引擎的 AssetPrefetcher 不涵蓋這些檔案。這裡改在背景執行緒呼叫同一個內部載入方法，
/// 結果存入 UnityStubs 的共用快取（ConcurrentDictionary + Lazy），主執行緒之後查詢時直接命中。
/// 只在開啟多執行緒預載入時執行；找不到內部方法則完全退回原版行為。
/// </summary>
internal static class CoreSoundPrewarmer
{
    // 與 UnityStubs 的 Resources.AudioClipExtensions 相同（該欄位為 private）。
    private static readonly string[] AudioExtensions = [".mp3", ".ogg", ".aac", ".wav", ".flac", ".m4a"];

    /// <summary>
    /// 由 Mod 建構子呼叫，在背景預先解碼原版音效。
    /// </summary>
    internal static void Start()
    {
        if (!FasterGameLoadingSettings.EnableMultiThreading) return;

        // 不可呼叫公開的 Resources.Load：它會先走 World.LoadResource，未確認可在背景執行緒使用。
        var method = AccessTools.Method(typeof(UnityEngine.Resources), "TryLoadFileBackedResource", [typeof(string), typeof(Type)]);
        if (method == null)
        {
            FGLLog.Message("Core sound prewarm skipped: Resources.TryLoadFileBackedResource not found.");
            return;
        }
        var load = (Func<string, Type, UnityEngine.Object>)method.CreateDelegate(typeof(Func<string, Type, UnityEngine.Object>));

        Task.Factory.StartNew(() => Run(load), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static void Run(Func<string, Type, UnityEngine.Object> load)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var paths = Waylith.Resources.EnumerateResourcePaths("Sounds", AudioExtensions).ToList();
            var failures = 0;
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2 - 1, 1, 4) };
            Parallel.ForEach(paths, options, path =>
            {
                try
                {
                    load(path, typeof(UnityEngine.AudioClip));
                }
                catch (Exception)
                {
                    // 該檔案之後由主執行緒以原版方式載入。
                    Interlocked.Increment(ref failures);
                }
            });

            FGLLog.Message(string.Format(CultureInfo.InvariantCulture,
                "Core sound prewarm: {0} files in {1} ms ({2} failed)", paths.Count, stopwatch.ElapsedMilliseconds, failures));
        }
        catch (Exception ex)
        {
            FGLLog.Warning("Core sound prewarm stopped early:", ex);
        }
    }
}
