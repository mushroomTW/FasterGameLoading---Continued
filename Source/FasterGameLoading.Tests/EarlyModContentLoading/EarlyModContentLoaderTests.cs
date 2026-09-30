using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;
using Verse;

namespace FasterGameLoading.Tests.EarlyModContentLoading
{
    [TestFixture]
    public class EarlyModContentLoaderTests
    {
        private static Harmony harmony;
        private EarlyModContentLoader loader;
        private DelayedActions delayedActions;
        private List<ModContentPack> originalRunningMods;

        private static bool mockReloadShouldThrow;
        private static readonly List<ModContentPack> reloadedMods = new List<ModContentPack>();
        private static bool mockIsOverBudget;

        private static bool PrefixSkip() => false;

        private static bool MockReloadContentInt(ModContentPack __instance)
        {
            if (mockReloadShouldThrow)
            {
                throw new InvalidOperationException("Simulated ReloadContentInt failure for testing");
            }
            reloadedMods.Add(__instance);
            return false;
        }

        private static bool MockIsOverBudget(ref bool __result)
        {
            __result = mockIsOverBudget;
            return false;
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            harmony = new Harmony("FasterGameLoading.Tests.EarlyModContentLoaderTests");

            var emitMethod = AccessTools.Method(typeof(FGLLog), "Emit");
            if (emitMethod != null)
            {
                harmony.Patch(emitMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(EarlyModContentLoaderTests), nameof(PrefixSkip))));
            }

