using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;
using Verse;

namespace FasterGameLoading.Tests.EarlyModContentLoading
{
    [TestFixture]
    public class ModContentPack_ReloadContentInt_PatchTests
    {
        private static Harmony harmony;
        private static bool drainCalled;

        private static bool MockDrain()
        {
            drainCalled = true;
            return false;
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            harmony = new Harmony("FasterGameLoading.Tests.ModContentPack_ReloadContentInt_PatchTests");

            // 注意：UnityData.IsInMainThread 已由 TestSetup(GlobalSetup) 全域 stub（Prefix_TrueStub 先執行），
            // 測試需透過 TestSetup.IsInMainThreadOverride 控制其回傳值，而非 patch getter。
            var drainMethod = AccessTools.Method(typeof(MainThreadTextureLoader), nameof(MainThreadTextureLoader.Drain));
            // 找不到就直接失敗：否則替身沒裝上、真正的 Drain 照跑，測試會因錯誤的原因失敗或通過。
            Assert.That(drainMethod, Is.Not.Null, "MainThreadTextureLoader.Drain 不存在，無法替換成測試替身。");
            harmony.Patch(drainMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(ModContentPack_ReloadContentInt_PatchTests), nameof(MockDrain))));
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            TestSetup.IsInMainThreadOverride = null;
            harmony?.UnpatchAll("FasterGameLoading.Tests.ModContentPack_ReloadContentInt_PatchTests");
        }

        [SetUp]
        public void SetUp()
        {
            ModContentPack_ReloadContentInt_Patch.loadedMods.Clear();
            TestSetup.IsInMainThreadOverride = () => true;
            drainCalled = false;
        }

        [TearDown]
        public void TearDown()
        {
            ModContentPack_ReloadContentInt_Patch.loadedMods.Clear();
            TestSetup.IsInMainThreadOverride = null;
            drainCalled = false;
        }

        private static ModContentPack CreateMockMod()
        {
            return (ModContentPack)FormatterServices.GetUninitializedObject(typeof(ModContentPack));
        }

        [Test]
        public void Prefix_WhenInMainThread_DrainsMainThreadTextureLoader()
        {
            TestSetup.IsInMainThreadOverride = () => true;
            drainCalled = false;
            var mod = CreateMockMod();

            bool shouldRun = ModContentPack_ReloadContentInt_Patch.Prefix(mod);

            Assert.That(drainCalled, Is.True);
            Assert.That(shouldRun, Is.True);
        }

        [Test]
        public void Prefix_WhenNotInMainThread_DoesNotDrainMainThreadTextureLoader()
        {
            TestSetup.IsInMainThreadOverride = () => false;
            drainCalled = false;
            var mod = CreateMockMod();

            bool shouldRun = ModContentPack_ReloadContentInt_Patch.Prefix(mod);

            Assert.That(drainCalled, Is.False);
            Assert.That(shouldRun, Is.True);
        }

        [Test]
        public void Prefix_WhenModNotLoaded_ReturnsTrue_AndWhenLoaded_ReturnsFalse()
        {
            var mod = CreateMockMod();

            // Unloaded mod -> returns true
            bool firstCheck = ModContentPack_ReloadContentInt_Patch.Prefix(mod);
            Assert.That(firstCheck, Is.True);

            // Postfix records loaded mod
            ModContentPack_ReloadContentInt_Patch.Postfix(mod);

            // Loaded mod -> returns false
            bool secondCheck = ModContentPack_ReloadContentInt_Patch.Prefix(mod);
            Assert.That(secondCheck, Is.False);
        }

        [Test]
        public void Postfix_RecordsTheContentPackForFutureCalls()
        {
            var mod = CreateMockMod();
            ModContentPack_ReloadContentInt_Patch.Postfix(mod);

            Assert.That(ModContentPack_ReloadContentInt_Patch.loadedMods.Contains(mod), Is.True);
        }

        [Test]
        public void LanguageReloading_ClearsLoadedMods()
        {
            var mod = CreateMockMod();
            ModContentPack_ReloadContentInt_Patch.Postfix(mod);
            Assert.That(ModContentPack_ReloadContentInt_Patch.loadedMods.Contains(mod), Is.True);

            SessionLifecycle.Raise(LifecyclePhase.LanguageReloading);

            Assert.That(ModContentPack_ReloadContentInt_Patch.loadedMods, Is.Empty);
        }
    }
}
