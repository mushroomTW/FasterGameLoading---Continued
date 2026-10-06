using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;
using Verse;
using Verse.Sound;

namespace FasterGameLoading.Tests.DelaySoundLoading
{
    [TestFixture]
    public class SoundStarter_PatchTests
    {
        private static FieldInfo unpatchedField;
        private static PropertyInfo delayedActionsProp;
        private DelayedActions delayedActions;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            unpatchedField = AccessTools.Field(typeof(SoundStarter_Patch), "unpatched");
            delayedActionsProp = typeof(FasterGameLoadingMod).GetProperty(
                nameof(FasterGameLoadingMod.delayedActions), BindingFlags.Public | BindingFlags.Static);

            var fglModHarmonyProp = typeof(FasterGameLoadingMod).GetProperty(
                nameof(FasterGameLoadingMod.harmony), BindingFlags.Public | BindingFlags.Static);
            fglModHarmonyProp?.SetValue(null, new Harmony("FasterGameLoadingMod.SoundStarter_PatchTests"), index: null);
        }

        [SetUp]
        public void SetUp()
        {
            SoundStarter_Patch.ResetUnpatchedStatus();
            delayedActions = new DelayedActions();
            delayedActionsProp.SetValue(null, delayedActions, index: null);
        }

        [TearDown]
        public void TearDown()
        {
            SoundStarter_Patch.ResetUnpatchedStatus();
            TestSetup.IsInMainThreadOverride = null;
            delayedActions.ClearQueues();
            delayedActionsProp.SetValue(null, value: null, index: null);
        }

        private static readonly FieldInfo ResolvedGrainsField = AccessTools.Field(typeof(SubSoundDef), "resolvedGrains");

        /// <summary>建立尚未解析（resolvedGrains 為空）的 SubSoundDef。</summary>
        private static SubSoundDef NewSubSound()
        {
            var subSound = (SubSoundDef)FormatterServices.GetUninitializedObject(typeof(SubSoundDef));
            ResolvedGrainsField.SetValue(subSound, new List<ResolvedGrain>());
            return subSound;
        }

        /// <summary>模擬原版解析回呼：在 resolvedGrains 放入一個 grain。</summary>
        private static void AddGrain(SubSoundDef subSound) =>
            ((List<ResolvedGrain>)ResolvedGrainsField.GetValue(subSound)).Add(
                (ResolvedGrain)FormatterServices.GetUninitializedObject(typeof(ResolvedGrain_Silence)));

        private static SubSoundDef NewResolvedSubSound()
        {
            var subSound = NewSubSound();
            AddGrain(subSound);
            return subSound;
        }

        private static bool InvokeTryPlay(SubSoundDef subSound) =>
            (bool)AccessTools.Method(typeof(SoundStarter_Patch), "TryPlay_Patch").Invoke(null, new object[] { subSound });

        private static bool InvokeTrySpawnSustainer(SoundDef soundDef, object[] args)
        {
            args[0] = soundDef;
            return (bool)AccessTools.Method(typeof(SoundStarter_Patch), "TrySpawnSustainer_Patch").Invoke(null, args);
        }

        [Test]
        public void TryPlay_Patch_ResolvesPendingSubSoundOnDemandAndAllowsPlayback()
        {
            var subSound = NewSubSound();
            int runs = 0;
            delayedActions.EnqueueSubSound(subSound, () => { runs++; AddGrain(subSound); });

            Assert.That(InvokeTryPlay(subSound), Is.True);
            Assert.That(runs, Is.EqualTo(1));
            Assert.That(delayedActions.SubSoundDefToResolveCount, Is.EqualTo(0));

            // 已解析的 SubSoundDef 再次播放不會重跑解析，佇列輪到它時也直接略過。
            Assert.That(InvokeTryPlay(subSound), Is.True);
            delayedActions.ResolvePendingSubSounds();
            Assert.That(runs, Is.EqualTo(1));
        }

        [Test]
        public void TryPlay_Patch_LeavesOtherSubSoundsQueued()
        {
            var played = NewSubSound();
            var other = NewSubSound();
            bool otherRan = false;
            delayedActions.EnqueueSubSound(played, () => AddGrain(played));
            delayedActions.EnqueueSubSound(other, () => otherRan = true);

            InvokeTryPlay(played);

            Assert.That(otherRan, Is.False);
            Assert.That(delayedActions.SubSoundDefToResolveCount, Is.EqualTo(1));
        }

        [Test]
        public void TryPlay_Patch_WhenSubSoundIsAlreadyResolved_AllowsPlayback()
        {
            Assert.That(InvokeTryPlay(NewResolvedSubSound()), Is.True);
        }

        [Test]
        public void TryPlay_Patch_WhenSubSoundIsNotQueuedYet_SkipsPlaybackSilently()
        {
            // 載入或重載中、ResolveReferences 之前：既不在佇列，也還沒有 grain。
            Assert.That(InvokeTryPlay(NewSubSound()), Is.False);
        }

        [Test]
        public void TryPlay_Patch_WhenResolutionThrows_SkipsPlaybackAndDropsTheAction()
        {
            var subSound = NewSubSound();
            delayedActions.EnqueueSubSound(subSound, () => throw new InvalidOperationException("Simulated grain failure"));

            Assert.That(InvokeTryPlay(subSound), Is.False);
            Assert.That(delayedActions.SubSoundDefToResolveCount, Is.EqualTo(0));
        }

