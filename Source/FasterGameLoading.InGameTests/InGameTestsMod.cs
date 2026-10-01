using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimTestRedux;
using RimTestRedux.Testing;
using Verse;

namespace FasterGameLoading.InGameTests
{
    /// <summary>
    /// 遊戲內測試的進入點。
    /// RimTest Redux 內建的「啟動時執行」在 PlayData 載入完成的那一幀就跑測試，
    /// 但 FGL 的延遲圖形、圖示、圖集與音效協程要之後才逐幀跑完；此時測試會量到半成品狀態。
    /// 因此關掉內建的啟動執行，改由 <see cref="TestRunDriver"/> 等延遲管線跑完後再觸發；
    /// 主選單模式另有語言重載後的第 2 輪，quicktest 模式則等地圖進入遊戲（輪次說明見 TestRunDriver）。
    /// </summary>
    public sealed class InGameTestsMod : Mod
    {
        public InGameTestsMod(ModContentPack content) : base(content)
        {
            ApplySeededFglSettings(content);
            // 只改記憶體中的值、不寫回設定檔；本 mod 排在 RimTest Redux 之後載入，其設定此時已建立。
            RimTestReduxMod.Settings.RunAtStartup = false;
            RimTestReduxMod.Settings.RunOwnTests = false;
            new Harmony(content.PackageId).PatchAll(typeof(InGameTestsMod).Assembly);
        }

        /// <summary>
        /// rimworld-mod-mcp 的 seed_config 會把 Mod_*_FasterGameLoadingMod.xml 改名成受測 mod（本 mod）的資料夾，
        /// FGL 因此讀不到。本 mod 排在 FGL 之前建構，趁 FGL 讀設定前把檔案複製回 FGL 的檔名。
        /// </summary>
        private static void ApplySeededFglSettings(ModContentPack content)
        {
            var seeded = LoadedModManager.GetSettingsFilename(content.FolderName, nameof(FasterGameLoadingMod));
            if (!File.Exists(seeded)) return;

            var fgl = LoadedModManager.RunningMods.FirstOrDefault(static m => string.Equals(m.PackageIdPlayerFacing, "Taranchuk.FasterGameLoading", StringComparison.OrdinalIgnoreCase));
            if (fgl == null)
            {
                Log.Error("[FGL InGameTests] Seeded FGL settings found but Taranchuk.FasterGameLoading is not running; settings not applied.");
                return;
            }
            File.Copy(seeded, LoadedModManager.GetSettingsFilename(fgl.FolderName, nameof(FasterGameLoadingMod)), overwrite: true);
            Log.Message($"[FGL InGameTests] Applied seeded FGL settings from {Path.GetFileName(seeded)}");
        }
    }

    /// <summary>
    /// 測試輪次：第 1 輪在初次載入完成後執行；主選單模式下接著切換語言觸發完整重載（見 <see cref="LanguageReload"/>），
    /// 重載完成後以同一批測試再跑第 2 輪。quicktest 模式改為等地圖進入遊戲後只跑一輪（遊戲中無法切換語言）。
    /// </summary>
    [HarmonyPatch(typeof(Root), nameof(Root.Update))]
    internal static class TestRunDriver
    {
        /// <summary>等待延遲管線的上限；逾時仍會執行測試，讓未完成的狀態以測試失敗呈現。</summary>
        private const double TimeoutSeconds = 300;

        /// <summary>目前等待中（或正在執行）的輪次，從 1 開始。</summary>
        public static int Round { get; private set; } = 1;

        private static bool finished;
        private static Stopwatch waitingSince;

        public static void Postfix()
        {
            if (finished || !ReadyForRound())
            {
                return;
            }
            waitingSince ??= Stopwatch.StartNew();

            bool timedOut = waitingSince.Elapsed.TotalSeconds >= TimeoutSeconds;
            if (!timedOut && !FglState.DeferredPipelineFinished(Round))
            {
                return;
            }

            if (timedOut)
            {
                Log.Error($"[FGL InGameTests] Round {Round}: timed out after {TimeoutSeconds}s waiting for FGL's deferred pipeline; running tests anyway.");
            }
            Log.Message($"[FGL InGameTests] Round {Round}: running suites after {waitingSince.Elapsed.TotalSeconds:F1}s wait. Language: {LanguageDatabase.activeLanguage?.folderName}, quicktest: {FglState.Quicktest}. Settings: {FglState.SettingsProfile}");
            // 比較不同版本的啟動速度用：自遊戲啟動起算的秒數，減掉上面的等待即為 PlayData 載入完成的時間點。
            Log.Message($"[FGL InGameTests] Round {Round} timing: realtimeSinceStartup={UnityEngine.Time.realtimeSinceStartup:F1}s, gcCollections={GC.CollectionCount(0)}, {MainThreadFileReadProbe.Summary}, {TypeEnumerationCost.Measure()}");
            Runner.RunAllRegisteredTests();
            StatusExplorer.UpdateAllStatusCounts();
            Viewer.LogTestsResults();
            waitingSince = null;

            if (Round == 1 && LanguageReload.TryStart())
            {
                Round = 2;
                return;
            }
            finished = true;
            Log.Message("[FGL InGameTests] All rounds finished.");
        }

        private static bool ReadyForRound()
        {
            if (!PlayDataLoader.Loaded || LongEventHandler.AnyEventNowOrWaiting || Find.UIRoot == null)
            {
                return false;
            }
            // quicktest 直接生成地圖：等地圖進入遊戲，World.FinalizeInit 那條音效路徑才會走過。
            return !FglState.Quicktest || (Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null);
        }
    }
}
