using System.Runtime.Serialization;
using NUnit.Framework;
using Verse;

namespace FasterGameLoading.Tests.EarlyModContentLoading
{
    [TestFixture]
    public class ModAssetBundlesHandler_ReloadAll_PatchTests
    {
        private static ModAssetBundlesHandler CreateMockHandler()
        {
            return (ModAssetBundlesHandler)FormatterServices.GetUninitializedObject(typeof(ModAssetBundlesHandler));
        }

        [SetUp]
        public void SetUp()
        {
            ModAssetBundlesHandler_ReloadAll_Patch.reloadedHandlers.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            ModAssetBundlesHandler_ReloadAll_Patch.reloadedHandlers.Clear();
        }

        [Test]
        public void Prefix_OnFirstCall_ReturnsTrue()
        {
            var handler = CreateMockHandler();

            bool shouldRunOriginal = ModAssetBundlesHandler_ReloadAll_Patch.Prefix(handler);

            Assert.That(shouldRunOriginal, Is.True);
        }

        [Test]
        public void Postfix_AddsHandlerToReloadedHandlers()
        {
            var handler = CreateMockHandler();

            ModAssetBundlesHandler_ReloadAll_Patch.Postfix(handler);

            Assert.That(ModAssetBundlesHandler_ReloadAll_Patch.reloadedHandlers.Contains(handler), Is.True);
        }

        [Test]
        public void Prefix_OnSubsequentCallForSameHandler_ReturnsFalse()
        {
            var handler = CreateMockHandler();

            // First call: allows original
            bool firstCall = ModAssetBundlesHandler_ReloadAll_Patch.Prefix(handler);
            Assert.That(firstCall, Is.True);

            // Postfix marks it as reloaded
            ModAssetBundlesHandler_ReloadAll_Patch.Postfix(handler);

            // Second call: intercepts duplicate load
            bool secondCall = ModAssetBundlesHandler_ReloadAll_Patch.Prefix(handler);
            Assert.That(secondCall, Is.False);
        }

        [Test]
        public void LanguageReloading_ClearsReloadedHandlers()
        {
            var handler = CreateMockHandler();
            ModAssetBundlesHandler_ReloadAll_Patch.reloadedHandlers.Add(handler);

            SessionLifecycle.Raise(LifecyclePhase.LanguageReloading);

            Assert.That(ModAssetBundlesHandler_ReloadAll_Patch.reloadedHandlers, Is.Empty);
        }
    }
}
