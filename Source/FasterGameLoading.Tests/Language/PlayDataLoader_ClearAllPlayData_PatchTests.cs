using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using NUnit.Framework;
using Verse;

namespace FasterGameLoading.Tests.Language
{
    [TestFixture]
    public class PlayDataLoader_ClearAllPlayData_PatchTests
    {
        private const string HarmonyId = "FasterGameLoading.Tests.PlayDataLoader_ClearAllPlayData_PatchTests";
        private static Harmony harmony;
        private static int mainThreadId;
        private static bool countResets;
        private static int resetCount;
        private static int lastResetThreadId;

        private static bool MockIsInMainThread(ref bool __result)
        {
            __result = Thread.CurrentThread.ManagedThreadId == mainThreadId;
            return false;
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            harmony = new Harmony(HarmonyId);
            // 其他測試類別也會以回傳固定值的前置補丁取代 IsInMainThread；排在最前面才能依執行緒判斷。
            harmony.Patch(AccessTools.PropertyGetter(typeof(UnityData), nameof(UnityData.IsInMainThread)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(PlayDataLoader_ClearAllPlayData_PatchTests), nameof(MockIsInMainThread)), Priority.First));
            SessionLifecycle.On(LifecyclePhase.LanguageReloading, static () =>
            {
                if (!countResets) return;
                Interlocked.Increment(ref resetCount);
                lastResetThreadId = Thread.CurrentThread.ManagedThreadId;
            });
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            harmony?.UnpatchAll(HarmonyId);
        }

        [SetUp]
        public void SetUp()
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            AccessTools.Field(typeof(PlayDataLoader_ClearAllPlayData_Patch), "resetBySelectLanguage").SetValue(null, false);
            SessionLifecycle.DrainMainThreadRaises();
            resetCount = 0;
            lastResetThreadId = 0;
            countResets = true;
        }

        [TearDown]
        public void TearDown()
        {
            countResets = false;
            SessionLifecycle.MainThreadRaiseTimeoutMs = 10_000;
        }

        [Test]
        public void Prefix_AfterSelectLanguage_DoesNotResetAgain()
        {
            LanguageDatabase_SelectLanguage_Patch.Prefix();
            Assert.That(resetCount, Is.EqualTo(1));

            PlayDataLoader_ClearAllPlayData_Patch.Prefix();
            Assert.That(resetCount, Is.EqualTo(1), "語言切換已重置過，同一輪清除不應重複。");

            // 標記只抵銷一次：之後的清除（例如重載失敗後的自動恢復）仍須重置。
            PlayDataLoader_ClearAllPlayData_Patch.Prefix();
            Assert.That(resetCount, Is.EqualTo(2));
        }

        [Test]
        public void Prefix_AfterSelectLanguageWasCancelled_StillResets()
        {
            // 其他 mod 的前置處理取消了原版語言切換：不會有對應的清除，標記必須撤銷。
            LanguageDatabase_SelectLanguage_Patch.Prefix();
            LanguageDatabase_SelectLanguage_Patch.Postfix(__runOriginal: false);
            Assert.That(resetCount, Is.EqualTo(1));

            PlayDataLoader_ClearAllPlayData_Patch.Prefix();
            Assert.That(resetCount, Is.EqualTo(2), "之後無關的清除（例如自動恢復）仍須重置。");
        }

        [Test]
        public void RaiseOnMainThread_WhenPumpHasStalled_GivesUpWithoutWaiting()
        {
            // 模擬主執行緒的泵送早已停擺（例如 DelayedActions 被停用）。
            AccessTools.Field(typeof(SessionLifecycle), "lastDrainTimestamp").SetValue(null, 1L);

            var stopwatch = Stopwatch.StartNew();
            bool raised = Task.Run(static () => SessionLifecycle.RaiseOnMainThread(LifecyclePhase.LanguageReloading)).Result;

            Assert.That(raised, Is.False);
            Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(5000), "不應等滿 10 秒的逾時。");
            SessionLifecycle.DrainMainThreadRaises();
            Assert.That(resetCount, Is.Zero);
        }

        [Test]
        public void Prefix_FromLoadingThread_ResetsOnMainThreadBeforeReturning()
        {
            // 原版錯誤恢復在載入事件緒呼叫 ClearAllPlayData。
            var recovery = Task.Run(PlayDataLoader_ClearAllPlayData_Patch.Prefix);

            var stopwatch = Stopwatch.StartNew();
            while (!recovery.IsCompleted && stopwatch.ElapsedMilliseconds < 5000)
            {
                Assert.That(resetCount, Is.Zero, "主執行緒泵送前不得在載入事件緒執行重置。");
                SessionLifecycle.DrainMainThreadRaises();
                Thread.Sleep(1);
            }

            Assert.That(recovery.IsCompleted, Is.True, "主執行緒執行重置後，載入事件緒應繼續。");
            Assert.That(resetCount, Is.EqualTo(1));
            Assert.That(lastResetThreadId, Is.EqualTo(mainThreadId));
        }

        [Test]
        public void RaiseOnMainThread_WhenMainThreadDoesNotPump_TimesOutAndDropsRequest()
        {
            SessionLifecycle.MainThreadRaiseTimeoutMs = 50;

            bool raised = Task.Run(static () => SessionLifecycle.RaiseOnMainThread(LifecyclePhase.LanguageReloading)).Result;
            SessionLifecycle.DrainMainThreadRaises();

            Assert.That(raised, Is.False);
            Assert.That(resetCount, Is.Zero, "逾時放棄的請求之後不得再執行，以免清掉新一輪載入的狀態。");
        }
    }
}
