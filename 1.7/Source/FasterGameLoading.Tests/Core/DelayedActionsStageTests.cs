using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;
using Verse;
using Verse.Sound;

namespace FasterGameLoading.Tests.Core;
[TestFixture]
public class DelayedActionsStageTests
{
    private static Harmony harmony;
    private DelayedActions delayedActions;

    private static bool PrefixSkip() => false;

    private static SubSoundDef NewSubSound() => (SubSoundDef)FormatterServices.GetUninitializedObject(typeof(SubSoundDef));

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        harmony = new Harmony("FasterGameLoading.Tests.DelayedActionsStageTests");

        var emitMethod = AccessTools.Method(typeof(FGLLog), "Emit");
        if (emitMethod != null)
        {
            harmony.Patch(emitMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(DelayedActionsStageTests), nameof(PrefixSkip))));
        }

        // Initialize FasterGameLoadingMod.harmony for SoundStarter_Patch.Unpatch()
        var fglModHarmonyProp = typeof(FasterGameLoadingMod).GetProperty(nameof(FasterGameLoadingMod.harmony), BindingFlags.Public | BindingFlags.Static);
        fglModHarmonyProp?.SetValue(obj: null, value: new Harmony("FasterGameLoadingMod.TestInstance"), index: null);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        harmony?.UnpatchAll("FasterGameLoading.Tests.DelayedActionsStageTests");
    }

    [SetUp]
    public void SetUp()
    {
        TestSetup.IsInMainThreadOverride = () => true;
        delayedActions = new DelayedActions();
        SoundStarter_Patch.ResetUnpatchedStatus();
    }

    [TearDown]
    public void TearDown()
    {
        TestSetup.IsInMainThreadOverride = null;
        delayedActions?.ClearQueues();
    }

    [Test]
    public void ResolveSubSoundDefsCoroutine_DrainsQueueAndCallsUnpatch()
    {
        bool act1Executed = false;
        bool act2Executed = false;

        delayedActions.EnqueueSubSound(NewSubSound(), () => act1Executed = true);
        delayedActions.EnqueueSubSound(NewSubSound(), () => act2Executed = true);

        var coroutine = delayedActions.ResolveSubSoundDefsCoroutine();
        while (coroutine.MoveNext()) { }

        Assert.That(act1Executed, Is.True);
        Assert.That(act2Executed, Is.True);
        Assert.That(delayedActions.SubSoundDefToResolveCount, Is.EqualTo(0));
        Assert.That(AccessTools.Field(typeof(SoundStarter_Patch), "unpatched").GetValue(obj: null), Is.EqualTo(true));
    }

    [Test]
    public void ResolveSubSoundDefsCoroutine_WhenActionThrows_CatchesAndContinues()
    {
        bool act2Executed = false;

        delayedActions.EnqueueSubSound(NewSubSound(), () => throw new InvalidOperationException("Audio error"));
        delayedActions.EnqueueSubSound(NewSubSound(), () => act2Executed = true);

        var coroutine = delayedActions.ResolveSubSoundDefsCoroutine();
        Assert.DoesNotThrow(() =>
        {
            while (coroutine.MoveNext()) { }
        });

        Assert.That(act2Executed, Is.True);
        Assert.That(delayedActions.SubSoundDefToResolveCount, Is.EqualTo(0));
    }

    private static bool MockOverBudgetTrue(ref bool __result)
    {
        __result = true;
        return false;
    }

    [Test]
    public void ResolveSubSoundDefsCoroutine_WhenOverBudget_YieldsAndRestartsStopwatch()
    {
        delayedActions.EnqueueSubSound(NewSubSound(), () => { });
        delayedActions.EnqueueSubSound(NewSubSound(), () => { });

        var getter = AccessTools.PropertyGetter(typeof(DelayedActions), nameof(DelayedActions.IsOverBudget));
        var patch = new HarmonyMethod(AccessTools.Method(typeof(DelayedActionsStageTests), nameof(MockOverBudgetTrue)));
        harmony.Patch(getter, prefix: patch);

        try
        {
            var coroutine = delayedActions.ResolveSubSoundDefsCoroutine();
            bool moved = coroutine.MoveNext();
            Assert.That(moved, Is.True);
            Assert.That(coroutine.Current, Is.EqualTo(0));
            Assert.That(delayedActions.SubSoundDefToResolveCount, Is.EqualTo(2));

            // 下一幀恢復時要重新計時，否則該幀的預算會被前一幀的耗時吃掉。
            var stopwatch = (Stopwatch)AccessTools.Field(typeof(DelayedActions), "stopwatch").GetValue(delayedActions);
            stopwatch.Stop();
            coroutine.MoveNext();
            Assert.That(stopwatch.IsRunning, Is.True);
        }
        finally
        {
            harmony.Unpatch(getter, patch.method);
        }
    }

    [Test]
    public void ResolveSubSoundDefsCoroutine_WhenNotInMainThread_YieldsAndContinues()
    {
        delayedActions.EnqueueSubSound(NewSubSound(), () => { });
        TestSetup.IsInMainThreadOverride = () => false;

        var coroutine = delayedActions.ResolveSubSoundDefsCoroutine();

        Assert.That(coroutine.MoveNext(), Is.True);
        Assert.That(coroutine.Current, Is.EqualTo(0));
        Assert.That(delayedActions.SubSoundDefToResolveCount, Is.EqualTo(1));
    }
}
