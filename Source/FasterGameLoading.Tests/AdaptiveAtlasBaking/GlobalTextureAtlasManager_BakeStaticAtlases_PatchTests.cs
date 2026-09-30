using System.Collections;
using System.Linq;
using HarmonyLib;
using NUnit.Framework;
using Verse;

namespace FasterGameLoading.Tests.AdaptiveAtlasBaking
{
    [TestFixture]
    public class GlobalTextureAtlasManager_BakeStaticAtlases_PatchTests
    {
        private static Harmony harmony;
        private static bool simulatedAdaptiveFailure;
        private bool previousDelay;
        private bool previousStaticBake;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            harmony = new Harmony("FasterGameLoading.Tests.AtlasPatch");
            var prefixSkip = new HarmonyMethod(AccessTools.Method(typeof(GlobalTextureAtlasManager_BakeStaticAtlases_PatchTests), nameof(PrefixSkip)));
            harmony.Patch(AccessTools.Method(typeof(FGLLog), "Emit"), prefix: prefixSkip);
            harmony.Patch(AccessTools.Method(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.BakeStaticAtlases)), prefix: prefixSkip);
            // 自適應烘焙本身由 AdaptiveAtlasBakerTests 驗證；這裡只模擬它的結果。
            harmony.Patch(AccessTools.Method(typeof(AdaptiveAtlasBaker), nameof(AdaptiveAtlasBaker.PerformAdaptiveStaticAtlasBake)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(GlobalTextureAtlasManager_BakeStaticAtlases_PatchTests), nameof(EmptyBake))));
            harmony.Patch(AccessTools.PropertyGetter(typeof(AdaptiveAtlasBaker), nameof(AdaptiveAtlasBaker.LastBakeFailed)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(GlobalTextureAtlasManager_BakeStaticAtlases_PatchTests), nameof(SimulatedFailure))));
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            harmony.UnpatchAll("FasterGameLoading.Tests.AtlasPatch");
        }

        private static bool PrefixSkip() => false;

        private static bool EmptyBake(ref IEnumerator __result)
        {
            __result = Enumerable.Empty<object>().GetEnumerator();
            return false;
        }

        private static bool SimulatedFailure(ref bool __result)
        {
            __result = simulatedAdaptiveFailure;
            return false;
        }

        [SetUp]
        public void SetUp()
        {
            previousDelay = FasterGameLoadingSettings.DelayGraphicLoading;
            previousStaticBake = FasterGameLoadingSettings.StaticAtlasesBaking;
            SessionLifecycle.Raise(LifecyclePhase.LanguageReloading);
        }

        [TearDown]
        public void TearDown()
        {
            FasterGameLoadingSettings.DelayGraphicLoading = previousDelay;
            FasterGameLoadingSettings.StaticAtlasesBaking = previousStaticBake;
            DelayedActions.ReleaseStartupSettingsForTests();
            SessionLifecycle.Raise(LifecyclePhase.LanguageReloading);
        }

        private static void CaptureSettings(bool delay, bool adaptive)
        {
            FasterGameLoadingSettings.DelayGraphicLoading = delay;
            FasterGameLoadingSettings.StaticAtlasesBaking = adaptive;
            DelayedActions.CaptureStartupSettings();
        }

        private static void RunDeferredPipeline()
        {
            DelayGraphicAndIconLoading.DelayedActionsTests.RunLikeUnity(new DelayedActions().PerformActions());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Prefix_WithoutDeferredVisuals_LetsVanillaRun(bool adaptive)
        {
            // 同步路徑沒有分幀的空間，自適應分批只會把圖集切碎；一律交給原版。
            CaptureSettings(delay: false, adaptive: adaptive);

            Assert.That(GlobalTextureAtlasManager_BakeStaticAtlases_Patch.Prefix(), Is.True);
        }

        [Test]
        public void Prefix_WithDeferredVisuals_SkipsStartupBake()
        {
            CaptureSettings(delay: true, adaptive: true);

            Assert.That(GlobalTextureAtlasManager_BakeStaticAtlases_Patch.Prefix(), Is.False);
        }

        [TestCase(false, false)]
        [TestCase(true, true)]
        public void Prefix_AfterAdaptiveBake_LetsVanillaRunOnlyAsFailureFallback(bool failed, bool expected)
        {
            CaptureSettings(delay: true, adaptive: true);
            simulatedAdaptiveFailure = failed;
            try
            {
                RunDeferredPipeline();
            }
            finally
            {
                simulatedAdaptiveFailure = false;
            }

            Assert.That(GlobalTextureAtlasManager_BakeStaticAtlases_Patch.Prefix(), Is.EqualTo(expected));
        }
    }
}
