using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld.IO;
using UnityEngine;
using Verse;

namespace FasterGameLoading.InGameTests
{
    // 比較不同 FGL 版本用的啟動成本量測，結果寫進第 1 輪的 timing log。
    [HarmonyPatch(typeof(FilesystemFile), nameof(FilesystemFile.ReadAllBytes))]
    internal static class MainThreadFileReadProbe
    {
        private static long ticks;
        private static int calls;

        public static string Summary => string.Format(CultureInfo.InvariantCulture,
            "mainThreadReadAllBytes={0} calls / {1:F0} ms", calls, ticks * 1000.0 / Stopwatch.Frequency);

        public static int Calls => calls;
        public static double ElapsedMs => ticks * 1000.0 / Stopwatch.Frequency;

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
                }
            }
            return string.Format(CultureInfo.InvariantCulture,
                "extraTypeEnumerationPass={0} assemblies / {1} types / {2} ms", assemblies.Length, types, watch.ElapsedMilliseconds);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 1. XML 載入細部診斷探針 (XmlLoadingCostProbe)
    // ════════════════════════════════════════════════════════════════════════════

    internal class XmlBatchAggregateRecord
    {
        public string PackageId { get; set; }
        public string FolderPath { get; set; }
        public int CallCount;
        public long TotalScanTicks;
        public long TotalBatchWallTicks;
        public long CumulativeWorkerParseTicks;
        public int ExpectedFiles;
        public int AttemptedFiles;
        public int SucceededFiles;
        public int FailedFiles;
        public long TotalBytes;
        public long MinBytes = long.MaxValue;
        public long MaxBytes;
        public int BatchExceptions;
    }

    [HarmonyPatch]
    internal static class XmlLoadingCostProbe
    {
        public static bool Prepare() => StartupExperiments.DiagnosticsEnabled;

        public static readonly List<(long Start, long End)> RealBatchIntervals = new();
        private static readonly object intervalLock = new();

        private static readonly ConcurrentDictionary<string, XmlBatchAggregateRecord> Aggregates = new(StringComparer.OrdinalIgnoreCase);

        // 外層掛點：DirectXmlLoader.XmlAssetsInModFolder
        [HarmonyPatch(typeof(DirectXmlLoader), nameof(DirectXmlLoader.XmlAssetsInModFolder))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void OuterPrefix(ModContentPack mod, string folderPath, out (XmlLoadingBatchTracker tracker, XmlLoadingBatchTracker prevState) __state)
        {
            if (!StartupExperiments.DiagnosticsEnabled || mod == null)
            {
                __state = (null, null);
                return;
            }

            string pkg = mod.PackageIdPlayerFacing ?? mod.PackageId ?? "Unknown";
            var tracker = XmlLoadingBatchTracker.Enter(pkg, folderPath, out var prevState);
            __state = (tracker, prevState);
        }

        [HarmonyPatch(typeof(DirectXmlLoader), nameof(DirectXmlLoader.XmlAssetsInModFolder))]
        [HarmonyFinalizer]
        public static Exception OuterFinalizer((XmlLoadingBatchTracker tracker, XmlLoadingBatchTracker prevState) __state, Exception __exception)
        {
            var (tracker, prevState) = __state;
            if (tracker == null) return __exception;

            XmlLoadingBatchTracker.Exit(tracker, prevState);

            if (tracker.WallEndTimestamp > tracker.WallStartTimestamp)
            {
                lock (intervalLock)
                {
                    RealBatchIntervals.Add((tracker.WallStartTimestamp, tracker.WallEndTimestamp));
                }
            }

            string key = $"{tracker.PackageId}|{tracker.FolderPath}";
            var agg = Aggregates.GetOrAdd(key, _ => new XmlBatchAggregateRecord
            {
                PackageId = tracker.PackageId,
                FolderPath = tracker.FolderPath,
            });

            Interlocked.Increment(ref agg.CallCount);
            Interlocked.Add(ref agg.TotalScanTicks, tracker.ScanTicks);
            Interlocked.Add(ref agg.TotalBatchWallTicks, tracker.WallEndTimestamp - tracker.WallStartTimestamp);
            Interlocked.Add(ref agg.CumulativeWorkerParseTicks, tracker.CumulativeWorkerParseTicks);
            Interlocked.Add(ref agg.ExpectedFiles, tracker.ExpectedFiles);
            Interlocked.Add(ref agg.AttemptedFiles, tracker.AttemptedFiles);
            Interlocked.Add(ref agg.SucceededFiles, tracker.SucceededFiles);
            Interlocked.Add(ref agg.FailedFiles, tracker.FailedFiles);
            Interlocked.Add(ref agg.TotalBytes, tracker.TotalBytes);

            if (tracker.MaxBytes > agg.MaxBytes) agg.MaxBytes = tracker.MaxBytes;
            if (tracker.MinBytes < agg.MinBytes) agg.MinBytes = tracker.MinBytes;

            if (__exception != null)
            {
                Interlocked.Increment(ref agg.BatchExceptions);
            }

            return __exception;
        }

        // 掃描掛點：DirectXmlLoader_XmlAssetsInModFolder_Patch.XmlFilesInVanillaOrder
        [HarmonyPatch(typeof(DirectXmlLoader_XmlAssetsInModFolder_Patch), "XmlFilesInVanillaOrder")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void ScanPrefix(out long __state)
        {
            __state = StartupExperiments.DiagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        }

        [HarmonyPatch(typeof(DirectXmlLoader_XmlAssetsInModFolder_Patch), "XmlFilesInVanillaOrder")]
        [HarmonyFinalizer]
        public static Exception ScanFinalizer(List<FileInfo> __result, long __state, Exception __exception)
        {
            if (__state > 0)
            {
                long scanTicks = Stopwatch.GetTimestamp() - __state;
                var current = XmlLoadingBatchTracker.CurrentContext.Value;
                current?.RecordScan(scanTicks, __result?.Count ?? 0);
            }
            return __exception;
        }

        // 解析建構子掛點：LoadableXmlAsset(FileInfo, ModContentPack)
        [HarmonyPatch(typeof(LoadableXmlAsset), MethodType.Constructor, typeof(FileInfo), typeof(ModContentPack))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void ParsePrefix(out long __state)
        {
            __state = StartupExperiments.DiagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        }

        [HarmonyPatch(typeof(LoadableXmlAsset), MethodType.Constructor, typeof(FileInfo), typeof(ModContentPack))]
        [HarmonyFinalizer]
        public static Exception ParseFinalizer(FileInfo file, long __state, Exception __exception)
        {
            if (__state > 0)
            {
                long parseTicks = Stopwatch.GetTimestamp() - __state;
                var current = XmlLoadingBatchTracker.CurrentContext.Value;
                if (current != null)
                {
                    long len = 0;
                    try
                    {
                        if (file != null && file.Exists) len = file.Length;
                    }
                    catch
                    {
                    }
                    current.RecordParseAttempt(parseTicks, len, __exception == null);
                }
            }
            return __exception;
        }

        public static object GetSummaryData()
        {
            double freq = Stopwatch.Frequency;
            var list = Aggregates.Values.Select(r => new
            {
                packageId = r.PackageId,
                folder = r.FolderPath,
                calls = r.CallCount,
                expectedFiles = r.ExpectedFiles,
                attemptedFiles = r.AttemptedFiles,
                succeededFiles = r.SucceededFiles,
                failedFiles = r.FailedFiles,
                totalBytes = r.TotalBytes,
                minBytes = r.MinBytes is long.MaxValue ? 0 : r.MinBytes,
                maxBytes = r.MaxBytes,
                scanMs = Math.Round(r.TotalScanTicks * 1000.0 / freq, 2),
                batchWallMs = Math.Round(r.TotalBatchWallTicks * 1000.0 / freq, 2),
                workerParseCumulativeMs = Math.Round(r.CumulativeWorkerParseTicks * 1000.0 / freq, 2),
                schedulingOverheadNote = "Unmeasured / estimated: batchWallMs - scanMs vs workerParseCumulativeMs",
                exceptions = r.BatchExceptions,
            }).ToList();

            long totalScan = Aggregates.Values.Sum(static r => r.TotalScanTicks);
            long totalWall = Aggregates.Values.Sum(static r => r.TotalBatchWallTicks);
            long totalParse = Aggregates.Values.Sum(static r => r.CumulativeWorkerParseTicks);
            int totalAttempted = Aggregates.Values.Sum(static r => r.AttemptedFiles);
            int totalExpected = Aggregates.Values.Sum(static r => r.ExpectedFiles);
            long totalBytes = Aggregates.Values.Sum(static r => r.TotalBytes);

            return new
            {
                totalCalls = Aggregates.Values.Sum(static r => r.CallCount),
                totalExpectedFiles = totalExpected,
                totalAttemptedFiles = totalAttempted,
                totalBytes,
                totalScanMs = Math.Round(totalScan * 1000.0 / freq, 2),
                totalBatchWallMs = Math.Round(totalWall * 1000.0 / freq, 2),
                totalWorkerParseMs = Math.Round(totalParse * 1000.0 / freq, 2),
                batches = list,
            };
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 2. 音效預熱與時間交集探針 (CoreSoundPrewarmProbe)
    // ════════════════════════════════════════════════════════════════════════════

    [HarmonyPatch]
    internal static class CoreSoundPrewarmProbe
    {
        public static bool Prepare() => StartupExperiments.DiagnosticsEnabled;

        public static long RunStartTimestamp { get; private set; }
        public static long RunEndTimestamp { get; private set; }

        public static int MainThreadAudioLookupCalls;
        public static long MainThreadAudioLookupDurationTicks;

        [HarmonyPatch(typeof(CoreSoundPrewarmer), "Run")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void RunPrefix()
        {
            if (StartupExperiments.DiagnosticsEnabled)
            {
                RunStartTimestamp = Stopwatch.GetTimestamp();
            }
        }

        [HarmonyPatch(typeof(CoreSoundPrewarmer), "Run")]
        [HarmonyFinalizer]
        public static Exception RunFinalizer(Exception __exception)
        {
            if (StartupExperiments.DiagnosticsEnabled)
            {
                RunEndTimestamp = Stopwatch.GetTimestamp();
            }
            return __exception;
        }

        [HarmonyPatch(typeof(ContentFinder<AudioClip>), nameof(ContentFinder<AudioClip>.Get))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void ContentFinderGetPrefix(out long __state)
        {
            __state = (StartupExperiments.DiagnosticsEnabled && UnityData.IsInMainThread) ? Stopwatch.GetTimestamp() : 0;
        }

        [HarmonyPatch(typeof(ContentFinder<AudioClip>), nameof(ContentFinder<AudioClip>.Get))]
        [HarmonyPostfix]
        public static void ContentFinderGetPostfix(long __state)
        {
            if (__state == 0) return;
            long elapsed = Stopwatch.GetTimestamp() - __state;
            Interlocked.Add(ref MainThreadAudioLookupDurationTicks, elapsed);
            Interlocked.Increment(ref MainThreadAudioLookupCalls);
        }

        public static object GetSummaryData()
        {
            double freq = Stopwatch.Frequency;
            bool started = RunStartTimestamp > 0;
            bool isIncomplete = started && RunEndTimestamp is 0;
            bool completed = started && RunEndTimestamp > 0;
            long effectiveEnd = completed ? RunEndTimestamp : (started ? Stopwatch.GetTimestamp() : 0);
            long runWall = started ? effectiveEnd - RunStartTimestamp : 0;

            List<(long Start, long End)> intervalsCopy;
            lock (XmlLoadingCostProbe.RealBatchIntervals)
            {
                intervalsCopy = new List<(long Start, long End)>(XmlLoadingCostProbe.RealBatchIntervals);
            }

            long overlapTicks = IntervalIntersectionHelper.CalculateOverlap(
                intervalsCopy,
                RunStartTimestamp,
                effectiveEnd,
                isIncomplete,
                out bool isOverlapIncomplete);

            return new
            {
                runStartTimestamp = RunStartTimestamp,
                runEndTimestamp = RunEndTimestamp,
                prewarmCompleted = completed,
                prewarmWallMs = Math.Round(runWall * 1000.0 / freq, 2),
                xmlRealIntervalOverlapMs = Math.Round(overlapTicks * 1000.0 / freq, 2),
                xmlOverlapIncomplete = isOverlapIncomplete,
                mainThreadAudioLookupCalls = MainThreadAudioLookupCalls,
                mainThreadAudioLookupDurationMs = Math.Round(MainThreadAudioLookupDurationTicks * 1000.0 / freq, 2),
                audioLookupNote = "Contains overall lookup and load time on main thread; cache hits are unmeasured.",
                cacheHitMeasured = false,
            };
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 3. 提早載入模組吞吐與退讓探針 (EarlyLoadThroughputProbe)
    // ════════════════════════════════════════════════════════════════════════════

    [HarmonyPatch]
    internal static class EarlyLoadThroughputProbe
    {
        public static bool Prepare() => StartupExperiments.DiagnosticsEnabled;

        public static long EarlyLoadTotalDurationTicks;
        public static int ThrottleEventsCount;
        public static int RemainingPendingEarlyLoadsCount;

        [HarmonyPatch(typeof(EarlyModContentLoader), nameof(EarlyModContentLoader.LoadOneModContent))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void LoadOneModPrefix(out long __state)
        {
            __state = StartupExperiments.DiagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        }

        [HarmonyPatch(typeof(EarlyModContentLoader), nameof(EarlyModContentLoader.LoadOneModContent))]
        [HarmonyFinalizer]
        public static Exception LoadOneModFinalizer(long __state, Exception __exception)
        {
            if (__state > 0)
            {
                long elapsed = Stopwatch.GetTimestamp() - __state;
                Interlocked.Add(ref EarlyLoadTotalDurationTicks, elapsed);
            }
            return __exception;
        }

        [HarmonyPatch(typeof(EarlyModContentLoader), nameof(EarlyModContentLoader.Update))]
        [HarmonyPostfix]
        public static void LoaderUpdatePostfix(EarlyModContentLoader __instance)
        {
            if (!StartupExperiments.DiagnosticsEnabled || __instance == null) return;

            if (__instance.skipFrames is 5)
            {
                ThrottleEventsCount++;
            }

            if (__instance.EarlyLoadingComplete || PlayDataLoader.Loaded)
            {
                RemainingPendingEarlyLoadsCount = __instance.pendingEarlyLoads?.Count ?? 0;
            }
        }

        public static object GetSummaryData()
        {
            double freq = Stopwatch.Frequency;
            return new
            {
                totalLoads = ContentLoadProbe.Loads,
                earlyLoads = ContentLoadProbe.EarlyLoads,
                earlyLoadDurationMs = Math.Round(EarlyLoadTotalDurationTicks * 1000.0 / freq, 2),
                throttleEvents = ThrottleEventsCount,
                remainingPendingMods = RemainingPendingEarlyLoadsCount,
            };
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 4. 延遲管線階段細分探針 (DeferredPipelineProbe)
    // ════════════════════════════════════════════════════════════════════════════

    internal class PhaseTimingStats
    {
        public string Name { get; }
        public long WallStartTimestamp;
        public long WallEndTimestamp;
        public long MoveNextExecutionTicks;
        public int MoveNextCount;
        public long MaxSingleWorkItemTicks;
        public int ItemCount;

        public PhaseTimingStats(string name)
        {
            Name = name;
        }

        public void RecordWorkItem(long ticks)
        {
            Interlocked.Increment(ref ItemCount);
            if (ticks > MaxSingleWorkItemTicks)
            {
                MaxSingleWorkItemTicks = ticks;
            }
        }
    }

    [HarmonyPatch]
    internal static class DeferredPipelineProbe
    {
        public static bool Prepare() => StartupExperiments.DiagnosticsEnabled;

        public static PhaseTimingStats Sounds { get; } = new("Sounds");

        private sealed class TimedEnumerator : IEnumerator, IDisposable
        {
            private readonly IEnumerator inner;
            private readonly PhaseTimingStats stats;
            private bool started;

            public TimedEnumerator(IEnumerator inner, PhaseTimingStats stats)
            {
                this.inner = inner;
                this.stats = stats;
            }

            public object Current => inner?.Current;

            public bool MoveNext()
            {
                if (inner == null) return false;
                if (!started)
                {
                    started = true;
                    stats.WallStartTimestamp = Stopwatch.GetTimestamp();
                }

                long start = Stopwatch.GetTimestamp();
                bool hasNext = false;
                try
                {
                    hasNext = inner.MoveNext();
                }
                finally
                {
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    stats.MoveNextExecutionTicks += elapsed;
                    stats.MoveNextCount++;
                }

                if (!hasNext && stats.WallEndTimestamp is 0)
                {
                    stats.WallEndTimestamp = Stopwatch.GetTimestamp();
                }
                return hasNext;
            }

            public void Reset() => inner?.Reset();

            public void Dispose()
            {
                if (stats.WallEndTimestamp is 0 && started)
                {
                    stats.WallEndTimestamp = Stopwatch.GetTimestamp();
                }
                (inner as IDisposable)?.Dispose();
            }
        }

        [HarmonyPatch(typeof(DelayedActions), nameof(DelayedActions.ResolveSubSoundDefsCoroutine))]
        [HarmonyPostfix]
        public static void SoundsCoroutinePostfix(ref IEnumerator __result)
        {
            if (StartupExperiments.DiagnosticsEnabled && __result != null)
            {
                __result = new TimedEnumerator(__result, Sounds);
            }
        }

        [HarmonyPatch(typeof(DelayedActions), "TryRunPendingSubSound")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void TryRunSubSoundPrefix(out long __state)
        {
            __state = StartupExperiments.DiagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        }

        [HarmonyPatch(typeof(DelayedActions), "TryRunPendingSubSound")]
        [HarmonyFinalizer]
        public static Exception TryRunSubSoundFinalizer(long __state, Exception __exception)
        {
            if (__state > 0)
            {
                long elapsed = Stopwatch.GetTimestamp() - __state;
                Sounds.RecordWorkItem(elapsed);
            }
            return __exception;
        }

        public static object GetSummaryData()
        {
            double freq = Stopwatch.Frequency;
            Func<PhaseTimingStats, object> formatPhase = s =>
            {
                long wall = s.WallEndTimestamp > s.WallStartTimestamp ? s.WallEndTimestamp - s.WallStartTimestamp : 0;
                return new
                {
                    phase = s.Name,
                    wallMs = Math.Round(wall * 1000.0 / freq, 2),
                    moveNextMs = Math.Round(s.MoveNextExecutionTicks * 1000.0 / freq, 2),
                    moveNextCalls = s.MoveNextCount,
                    itemCount = s.ItemCount,
                    singleWorkItemMaxMs = Math.Round(s.MaxSingleWorkItemTicks * 1000.0 / freq, 2),
                };
            };

            return new
            {
                sounds = formatPhase(Sounds),
            };
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 5. 型別查詢形態統計探針 (TypeLookupDiagnostics)
    // ════════════════════════════════════════════════════════════════════════════

    internal struct TypeLookupCaptureState
    {
        public bool IsFullName;
        public bool HasHint;
    }

    [HarmonyPatch]
    internal static class TypeLookupDiagnostics
    {
        public static bool Prepare() => StartupExperiments.DiagnosticsEnabled;

        public static int GenTypesTotalCalls;
        public static int GenTypesFullNameWithHint;
        public static int GenTypesFullNameWithoutHint;
        public static int GenTypesShortNameWithHint;
        public static int GenTypesShortNameWithoutHint;
        public static int GenTypesOriginalSkipped;
        public static int GenTypesResolvedByOriginal;
        public static int GenTypesNotFound;

        public static int AccessToolsTotalCalls;
        public static int AccessToolsFullName;
        public static int AccessToolsShortName;
        public static int AccessToolsOriginalSkipped;
        public static int AccessToolsResolvedByOriginal;
        public static int AccessToolsNotFound;

        [HarmonyPatch(typeof(GenTypes), "GetTypeInAnyAssemblyInt")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void GenTypesPrefix(string typeName, string namespaceIfAmbiguous, out TypeLookupCaptureState __state)
        {
            if (!StartupExperiments.DiagnosticsEnabled)
            {
                __state = default;
                return;
            }

            __state = new TypeLookupCaptureState
            {
                IsFullName = typeName != null && typeName.Contains('.'),
                HasHint = !string.IsNullOrEmpty(namespaceIfAmbiguous),
            };
        }

        [HarmonyPatch(typeof(GenTypes), "GetTypeInAnyAssemblyInt")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        public static void GenTypesPostfix(Type __result, bool __runOriginal, TypeLookupCaptureState __state)
        {
            if (!StartupExperiments.DiagnosticsEnabled) return;

            Interlocked.Increment(ref GenTypesTotalCalls);

            if (__state.IsFullName && __state.HasHint) Interlocked.Increment(ref GenTypesFullNameWithHint);
            else if (__state.IsFullName && !__state.HasHint) Interlocked.Increment(ref GenTypesFullNameWithoutHint);
            else if (!__state.IsFullName && __state.HasHint) Interlocked.Increment(ref GenTypesShortNameWithHint);
            else Interlocked.Increment(ref GenTypesShortNameWithoutHint);

            if (!__runOriginal)
            {
                Interlocked.Increment(ref GenTypesOriginalSkipped);
            }
            else if (__result != null)
            {
                Interlocked.Increment(ref GenTypesResolvedByOriginal);
            }
            else
            {
                Interlocked.Increment(ref GenTypesNotFound);
            }
        }

        [HarmonyPatch(typeof(AccessTools), nameof(AccessTools.TypeByName))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void AccessToolsPrefix(string name, out bool __state)
        {
            __state = StartupExperiments.DiagnosticsEnabled && name != null && name.Contains('.');
        }

        [HarmonyPatch(typeof(AccessTools), nameof(AccessTools.TypeByName))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        public static void AccessToolsPostfix(Type __result, bool __runOriginal, bool __state)
        {
            if (!StartupExperiments.DiagnosticsEnabled) return;

            Interlocked.Increment(ref AccessToolsTotalCalls);

            if (__state) Interlocked.Increment(ref AccessToolsFullName);
            else Interlocked.Increment(ref AccessToolsShortName);

            if (!__runOriginal)
            {
                Interlocked.Increment(ref AccessToolsOriginalSkipped);
            }
            else if (__result != null)
            {
                Interlocked.Increment(ref AccessToolsResolvedByOriginal);
            }
            else
            {
                Interlocked.Increment(ref AccessToolsNotFound);
            }
        }

        public static object GetSummaryData()
        {
            return new
            {
                genTypes = new
                {
                    totalCalls = GenTypesTotalCalls,
                    fullNameWithHint = GenTypesFullNameWithHint,
                    fullNameWithoutHint = GenTypesFullNameWithoutHint,
                    shortNameWithHint = GenTypesShortNameWithHint,
                    shortNameWithoutHint = GenTypesShortNameWithoutHint,
                    originalSkipped = GenTypesOriginalSkipped,
                    resolvedByOriginal = GenTypesResolvedByOriginal,
                    notFound = GenTypesNotFound,
                    note = "originalSkipped indicates original method was bypassed; cannot be attributed solely to FGL cache.",
                },
                accessTools = new
                {
                    totalCalls = AccessToolsTotalCalls,
                    fullName = AccessToolsFullName,
                    shortName = AccessToolsShortName,
                    originalSkipped = AccessToolsOriginalSkipped,
                    resolvedByOriginal = AccessToolsResolvedByOriginal,
                    notFound = AccessToolsNotFound,
                }
            };
        }
    }
}
