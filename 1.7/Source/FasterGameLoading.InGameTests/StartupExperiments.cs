using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevTools;
using DevTools.Testing;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FasterGameLoading.InGameTests
{
    internal enum StartupExperimentMode
    {
        Baseline,
        XmlSequential,
        NoSoundPrewarm,
    }

    internal static class StartupExperiments
    {
        // 記錄開發測試模組初始化時的 monotonic timestamp（非完整作業系統程序啟動時間）
        public static readonly long ObservationStartTimestamp = Stopwatch.GetTimestamp();

        public static StartupExperimentMode CurrentMode { get; private set; } = StartupExperimentMode.Baseline;
        public static string CurrentModeSlug => CurrentMode switch
        {
            StartupExperimentMode.Baseline => "baseline",
            StartupExperimentMode.XmlSequential => "xml-sequential",
            StartupExperimentMode.NoSoundPrewarm => "no-sound-prewarm",
            _ => "unknown"
        };

        public static bool DiagnosticsEnabled { get; private set; }
        public static bool StartupOnly { get; private set; }
        public static bool AutoLanguageReload { get; private set; }

        private static bool initialized;

        static StartupExperiments()
        {
            Initialize();
        }

        public static void Initialize()
        {
            if (initialized) return;
            initialized = true;

            DiagnosticsEnabled = GenCommandLine.CommandLineArgPassed("fglStartupDiagnostics");
            StartupOnly = GenCommandLine.CommandLineArgPassed("fglStartupOnly");
            AutoLanguageReload = GenCommandLine.CommandLineArgPassed("fglAutoLanguageReload");

            if (GenCommandLine.TryGetCommandLineArg("fglStartupExperiment", out var expVal))
            {
                if (string.Equals(expVal, "baseline", StringComparison.OrdinalIgnoreCase))
                {
                    CurrentMode = StartupExperimentMode.Baseline;
                }
                else if (string.Equals(expVal, "xml-sequential", StringComparison.OrdinalIgnoreCase))
                {
                    CurrentMode = StartupExperimentMode.XmlSequential;
                }
                else if (string.Equals(expVal, "no-sound-prewarm", StringComparison.OrdinalIgnoreCase))
                {
                    CurrentMode = StartupExperimentMode.NoSoundPrewarm;
                }
                else
                {
                    Log.Error($"[FGL StartupExperiments] Unknown experiment mode: '{expVal}'. Expected 'baseline', 'xml-sequential', or 'no-sound-prewarm'. Aborting.");
                    Application.Quit(1);
                    return;
                }
            }
            else
            {
                CurrentMode = StartupExperimentMode.Baseline;
            }

            // 檢查旗標衝突
            if (StartupOnly && AutoLanguageReload)
            {
                Log.Error("[FGL StartupExperiments] Conflicting flags: -fglStartupOnly and -fglAutoLanguageReload cannot be used together. Aborting.");
                Application.Quit(1);
                return;
            }

            // -fglAutoLanguageReload 必須在隔離 session (-savedatafolder) 下執行
            if (AutoLanguageReload)
            {
                bool hasSaveDataFolder = GenCommandLine.CommandLineArgPassed("savedatafolder")
                    || Environment.GetCommandLineArgs().Any(static a => a.StartsWith("-savedatafolder", StringComparison.OrdinalIgnoreCase));

                if (!hasSaveDataFolder)
                {
                    Log.Error("[FGL StartupExperiments] -fglAutoLanguageReload requires an isolated session via -savedatafolder. Refusing to run in non-isolated environment. Aborting.");
                    Application.Quit(1);
                    return;
                }

                // 檢查 CLI filter 參數（不支援重播）
                string[] args = Environment.GetCommandLineArgs();
                bool hasFilterArgs = args.Any(static a =>
                    string.Equals(a, "--where", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-w", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a, "--test-list", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a, "--test-plan", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-p", StringComparison.OrdinalIgnoreCase));

                if (hasFilterArgs)
                {
                    Log.Error("[FGL StartupExperiments] -fglAutoLanguageReload does not support CLI filters (--where, --test-list, --test-plan); run full suite (--test) or without filter. Aborting.");
                    Application.Quit(1);
                    return;
                }

                if (Prefs.data != null)
                {
                    AutoLanguageReloadDriver.OriginalLanguage = Prefs.LangFolderName;
                }
            }

            FGLLog.Message($"[StartupExperiments] Initialized. Mode={CurrentModeSlug}, Diagnostics={DiagnosticsEnabled}, StartupOnly={StartupOnly}, AutoLanguageReload={AutoLanguageReload}, Freq={Stopwatch.Frequency}");
        }
    }

    /// <summary>
    /// xml-sequential 實驗補丁：
    /// 改掛 FGL 的 DirectXmlLoader_XmlAssetsInModFolder_Patch.Prefix 本身，
    /// 序列實作維持與正式版相同 array + Where(..).ToArray()，僅將 Parallel.For 替換為 for 迴圈。
    /// 沿用全部 guard 與 fallback，避免 fallback 時再度進入 FGL 平行流程。
    /// </summary>
    [HarmonyPatch(typeof(DirectXmlLoader_XmlAssetsInModFolder_Patch), nameof(DirectXmlLoader_XmlAssetsInModFolder_Patch.Prefix))]
    internal static class XmlSequentialPatch
    {
        public static bool Prepare() => StartupExperiments.CurrentMode == StartupExperimentMode.XmlSequential;

        [HarmonyPrefix]
        public static bool Prefix(
            ref bool __result,
            ref LoadableXmlAsset[] __0,
            ModContentPack mod,
            string folderPath,
            List<string> foldersToLoadDebug)
        {
            if (StartupExperiments.CurrentMode != StartupExperimentMode.XmlSequential)
            {
                return true;
            }

            // 與 FGL 相同的前置檢查；不符時直接讓原版執行 (__result = true，略過 FGL Prefix)
            if (mod == null || !FasterGameLoadingSettings.EnableMultiThreading || ProtectedMods.ShouldSkipEarlyLoad(mod))
            {
                __result = true;
                return false;
            }

            if (HyperdriveCompat.ParallelizesModDefs && string.Equals(folderPath, DirectXmlLoader_XmlAssetsInModFolder_Patch.DefsFolderPath, StringComparison.Ordinal))
            {
                __result = true;
                return false;
            }

            try
            {
                var files = DirectXmlLoader_XmlAssetsInModFolder_Patch.XmlFilesInVanillaOrder(mod, folderPath, foldersToLoadDebug);
                if (files.Count is 0)
                {
                    __0 = [];
                    __result = false;
                    return false;
                }

                var assets = new LoadableXmlAsset[files.Count];
                for (int i = 0; i < files.Count; i++)
                {
                    try
                    {
                        assets[i] = new LoadableXmlAsset(files[i], mod);
                    }
                    catch (Exception ex)
                    {
                        FGLLog.Error($"Failed to load XML asset in sequential mode for Mod {mod.Name} (File: {files[i]?.FullName}):", ex);
                    }
                }

                __0 = assets.Where(static a => a != null).ToArray();
                __result = false;
                return false;
            }
            catch (Exception ex)
            {
                FGLLog.Warning($"Sequential XML loading failed for Mod {mod?.Name ?? "Unknown"}, falling back to vanilla loader: {ex.Message}");
                __result = true;
                return false;
            }
        }
    }

    /// <summary>
    /// no-sound-prewarm 實驗補丁：
    /// 僅以 Harmony 跳過 CoreSoundPrewarmer.Start，不關閉 EnableMultiThreading。
    /// </summary>
    [HarmonyPatch(typeof(CoreSoundPrewarmer), nameof(CoreSoundPrewarmer.Start))]
    internal static class NoSoundPrewarmPatch
    {
        public static bool Prepare() => StartupExperiments.CurrentMode == StartupExperimentMode.NoSoundPrewarm;

        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (StartupExperiments.CurrentMode == StartupExperimentMode.NoSoundPrewarm)
            {
                FGLLog.Message("Core sound prewarm skipped by startup experiment (no-sound-prewarm).");
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// -fglStartupOnly 模式驅動器：
    /// 略過 DevTools 測試與舊 TypeEnumerationCost.Measure 人工額外掃描。
    /// 最小幀觀察器於純效能與診斷回合皆執行；精確就緒時間以 StartupTimeline Hook 捕捉。
    /// </summary>
    [HarmonyPatch]
    internal static class StartupOnlyDriver
    {
        public static bool Prepare() => StartupExperiments.StartupOnly;

        private static bool mainMenuFrameStarted;
        private static bool mainMenuCompleted;
        private static int mainMenuCompletedFrame = -1;
        private static long mainMenuTimestamp;


        public static bool AllDoneRecorded { get; private set; }
        public static int AllDoneFrame { get; private set; } = -1;
        public static long AllDoneTimestamp { get; private set; }

        private static bool jsonEmitted;

        // 主選單幀率統計（以單調時鐘 Stopwatch 測量 Update 呼叫間隔）
        private static readonly List<double> MainMenuFrameTimesMs = [];
        private static long lastUpdateTimestamp;
        private static int lastSampledFrame = -1;
        private static int frameSampleStartFrame = -1;
        private static int frameSampleEndFrame = -1;

        // Hook: StartupTimeline.MarkAllReady 高精度捕捉
        [HarmonyPatch(typeof(StartupTimeline), nameof(StartupTimeline.MarkAllReady))]
        [HarmonyPostfix]
        public static void MarkAllReadyPostfix()
        {
            if (!StartupExperiments.StartupOnly || AllDoneRecorded) return;
            AllDoneRecorded = true;
            AllDoneFrame = Time.frameCount;
            AllDoneTimestamp = Stopwatch.GetTimestamp();
        }

        [HarmonyPatch(typeof(Root), nameof(Root.Update))]
        [HarmonyPostfix]
        public static void RootUpdatePostfix()
        {
            if (!StartupExperiments.StartupOnly || jsonEmitted)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            int currentFrame = Time.frameCount;

            // 首個主選單完成幀確認
            bool mainMenuShown = StartupTimeline.MainMenuShown;
            if (mainMenuShown && mainMenuFrameStarted && !mainMenuCompleted)
            {
                mainMenuCompleted = true;
                mainMenuCompletedFrame = currentFrame;
                mainMenuTimestamp = now;
                lastUpdateTimestamp = now;
                frameSampleStartFrame = currentFrame;
            }
            mainMenuFrameStarted = mainMenuShown;

            // 主選單幀耗時採樣（避免同幀重複採樣）
            if (mainMenuCompleted && currentFrame != lastSampledFrame)
            {
                if (lastUpdateTimestamp > 0)
                {
                    double frameMs = (now - lastUpdateTimestamp) * 1000.0 / Stopwatch.Frequency;
                    MainMenuFrameTimesMs.Add(frameMs);
                }
                lastUpdateTimestamp = now;
                lastSampledFrame = currentFrame;
            }

            // 完成條件：延遲管線全部完成，且主選單後至少經過 1 秒與 2 幀讓 LoadingProgress 寫入
            bool timeElapsedSufficient = mainMenuCompleted && (now - mainMenuTimestamp >= Stopwatch.Frequency);
            bool framesSufficient = mainMenuCompleted && (currentFrame - mainMenuCompletedFrame >= 2);

            if (mainMenuCompleted && AllDoneRecorded && timeElapsedSufficient && framesSufficient)
            {
                jsonEmitted = true;
                frameSampleEndFrame = currentFrame;
                EmitResultAndExit();
            }
        }

        private static void EmitResultAndExit()
        {
            double freq = Stopwatch.Frequency;
            long startTs = StartupExperiments.ObservationStartTimestamp;

            double mainMenuMs = mainMenuTimestamp > 0 ? (mainMenuTimestamp - startTs) * 1000.0 / freq : 0;
            double allDoneMs = AllDoneTimestamp > 0 ? (AllDoneTimestamp - startTs) * 1000.0 / freq : mainMenuMs;

            bool allDoneClamped = false;
            double mainMenuToAllDoneSec = 0;
            if (AllDoneTimestamp > 0 && mainMenuTimestamp > 0)
            {
                if (AllDoneTimestamp < mainMenuTimestamp)
                {
                    allDoneClamped = true;
                    mainMenuToAllDoneSec = 0;
                }
                else
                {
                    mainMenuToAllDoneSec = (AllDoneTimestamp - mainMenuTimestamp) / freq;
                }
            }

            // 計算主選單幀統計
            double frameAvg = 0, frameP95 = 0, frameMax = 0;
            if (MainMenuFrameTimesMs.Count > 0)
            {
                var sorted = MainMenuFrameTimesMs.OrderBy(static x => x).ToList();
                frameMax = sorted[^1];
                frameP95 = sorted[(int)Math.Round(0.95 * (sorted.Count - 1))];
                frameAvg = sorted.Average();
            }

            var frameSummary = new
            {
                samples = MainMenuFrameTimesMs.Count,
                startFrame = frameSampleStartFrame,
                endFrame = frameSampleEndFrame,
                avgMs = Math.Round(frameAvg, 2),
                p95Ms = Math.Round(frameP95, 2),
                maxMs = Math.Round(frameMax, 2),
                measurementNote = "Interval between Root.Update calls using Stopwatch monotonic clock; covers full frame duration, not purely Update CPU time.",
            };

            var report = new StartupExperimentJsonReport
            {
                ProcessId = Environment.ProcessId,
                Experiment = StartupExperiments.CurrentModeSlug,
                Diagnostics = StartupExperiments.DiagnosticsEnabled,
                ObservationStartTimestamp = startTs,
                StopwatchFrequency = (long)freq,
                FirstMainMenuFrame = mainMenuCompletedFrame,
                FirstMainMenuMs = Math.Round(mainMenuMs, 2),
                AllDeferredWorkDoneFrame = AllDoneFrame,
                AllDeferredWorkDoneMs = Math.Round(allDoneMs, 2),
                AllDoneClampedToZero = allDoneClamped,
                MainMenuToAllDoneSec = Math.Round(mainMenuToAllDoneSec, 3),
                MainMenuFrameSummary = frameSummary,
                MainThreadFileRead = MainThreadFileReadProbe.Summary,
            };

            if (StartupExperiments.DiagnosticsEnabled)
            {
                report.XmlDiagnostics = XmlLoadingCostProbe.GetSummaryData();
                report.SoundPrewarmDiagnostics = CoreSoundPrewarmProbe.GetSummaryData();
                report.EarlyLoadDiagnostics = EarlyLoadThroughputProbe.GetSummaryData();
                report.DeferredPipelineDiagnostics = DeferredPipelineProbe.GetSummaryData();
                report.TypeLookupDiagnostics = TypeLookupDiagnostics.GetSummaryData();
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            string json = JsonSerializer.Serialize(report, options);

            Log.Message($"[FGL StartupExperiment] {json}");
            Log.Message("[FGL StartupExperiment] Exiting application normally.");
            Application.Quit(0);
        }
    }

    /// <summary>
    /// -fglAutoLanguageReload 模式驅動器：
    /// 驗證第 1 輪測試完成後，切換至 Japanese 重載 PlayData 並自動執行第 2 輪測試。
    /// 截斷提早退出，待 Runner 完全退出後於主選單安全切換語言，最後還原玩家原始設定。
    /// </summary>
    [HarmonyPatch]
    internal static class AutoLanguageReloadDriver
    {
        public static bool Prepare() => StartupExperiments.AutoLanguageReload;

        public static string OriginalLanguage { get; set; }
        public static int Round { get; private set; } = 1;

        private static bool pendingLanguageSwitch;
        private static bool round2Triggered;
        private static bool pendingFinalExit;
        private static int exitCode;

        private static TestStatusEvaluator.EvaluationResult round1Result;
        private static TestStatusEvaluator.EvaluationResult round2Result;

        public static void OnRoundReady(int round)
        {
            if (!StartupExperiments.AutoLanguageReload) return;

            // 補捉原始語言（若建構子初始化時 Prefs 尚未就緒）
            if (OriginalLanguage == null && Prefs.data != null)
            {
                OriginalLanguage = Prefs.LangFolderName;
            }

            if (round is 1 && DevHarmony.Args == null)
            {
                var testMod = LoadedModManager.RunningMods.FirstOrDefault(static m => string.Equals(m.PackageIdPlayerFacing, "Taranchuk.FasterGameLoading.InGameTests", StringComparison.OrdinalIgnoreCase));
                var fixtureManager = DevHarmony.GetDevTool<TestFixtureManager>(testMod);
                fixtureManager?.RunAll();
            }
        }

        // 僅攔截 FGL 開發測試附屬模組自己的 TestFixtureManager
        [HarmonyPatch(typeof(TestFixtureManager), "DevTools.Testing.ITestManager.OnTestRunnerEnd")]
        [HarmonyPrefix]
        public static bool TestFixtureManagerOnEndPrefix(TestFixtureManager __instance)
        {
            if (!StartupExperiments.AutoLanguageReload) return true;

            var testMod = LoadedModManager.RunningMods.FirstOrDefault(static m => string.Equals(m.PackageIdPlayerFacing, "Taranchuk.FasterGameLoading.InGameTests", StringComparison.OrdinalIgnoreCase));
            if (__instance == null || !ReferenceEquals(__instance.mod, testMod))
            {
                return true;
            }

            var statuses = new List<TestStatusEvaluator.SimplifiedStatus>();
            bool anyFixtureFailed = false;

            if (__instance.TestFixtures != null)
            {
                foreach (var fixture in __instance.TestFixtures)
                {
                    if (fixture.Status is Status.Failed or Status.Canceled)
                    {
                        anyFixtureFailed = true;
                    }
                    if (fixture.TestFunctions == null) continue;
                    foreach (var func in fixture.TestFunctions)
                    {
                        statuses.Add(func.Status switch
                        {
                            Status.Passed => TestStatusEvaluator.SimplifiedStatus.Passed,
                            Status.Failed => TestStatusEvaluator.SimplifiedStatus.Failed,
                            Status.Skipped => TestStatusEvaluator.SimplifiedStatus.Skipped,
                            Status.Canceled => TestStatusEvaluator.SimplifiedStatus.Canceled,
                            Status.Pending => TestStatusEvaluator.SimplifiedStatus.Pending,
                            _ => TestStatusEvaluator.SimplifiedStatus.NotRun,
                        });
                    }
                }
            }

            var eval = TestStatusEvaluator.Evaluate(statuses, anyFixtureFailed);

            if (Round is 1)
            {
                round1Result = eval;
                Log.Message($"[FGL InGameTests] Round 1 tests finished. Total: {eval.Total}, Passed: {eval.Passed}, Failed: {eval.Failed}, Skipped: {eval.Skipped}, Canceled: {eval.Canceled}, Pending: {eval.Pending}. Result: {(eval.IsSuccess ? "Passed" : "Failed")}");

                if (!eval.IsSuccess)
                {
                    Log.Error("[FGL InGameTests] Round 1 tests did not pass strictly. Aborting language reload.");
                    RestoreOriginalLanguage();
                    Application.Quit(1);
                    return false;
                }

                // 排程於 Root.Update 切換語言，不在未 Dispose 的 TestRunner 呼叫堆疊中直接切換
                pendingLanguageSwitch = true;
                return false;
            }
            else
            {
                round2Result = eval;
                Log.Message($"[FGL InGameTests] Round 2 tests finished. Total: {eval.Total}, Passed: {eval.Passed}, Failed: {eval.Failed}, Skipped: {eval.Skipped}, Canceled: {eval.Canceled}, Pending: {eval.Pending}. Result: {(eval.IsSuccess ? "Passed" : "Failed")}");

                bool suiteMatched = round1Result != null && round2Result.Total == round1Result.Total;
                bool overallSuccess = eval.IsSuccess && suiteMatched;
                exitCode = overallSuccess ? 0 : 1;

                if (!suiteMatched)
                {
                    Log.Error($"[FGL InGameTests] Test suite mismatch between rounds: Round 1 had {round1Result?.Total} tests, Round 2 had {round2Result.Total} tests.");
                }

                Log.Message($"[FGL InGameTests] AutoLanguageReload complete: Round 1 {(round1Result?.IsSuccess == true ? "Passed" : "Failed")}, Round 2 {(eval.IsSuccess ? "Passed" : "Failed")}. Overall: {(overallSuccess ? "Passed" : "Failed")}.");

                pendingFinalExit = true;
                return false;
            }
        }

        [HarmonyPatch(typeof(Root), nameof(Root.Update))]
        [HarmonyPostfix]
        public static void RootUpdatePostfix()
        {
            if (!StartupExperiments.AutoLanguageReload) return;

            // 1. 執行排程的語言切換（等舊 Runner 完全退出、主選單且無長事件）
            if (pendingLanguageSwitch && !TestRunner.Active && Current.ProgramState is ProgramState.Entry && !LongEventHandler.AnyEventNowOrWaiting)
            {
                pendingLanguageSwitch = false;
                Round = 2;

                var japanese = LanguageDatabase.AllLoadedLanguages.FirstOrDefault(static l => l.folderName.StartsWith("Japanese", StringComparison.Ordinal));
                if (japanese == null)
                {
                    Log.Error("[FGL InGameTests] AutoLanguageReload: Japanese language folder not found in LanguageDatabase!");
                    RestoreOriginalLanguage();
                    Application.Quit(1);
                    return;
                }

                Log.Message($"[FGL InGameTests] AutoLanguageReload: Switching language to {japanese.folderName} for Round 2.");
                LanguageDatabase.SelectLanguage(japanese);
                return;
            }

            // 2. 啟動第 2 輪測試
            if (Round is 2 && TestRunDriver.Ready && !round2Triggered && !TestRunner.Active && !LongEventHandler.AnyEventNowOrWaiting)
            {
                round2Triggered = true;
                Log.Message("[FGL InGameTests] AutoLanguageReload: Starting Round 2 DevTools tests.");

                var testMod = LoadedModManager.RunningMods.FirstOrDefault(static m => string.Equals(m.PackageIdPlayerFacing, "Taranchuk.FasterGameLoading.InGameTests", StringComparison.OrdinalIgnoreCase));
                var fixtureManager = DevHarmony.GetDevTool<TestFixtureManager>(testMod);
                fixtureManager?.RunAll();
                return;
            }

            // 3. 兩輪結束後還原語言並正常退出
            if (pendingFinalExit && !TestRunner.Active)
            {
                pendingFinalExit = false;
                RestoreOriginalLanguage();
                Log.Message($"[FGL InGameTests] Application exiting with code {exitCode}.");
                Application.Quit(exitCode);
            }
        }

        private static void RestoreOriginalLanguage()
        {
            if (OriginalLanguage != null && Prefs.data != null)
            {
                Prefs.LangFolderName = OriginalLanguage;
                Prefs.Save();
                Log.Message($"[FGL InGameTests] Restored original language setting in isolated session ({OriginalLanguage}).");
            }
        }
    }

    internal class StartupExperimentJsonReport
    {
        [JsonPropertyName("processId")]
        public int ProcessId { get; set; }

        [JsonPropertyName("experiment")]
        public string Experiment { get; set; }

        [JsonPropertyName("diagnostics")]
        public bool Diagnostics { get; set; }

        [JsonPropertyName("observationStartTimestamp")]
        public long ObservationStartTimestamp { get; set; }

        [JsonPropertyName("stopwatchFrequency")]
        public long StopwatchFrequency { get; set; }

        [JsonPropertyName("mainMenuFrame")]
        public int FirstMainMenuFrame { get; set; }

        [JsonPropertyName("mainMenuMs")]
        public double FirstMainMenuMs { get; set; }

        [JsonPropertyName("allDeferredWorkDoneFrame")]
        public int AllDeferredWorkDoneFrame { get; set; }

        [JsonPropertyName("allDeferredWorkDoneMs")]
        public double AllDeferredWorkDoneMs { get; set; }

        [JsonPropertyName("allDoneClampedToZero")]
        public bool AllDoneClampedToZero { get; set; }

        [JsonPropertyName("mainMenuToAllDoneSec")]
        public double MainMenuToAllDoneSec { get; set; }

        [JsonPropertyName("mainMenuFrameSummary")]
        public object MainMenuFrameSummary { get; set; }

        [JsonPropertyName("fileReadProbe")]
        public string MainThreadFileRead { get; set; }

        [JsonPropertyName("xmlDiagnostics")]
        public object XmlDiagnostics { get; set; }

        [JsonPropertyName("soundPrewarmDiagnostics")]
        public object SoundPrewarmDiagnostics { get; set; }

        [JsonPropertyName("earlyLoadDiagnostics")]
        public object EarlyLoadDiagnostics { get; set; }

        [JsonPropertyName("deferredPipelineDiagnostics")]
        public object DeferredPipelineDiagnostics { get; set; }

        [JsonPropertyName("typeLookupDiagnostics")]
        public object TypeLookupDiagnostics { get; set; }
    }
}
