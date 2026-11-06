using System;
using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;
using Verse;
using Verse.Sound;

namespace FasterGameLoading.Tests.Core;
[TestFixture]
public class DelayedActionsTests
{
    private Harmony harmony;
    private DelayedActions delayedActions;

    private static bool PrefixSkip() => false;

    private static SubSoundDef NewSubSound() => (SubSoundDef)FormatterServices.GetUninitializedObject(typeof(SubSoundDef));

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        harmony = new Harmony("FasterGameLoading.Tests.DelayedActionsTests");
        var emitMethod = AccessTools.Method(typeof(FGLLog), "Emit");
        if (emitMethod != null)
        {
            harmony.Patch(emitMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(DelayedActionsTests), nameof(PrefixSkip))));
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        harmony?.UnpatchAll("FasterGameLoading.Tests.DelayedActionsTests");
    }

    [SetUp]
    public void SetUp()
    {
        delayedActions = new DelayedActions();
    }

    [TearDown]
    public void TearDown()
    {
        delayedActions?.ClearQueues();
    }

    private void RunToCompletion() => RunLikeUnity(delayedActions.PerformActions());

    // 模擬 Unity 協程：yield 出的 IEnumerator 視為子協程，先跑完再繼續外層。
    private static void RunLikeUnity(System.Collections.IEnumerator coroutine)
    {
        while (coroutine.MoveNext())
        {
            if (coroutine.Current is System.Collections.IEnumerator nested)
            {
                RunLikeUnity(nested);
            }
        }
    }

    [Test]
    public void ClearQueues_DropsAllQueuedWorkAndResetsCompletion()
    {
        RunToCompletion();
        Assert.That(delayedActions.DeferredWorkCompleted, Is.True);

        delayedActions.EnqueueSubSound(NewSubSound(), () => { });

        delayedActions.ClearQueues();

        Assert.That(delayedActions.SubSoundDefToResolveCount, Is.EqualTo(0));
        Assert.That(delayedActions.DeferredWorkCompleted, Is.False);
    }

    [Test]
    public void ResolvePendingSubSounds_RunsEveryQueuedSoundAndContinuesPastFailures()
    {
        bool secondRan = false;
        delayedActions.EnqueueSubSound(NewSubSound(), () => throw new InvalidOperationException("Simulated grain failure"));
        delayedActions.EnqueueSubSound(NewSubSound(), () => secondRan = true);

        Assert.DoesNotThrow(delayedActions.ResolvePendingSubSounds);

        Assert.That(secondRan, Is.True);
        Assert.That(delayedActions.SubSoundDefToResolveCount, Is.EqualTo(0));
    }

    [Test]
    public void Budget_StartsUnderBudgetWithMenuFrameLimit()
    {
        Assert.That(delayedActions.IsOverBudget, Is.False);
        // 測試環境沒有 Current.Game，套用主選單的 50ms 上限。
        Assert.That(DelayedActions.MaxImpactThisFrame, Is.EqualTo(0.05f));
    }

    [Test]
    public void LateUpdate_InvokesEarlyModContentLoaderUpdateWithoutThrowing()
    {
        // LateUpdate 每幀呼叫 EarlyModContentLoader.Update；在設定關閉時該方法提前返回，
        // 此處僅驗證呼叫路徑本身不拋出例外（覆蓋 DelayedActions.LateUpdate）。
        Assert.DoesNotThrow(() => delayedActions.LateUpdate());
    }

    [Test]
    public void PerformActions_ResolvesSoundsAndCompletes()
    {
        bool soundResolved = false;
        delayedActions.EnqueueSubSound(NewSubSound(), () => soundResolved = true);

        RunToCompletion();

        Assert.That(soundResolved, Is.True);
        Assert.That(delayedActions.DeferredWorkCompleted, Is.True);
    }

    // 以假時鐘的時間軸執行 <paramref name="body"/>，並在主選單已出現（時間 1）後開始。
    private static void WithTimelineAfterMainMenu(Action<StartupTimeline, Action<double>> body)
    {
        double now = 0;
        var timeline = new StartupTimeline(() => now);
        timeline.Observe(mainMenuShown: true, playing: false);
        now = 1;
        timeline.Observe(mainMenuShown: true, playing: false);
        var previous = StartupTimeline.Instance;
        StartupTimeline.Instance = timeline;
        try
        {
            body(timeline, t => now = t);
        }
        finally
        {
            StartupTimeline.Instance = previous;
        }
    }

    [Test]
    public void PerformActions_MarksAllReadyAfterSounds()
    {
        WithTimelineAfterMainMenu((timeline, setNow) =>
        {
            setNow(3);
            // 音效解析要花時間：解析期間時鐘前進到 5。
            delayedActions.EnqueueSubSound(NewSubSound(), () => setNow(5));

            RunToCompletion();

            Assert.That(timeline.Observe(mainMenuShown: true, playing: false),
                Does.Contain("all deferred work done +4.0 s."));
        });
    }

    [Test]
    public void ReportStartupTiming_WithVerboseLoggingOff_StillConsumesTheSummary()
    {
        // 啟動後才開啟詳細日誌時不能再輸出：那時才記下的主選單時間會讓摘要變成假的 +0.0 s。
        bool originalVerbose = FasterGameLoadingSettings.VerboseLogging;
        FasterGameLoadingSettings.VerboseLogging = false;
        try
        {
            WithTimelineAfterMainMenu((timeline, setNow) =>
            {
                timeline.MarkAllReady();

                DelayedActions.ReportStartupTiming();

                Assert.That(timeline.Pending, Is.False);
            });
        }
        finally
        {
            FasterGameLoadingSettings.VerboseLogging = originalVerbose;
        }
    }
}