            var reloadContentIntMethod = AccessTools.Method(typeof(ModContentPack), "ReloadContentInt");
            if (reloadContentIntMethod != null)
            {
                harmony.Patch(reloadContentIntMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(EarlyModContentLoaderTests), nameof(MockReloadContentInt))));
            }

            var isOverBudgetGetter = AccessTools.PropertyGetter(typeof(DelayedActions), nameof(DelayedActions.IsOverBudget));
            if (isOverBudgetGetter != null)
            {
                harmony.Patch(isOverBudgetGetter, prefix: new HarmonyMethod(AccessTools.Method(typeof(EarlyModContentLoaderTests), nameof(MockIsOverBudget))));
            }
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            harmony?.UnpatchAll("FasterGameLoading.Tests.EarlyModContentLoaderTests");
        }

        [SetUp]
        public void SetUp()
        {
            loader = new EarlyModContentLoader();
            delayedActions = new DelayedActions();
            mockReloadShouldThrow = false;
            mockIsOverBudget = false;
            reloadedMods.Clear();
            ModContentPack_ReloadContentInt_Patch.loadedMods.Clear();
            FasterGameLoadingSettings.earlyModContentLoading = true;
            EarlyModContentLoader.ModClassesCreated = true;

            var runningModsField = AccessTools.Field(typeof(LoadedModManager), "runningMods");
            if (runningModsField != null)
            {
                originalRunningMods = (List<ModContentPack>)runningModsField.GetValue(null);
            }
        }

        [TearDown]
        public void TearDown()
        {
            var runningModsField = AccessTools.Field(typeof(LoadedModManager), "runningMods");
            runningModsField?.SetValue(null, originalRunningMods);

            ModContentPack_ReloadContentInt_Patch.loadedMods.Clear();
            reloadedMods.Clear();
            mockReloadShouldThrow = false;
            mockIsOverBudget = false;
            // 靜態旗標恢復為執行期預設值，避免洩漏到其他 fixture。
            EarlyModContentLoader.ModClassesCreated = false;
        }

        private static ModContentPack CreateMockModContentPack(string packageId = "test.mod")
        {
            var mod = (ModContentPack)FormatterServices.GetUninitializedObject(typeof(ModContentPack));
            var packageIdField = AccessTools.Field(typeof(ModContentPack), "packageIdInt");
            packageIdField?.SetValue(mod, packageId);
            return mod;
        }

        private static void SetRunningMods(List<ModContentPack> mods)
        {
            var runningModsField = AccessTools.Field(typeof(LoadedModManager), "runningMods");
            runningModsField?.SetValue(null, mods);
        }

        [Test]
        public void EarlyLoadingComplete_InitialIsFalse_WhenCompletedIsTrue_AndSubsequentUpdateReturnsImmediately()
        {
            // 也守住「提早載入不等原版 ReloadContentInt 開始」：原版尚未載入任何 mod，第一幀就要把佇列載完，
            // 否則正式流程會先吃光整個佇列。
            var mod1 = CreateMockModContentPack("test.mod1");
            var mod2 = CreateMockModContentPack("test.mod2");
            SetRunningMods(new List<ModContentPack> { mod1, mod2 });

            Assert.That(loader.EarlyLoadingComplete, Is.False);

            loader.Update(delayedActions);

            Assert.That(loader.EarlyLoadingComplete, Is.True);
            Assert.That(reloadedMods.Count, Is.EqualTo(2));
            Assert.That(ModContentPack_ReloadContentInt_Patch.loadedMods.Contains(mod1), Is.True);
            Assert.That(ModContentPack_ReloadContentInt_Patch.loadedMods.Contains(mod2), Is.True);

            // Subsequent update should return immediately without executing anything
            loader.Update(delayedActions);
            Assert.That(reloadedMods.Count, Is.EqualTo(2));
        }

        [Test]
        public void Update_AfterPlayDataLoaded_CompletesWithoutReloadingMods()
        {
            // 切換語言時 LanguageReloading 會清空 loadedMods；此時 RunningMods 仍是即將被 ClearAllPlayData
            // 銷毀的舊 ModContentPack。若再提早載入，會把所有內容重載一次（大量 duplicate 警告並洩漏貼圖），
            // 還會與事件緒的 ClearDestroy 同時存取同一份內容字典。
            var mod = CreateMockModContentPack("test.mod.already.loaded");
            SetRunningMods(new List<ModContentPack> { mod });
            var loadedField = AccessTools.Field(typeof(PlayDataLoader), "loadedInt");
            loadedField.SetValue(null, true);
            try
            {
                loader.Update(delayedActions);

                Assert.That(reloadedMods, Is.Empty);
                Assert.That(loader.EarlyLoadingComplete, Is.True);
            }
            finally
            {
                loadedField.SetValue(null, false);
            }
        }

        [Test]
        public void Update_BeforeModClassesCreated_DoesNotLoadAnything()
        {
            // Mod 建構子在事件緒執行期間，主執行緒已開始每幀呼叫 Update；此時 FGL 與其他 mod 的
            // Harmony patch 都還沒套用，提早載入的內容會完全繞過它們。
            EarlyModContentLoader.ModClassesCreated = false;
            var mod = CreateMockModContentPack("test.mod");
            SetRunningMods(new List<ModContentPack> { mod });

            loader.Update(delayedActions);

            Assert.That(loader.EarlyLoadingComplete, Is.False);
            Assert.That(reloadedMods, Is.Empty);

            LoadedModManager_LoadModXML_Patch.Prefix();
            loader.Update(delayedActions);

            Assert.That(loader.EarlyLoadingComplete, Is.True);
            Assert.That(reloadedMods, Is.EquivalentTo(new[] { mod }));
        }

        [Test]
        public void LoadModXML_PatchTargetExists()
        {
            // 上面的測試直接呼叫 Prefix；這裡確認 [HarmonyPatch] 的目標在目前的遊戲版本仍存在，
            // 否則閘門永遠不會開、提早載入整個失效。
            Assert.That(AccessTools.Method(typeof(LoadedModManager), "LoadModXML"), Is.Not.Null);
        }

        [Test]
        public void Update_WhenSettingDisabled_ReturnsImmediately()
        {
            FasterGameLoadingSettings.earlyModContentLoading = false;
            var mod = CreateMockModContentPack("test.mod");
            SetRunningMods(new List<ModContentPack> { mod });

            loader.Update(delayedActions);

            Assert.That(loader.EarlyLoadingComplete, Is.False);
            Assert.That(reloadedMods, Is.Empty);
            Assert.That(ModContentPack_ReloadContentInt_Patch.loadedMods, Is.Empty);
        }

        [Test]
        public void Update_WhenSkipFramesGreaterThanZero_DecrementsAndYieldsFrame()
        {
            var mod1 = CreateMockModContentPack("test.mod1");
            var mod2 = CreateMockModContentPack("test.mod2");
            SetRunningMods(new List<ModContentPack> { mod1, mod2 });

            // Set skipFrames to 2 via reflection
            var skipFramesField = AccessTools.Field(typeof(EarlyModContentLoader), "skipFrames");
            skipFramesField?.SetValue(loader, 2);

            // Frame 1: skipFrames 2 -> 1, no mod processed
            loader.Update(delayedActions);
            Assert.That((int)skipFramesField.GetValue(loader), Is.EqualTo(1));
            Assert.That(reloadedMods, Is.Empty);

            // Frame 2: skipFrames 1 -> 0, no mod processed
            loader.Update(delayedActions);
            Assert.That((int)skipFramesField.GetValue(loader), Is.EqualTo(0));
            Assert.That(reloadedMods, Is.Empty);

            // Frame 3: skipFrames is 0, normal processing runs
            loader.Update(delayedActions);
            Assert.That(reloadedMods.Count, Is.EqualTo(2));
            Assert.That(loader.EarlyLoadingComplete, Is.True);
        }

        [Test]
        public void Update_WhenConsecutiveTimeoutsReachesThreshold_SetsSkipFramesToFive()
        {
            var mods = new List<ModContentPack>();
            for (int i = 0; i < 5; i++)
            {
                mods.Add(CreateMockModContentPack($"test.mod{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
            }
            SetRunningMods(mods);

            var skipFramesField = AccessTools.Field(typeof(EarlyModContentLoader), "skipFrames");
            var consecutiveTimeoutsField = AccessTools.Field(typeof(EarlyModContentLoader), "consecutiveTimeouts");

            mockIsOverBudget = true;

            // Call 1: process 1 mod, over budget -> consecutiveTimeouts = 1
            loader.Update(delayedActions);
            Assert.That(reloadedMods.Count, Is.EqualTo(1));
            Assert.That((int)consecutiveTimeoutsField.GetValue(loader), Is.EqualTo(1));
            Assert.That((int)skipFramesField.GetValue(loader), Is.EqualTo(0));

            // Call 2: process 1 mod, over budget -> consecutiveTimeouts = 2
            loader.Update(delayedActions);
            Assert.That(reloadedMods.Count, Is.EqualTo(2));
            Assert.That((int)consecutiveTimeoutsField.GetValue(loader), Is.EqualTo(2));
            Assert.That((int)skipFramesField.GetValue(loader), Is.EqualTo(0));

            // Call 3: process 1 mod, over budget -> consecutiveTimeouts = 3 -> triggers skipFrames = 5, consecutiveTimeouts = 0
            loader.Update(delayedActions);
            Assert.That(reloadedMods.Count, Is.EqualTo(3));
            Assert.That((int)consecutiveTimeoutsField.GetValue(loader), Is.EqualTo(0));
            Assert.That((int)skipFramesField.GetValue(loader), Is.EqualTo(5));

            // Call 4: skipFrames 5 -> 4, no mods processed
            loader.Update(delayedActions);
            Assert.That(reloadedMods.Count, Is.EqualTo(3));
            Assert.That((int)skipFramesField.GetValue(loader), Is.EqualTo(4));
        }

        [Test]
        public void Update_WhenReloadContentIntThrows_DoesNotAddToLoadedModsAndAllowsRetry()
        {
            var mod = CreateMockModContentPack("test.failing.mod");
            SetRunningMods(new List<ModContentPack> { mod });

            mockReloadShouldThrow = true;

            // Should catch exception and not add to loadedMods
            Assert.DoesNotThrow(() => loader.Update(delayedActions));
            Assert.That(ModContentPack_ReloadContentInt_Patch.loadedMods.Contains(mod), Is.False);

            // 失敗的 mod 沒被標記為已載入，之後的載入流程（這裡以新的載入器代表）仍會重試並成功
            mockReloadShouldThrow = false;
            loader = new EarlyModContentLoader();
            loader.Update(delayedActions);

            Assert.That(ModContentPack_ReloadContentInt_Patch.loadedMods.Contains(mod), Is.True);
            Assert.That(loader.EarlyLoadingComplete, Is.True);
        }

        [Test]
        public void Update_WhenImageOptInstalled_ProcessesViaSyncScope()
        {
            // 模擬 ImageOpt 已安裝：Update 會走 sync scope 分支（L95-100）
            var started = false;
            ImageOptEarlyLoadCoordinator.ConfigureForTests(() => started, value => started = value, enabled: true);
            try
            {
                var mod = CreateMockModContentPack("test.mod");
                SetRunningMods(new List<ModContentPack> { mod });

                loader.Update(delayedActions);

                Assert.That(reloadedMods, Does.Contain(mod));
                Assert.That(ModContentPack_ReloadContentInt_Patch.loadedMods.Contains(mod), Is.True);
                Assert.That(loader.EarlyLoadingComplete, Is.True);
            }
            finally
            {
                ImageOptEarlyLoadCoordinator.ResetTestConfiguration();
            }
        }

        [Test]
        public void InvokeReloadContentInt_WhenTargetThrows_UnwrapsException()
        {
            var invokeMethod = AccessTools.Method(typeof(EarlyModContentLoader), "InvokeReloadContentInt");
            mockReloadShouldThrow = true;
            try
            {
                var ex = Assert.Throws<TargetInvocationException>(() =>
                    invokeMethod.Invoke(null, new object[] { CreateMockModContentPack("test.mod") }));
                Assert.That(ex.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That(ex.InnerException.Message, Does.Contain("Simulated ReloadContentInt failure"));
            }
            finally
            {
                mockReloadShouldThrow = false;
            }
        }

        [Test]
        public void LoadOneModContent_WhenInvokeThrows_CatchesAndDoesNotAddToLoadedMods()
        {
            var mod = CreateMockModContentPack("test.mod");
            SetRunningMods(new List<ModContentPack> { mod });
            mockReloadShouldThrow = true;
            try
            {
                loader.Update(delayedActions);
                Assert.That(ModContentPack_ReloadContentInt_Patch.loadedMods.Contains(mod), Is.False);
            }
            finally
            {
                mockReloadShouldThrow = false;
            }
        }

        [Test]
        public void Update_WhenModAddedToLoadedModsMidQueue_SkipsLoading()
        {
            var mod1 = CreateMockModContentPack("test.mod.loaded1");
            var mod2 = CreateMockModContentPack("test.mod.loaded2");
            SetRunningMods(new List<ModContentPack> { mod1, mod2 });

            reloadedMods.Clear();
            var field = AccessTools.Field(typeof(EarlyModContentLoader), "pendingEarlyLoads");
            var queue = new Queue<ModContentPack>();
            queue.Enqueue(mod1);
            queue.Enqueue(mod2);
            field.SetValue(loader, queue);

            ModContentPack_ReloadContentInt_Patch.loadedMods.Add(mod2);

            loader.Update(delayedActions);

            Assert.That(reloadedMods, Does.Contain(mod1));
            Assert.That(reloadedMods, Does.Not.Contain(mod2));
        }
    }
}