        [Test]
        public void TryPlay_Patch_OffMainThread_SkipsPlaybackAndKeepsSubSoundPending()
        {
            var subSound = NewSubSound();
            bool ran = false;
            delayedActions.EnqueueSubSound(subSound, () => ran = true);
            TestSetup.IsInMainThreadOverride = () => false;

            Assert.That(InvokeTryPlay(subSound), Is.False);
            Assert.That(ran, Is.False);
            Assert.That(delayedActions.SubSoundDefToResolveCount, Is.EqualTo(1));
        }

        [Test]
        public void TryPlay_Patch_WithoutDelayedActions_AllowsOnlyResolvedSubSounds()
        {
            delayedActionsProp.SetValue(null, value: null, index: null);

            Assert.That(InvokeTryPlay(NewResolvedSubSound()), Is.True);
            Assert.That(InvokeTryPlay(NewSubSound()), Is.False);
        }

        [Test]
        public void TrySpawnSustainer_Patch_ResolvesEverySubSoundOfTheDefBeforeSpawning()
        {
            var first = NewSubSound();
            var second = NewSubSound();
            int runs = 0;
            delayedActions.EnqueueSubSound(first, () => { runs++; AddGrain(first); });
            delayedActions.EnqueueSubSound(second, () => { runs++; AddGrain(second); });
            var soundDef = (SoundDef)FormatterServices.GetUninitializedObject(typeof(SoundDef));
            soundDef.subSounds = new List<SubSoundDef> { first, second };

            Assert.That(InvokeTrySpawnSustainer(soundDef, new object[2]), Is.True);
            Assert.That(runs, Is.EqualTo(2));
            Assert.That(delayedActions.SubSoundDefToResolveCount, Is.EqualTo(0));
        }

        [Test]
        public void TrySpawnSustainer_Patch_WithUnresolvedSubSound_ReturnsNullSustainer()
        {
            var soundDef = (SoundDef)FormatterServices.GetUninitializedObject(typeof(SoundDef));
            soundDef.subSounds = new List<SubSoundDef> { NewResolvedSubSound(), NewSubSound() };
            var args = new object[] { null, FormatterServices.GetUninitializedObject(typeof(Sustainer)) };

            Assert.That(InvokeTrySpawnSustainer(soundDef, args), Is.False);
            Assert.That(args[1], Is.Null);
        }

        [Test]
        public void TrySpawnSustainer_Patch_OffMainThread_ReturnsNullEvenWhenResolved()
        {
            var pending = NewSubSound();
            delayedActions.EnqueueSubSound(pending, () => AddGrain(pending));
            var soundDef = (SoundDef)FormatterServices.GetUninitializedObject(typeof(SoundDef));
            soundDef.subSounds = new List<SubSoundDef> { NewResolvedSubSound(), pending };
            TestSetup.IsInMainThreadOverride = () => false;
            var args = new object[] { null, FormatterServices.GetUninitializedObject(typeof(Sustainer)) };

            Assert.That(InvokeTrySpawnSustainer(soundDef, args), Is.False);
            Assert.That(args[1], Is.Null);
            Assert.That(delayedActions.SubSoundDefToResolveCount, Is.EqualTo(1));
        }

        [Test]
        public void TrySpawnSustainer_Patch_WithNullSoundDef_DefersToVanilla()
        {
            Assert.That(InvokeTrySpawnSustainer(null, new object[2]), Is.True);
        }

        [Test]
        public void Unpatch_And_ResetUnpatchedStatus_OperatesCorrectlyAndIdempotently()
        {
            Assert.That((bool)unpatchedField.GetValue(null), Is.False);

            SoundStarter_Patch.Unpatch();
            Assert.That((bool)unpatchedField.GetValue(null), Is.True);

            // Second call is idempotent
            SoundStarter_Patch.Unpatch();
            Assert.That((bool)unpatchedField.GetValue(null), Is.True);

            SoundStarter_Patch.ResetUnpatchedStatus();
            Assert.That((bool)unpatchedField.GetValue(null), Is.False);
        }

        private static bool ThrowingUnpatchCategoryPrefix() =>
            throw new InvalidOperationException("Simulated UnpatchCategory failure for testing");

        [Test]
        public void Unpatch_WhenUnpatchCategoryThrows_CatchesExceptionAndStillMarksUnpatched()
        {
            // 讓 Harmony.UnpatchCategory 拋例外，驗證 Unpatch() 的 try/catch 會安全吞掉例外並記錄警告，
            // 不影響 unpatched 狀態的設定。
            var unpatchCategoryMethod = AccessTools.Method(typeof(Harmony), nameof(Harmony.UnpatchCategory), new[] { typeof(string) });
            var metaHarmony = new Harmony("FasterGameLoadingMod.SoundStarter_PatchTests.Meta");
            metaHarmony.Patch(unpatchCategoryMethod, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(SoundStarter_PatchTests), nameof(ThrowingUnpatchCategoryPrefix))));
            try
            {
                Assert.DoesNotThrow(() => SoundStarter_Patch.Unpatch());
                Assert.That((bool)unpatchedField.GetValue(null), Is.True);
            }
            finally
            {
                metaHarmony.UnpatchAll("FasterGameLoadingMod.SoundStarter_PatchTests.Meta");
            }
        }
    }
}
