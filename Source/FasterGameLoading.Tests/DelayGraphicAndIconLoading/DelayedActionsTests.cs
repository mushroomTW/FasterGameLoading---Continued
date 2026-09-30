using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;
using Verse;
using Verse.Sound;

namespace FasterGameLoading.Tests.DelayGraphicAndIconLoading
{
    [TestFixture]
    public class DelayedActionsTests
    {
        private Harmony harmony;
        private DelayedActions delayedActions;
        private bool originalDelay;
        private bool originalStaticBake;
        private static int vanillaBakeCalls;

        private static bool PrefixSkip() => false;

        private static bool CountVanillaBake()
        {
            vanillaBakeCalls++;
            return false;
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            harmony = new Harmony("FasterGameLoading.Tests.DelayedActionsTests");
            var emitMethod = AccessTools.Method(typeof(FGLLog), "Emit");
            if (emitMethod != null)
            {
                harmony.Patch(emitMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(DelayedActionsTests), nameof(PrefixSkip))));
            }
            // 原版烘焙需要圖集基礎設施，無頭環境只記錄被呼叫的次數。
            harmony.Patch(
                AccessTools.Method(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.BakeStaticAtlases)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(DelayedActionsTests), nameof(CountVanillaBake))));
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
            originalDelay = FasterGameLoadingSettings.DelayGraphicLoading;
            originalStaticBake = FasterGameLoadingSettings.StaticAtlasesBaking;
            vanillaBakeCalls = 0;
            SessionLifecycle.Raise(LifecyclePhase.LanguageReloading);
        }

        [TearDown]
        public void TearDown()
        {
            delayedActions?.ClearQueues();
            FasterGameLoadingSettings.DelayGraphicLoading = originalDelay;
            FasterGameLoadingSettings.StaticAtlasesBaking = originalStaticBake;
            DelayedActions.ReleaseStartupSettingsForTests();
            SessionLifecycle.Raise(LifecyclePhase.LanguageReloading);
        }

        private static ThingDef CreateMockThingDef(string name)
        {
            var def = (ThingDef)FormatterServices.GetUninitializedObject(typeof(ThingDef));
            def.defName = name;
            return def;
        }

        private void RunToCompletion() => RunLikeUnity(delayedActions.PerformActions());

        /// <summary>模擬 Unity 協程：yield 出的 IEnumerator 視為子協程，先跑完再繼續外層。</summary>
        internal static void RunLikeUnity(System.Collections.IEnumerator coroutine)
        {
            while (coroutine.MoveNext())
            {
                if (coroutine.Current is System.Collections.IEnumerator nested)
                {
                    RunLikeUnity(nested);
                }
            }
        }

        private void CaptureSettings(bool delay, bool adaptive)
        {
            FasterGameLoadingSettings.DelayGraphicLoading = delay;
            FasterGameLoadingSettings.StaticAtlasesBaking = adaptive;
            DelayedActions.CaptureStartupSettings();
        }

        [Test]
        public void DeferredGraphics_RunInEnqueueOrder()
        {
            var order = new List<string>();
            delayedActions.EnqueueGraphic(CreateMockThingDef("First"), () => order.Add("First"));
            delayedActions.EnqueueGraphic(CreateMockThingDef("Second"), () => order.Add("Second"));

            var drain = delayedActions.LoadDeferredGraphicsCoroutine(new List<ThingDef>());
            while (drain.MoveNext()) { }

            Assert.That(order, Is.EqualTo(new[] { "First", "Second" }));
        }

        [Test]
        public void ClearQueues_DropsAllQueuedWorkAndResetsPhase()
        {
            CaptureSettings(delay: false, adaptive: false);
            RunToCompletion();
            Assert.That(delayedActions.Phase, Is.EqualTo(DeferredPhase.Completed));

            delayedActions.EnqueueGraphic(CreateMockThingDef("TestClear"), () => { });
            delayedActions.EnqueueIcon(CreateMockThingDef("IconClear"), () => { });
            delayedActions.EnqueueSubSound((SubSoundDef)FormatterServices.GetUninitializedObject(typeof(SubSoundDef)), () => { });

            delayedActions.ClearQueues();

            Assert.That(delayedActions.GraphicsToLoadCount, Is.EqualTo(0));
            Assert.That(delayedActions.IconsToLoadCount, Is.EqualTo(0));
            Assert.That(delayedActions.SubSoundDefToResolveCount, Is.EqualTo(0));
            Assert.That(delayedActions.Phase, Is.EqualTo(DeferredPhase.Idle));
        }

        [Test]
        public void ResolvePendingSubSounds_RunsEveryQueuedSoundAndContinuesPastFailures()
        {
            bool secondRan = false;
            delayedActions.EnqueueSubSound((SubSoundDef)FormatterServices.GetUninitializedObject(typeof(SubSoundDef)),
                () => throw new InvalidOperationException("Simulated grain failure"));
            delayedActions.EnqueueSubSound((SubSoundDef)FormatterServices.GetUninitializedObject(typeof(SubSoundDef)),
                () => secondRan = true);

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
        public void PerformActions_WhenDeferredVisualsDisabled_ResolvesSoundsWithoutBaking()
        {
            CaptureSettings(delay: false, adaptive: false);
            bool soundResolved = false;
            delayedActions.EnqueueSubSound((SubSoundDef)FormatterServices.GetUninitializedObject(typeof(SubSoundDef)), () => soundResolved = true);

            RunToCompletion();

            Assert.That(soundResolved, Is.True);
            Assert.That(vanillaBakeCalls, Is.Zero, "延遲視覺關閉時靜態圖集已在啟動流程由原版烘焙，不可再烘一次。");
            Assert.That(delayedActions.Phase, Is.EqualTo(DeferredPhase.Completed));
            Assert.That(DelayedActions.AllowsVanillaStaticBake, Is.True);
        }

        [Test]
        public void PerformActions_WhenDeferredVisualsEnabled_LoadsGraphicsThenIconsBeforeBaking()
        {
            CaptureSettings(delay: true, adaptive: false);
            // 圖示只需要剛載入的圖形、與圖集無關，排在烘焙之前，玩家不必等整批圖集烘焙完才看到正確圖示。
            var enumerator = delayedActions.PerformActions();
            try
            {
                Assert.That(enumerator.MoveNext(), Is.True);
                Assert.That(delayedActions.Phase, Is.EqualTo(DeferredPhase.Graphics));
                Assert.That(enumerator.Current.GetType().Name, Does.Contain(nameof(DelayedActions.LoadDeferredGraphicsCoroutine)));
                Assert.That(enumerator.MoveNext(), Is.True);
                Assert.That(delayedActions.Phase, Is.EqualTo(DeferredPhase.Icons));
                Assert.That(enumerator.Current.GetType().Name, Does.Contain(nameof(DelayedActions.LoadDeferredIconsCoroutine)));
            }
            finally
            {
                if (enumerator is IDisposable disposable)
                    disposable.Dispose();
            }
        }

        [Test]
        public void StartupBake_IsHeldUntilDeferredVanillaBakeRuns()
        {
            CaptureSettings(delay: true, adaptive: false);
            Assert.That(DelayedActions.AllowsVanillaStaticBake, Is.False, "啟動流程那一次烘焙必須延後到延遲圖形載入之後。");

            RunToCompletion();

            Assert.That(vanillaBakeCalls, Is.EqualTo(1));
            Assert.That(DelayedActions.AllowsVanillaStaticBake, Is.True);
            Assert.That(delayedActions.Phase, Is.EqualTo(DeferredPhase.Completed));
        }

        [Test]
        public void LanguageReload_HoldsStartupBakeAgain()
        {
            CaptureSettings(delay: true, adaptive: false);
            RunToCompletion();
            Assert.That(DelayedActions.AllowsVanillaStaticBake, Is.True);

            SessionLifecycle.Raise(LifecyclePhase.LanguageReloading);

            Assert.That(DelayedActions.AllowsVanillaStaticBake, Is.False);
        }

        [Test]
        public void CapturedSettings_TurningDelayOffMidSession_StillDrainsQueuedGraphics()
        {
            // PostLoad 補丁在啟動時依「開啟」套用並持續排入佇列；玩家之後在設定頁關閉，
            // 管線仍須照定案值執行，否則切換語言後排入的圖形永遠不會載入。
            CaptureSettings(delay: true, adaptive: false);
            FasterGameLoadingSettings.DelayGraphicLoading = false;
            bool graphicLoaded = false;
            delayedActions.EnqueueGraphic(CreateMockThingDef("QueuedAfterToggle"), () => graphicLoaded = true);

            RunToCompletion();

            Assert.That(ThingDef_PostLoad_Patch.Prepare(), Is.True);
            Assert.That(graphicLoaded, Is.True);
            Assert.That(delayedActions.GraphicsToLoadCount, Is.EqualTo(0));
        }
    }
}
