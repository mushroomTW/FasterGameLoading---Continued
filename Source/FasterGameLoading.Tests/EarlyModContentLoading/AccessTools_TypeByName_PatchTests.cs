using System;
using NUnit.Framework;

namespace FasterGameLoading.Tests.EarlyModContentLoading
{
    [TestFixture]
    public class AccessTools_TypeByName_PatchTests
    {
        [SetUp]
        public void SetUp()
        {
            AccessTools_TypeByName_Patch.cachedResults.Clear();
            GenTypes_GetTypeInAnyAssemblyInt_Patch.ClearCache();
            TypeLookupCache.FullNamesFromLastSession.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            AccessTools_TypeByName_Patch.cachedResults.Clear();
            GenTypes_GetTypeInAnyAssemblyInt_Patch.ClearCache();
            TypeLookupCache.FullNamesFromLastSession.Clear();
        }

        [Test]
        public void Prepare_ReflectsTypeLookupCacheSetting()
        {
            var previous = FasterGameLoadingSettings.TypeLookupCache;
            try
            {
                FasterGameLoadingSettings.TypeLookupCache = true;
                Assert.That(AccessTools_TypeByName_Patch.Prepare(), Is.True);

                FasterGameLoadingSettings.TypeLookupCache = false;
                Assert.That(AccessTools_TypeByName_Patch.Prepare(), Is.False);
            }
            finally
            {
                FasterGameLoadingSettings.TypeLookupCache = previous;
            }
        }

        [Test]
        public void Prefix_WhenNullOrEmptyName_RunsOriginal()
        {
            Type result = null;

            Assert.That(AccessTools_TypeByName_Patch.Prefix(ref result, name: null), Is.True);
            Assert.That(AccessTools_TypeByName_Patch.Prefix(ref result, string.Empty), Is.True);
            Assert.That(result, Is.Null);
        }

        [Test]
        public void Prefix_WhenCachedResultsHit_SetsResultAndReturnsFalse()
        {
            AccessTools_TypeByName_Patch.cachedResults["TestClass"] = typeof(string);

            Type result = null;
            bool shouldRun = AccessTools_TypeByName_Patch.Prefix(ref result, "TestClass");

            Assert.That(shouldRun, Is.False);
            Assert.That(result, Is.EqualTo(typeof(string)));
        }

        [Test]
        public void Prefix_IgnoresGenTypesCachesAndSessionMapping()
        {
            // GenTypes 的快取與跨 session 對照依 GenTypes 的解析規則產生，這裡不得沿用。
            GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults["ShortClass"] = typeof(string);
            TypeLookupCache.FullNamesFromLastSession["ShortClass"] = "System.Text.StringBuilder";

            Type result = null;
            bool shouldRun = AccessTools_TypeByName_Patch.Prefix(ref result, "ShortClass");

            Assert.That(shouldRun, Is.True);
            Assert.That(result, Is.Null);
        }

        [Test]
        public void Postfix_WhenOriginalResolvedType_CachesOnlyInOwnCache()
        {
            AccessTools_TypeByName_Patch.Postfix(typeof(string), "String", __runOriginal: true);

            Assert.That(AccessTools_TypeByName_Patch.cachedResults.TryGetValue("String", out var type), Is.True);
            Assert.That(type, Is.EqualTo(typeof(string)));
            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults, Is.Empty);
            Assert.That(TypeLookupCache.FullNamesFromLastSession, Is.Empty);
        }

        [Test]
        public void Postfix_WhenServedFromCache_DoesNotRewriteCache()
        {
            AccessTools_TypeByName_Patch.Postfix(typeof(string), "String", __runOriginal: false);

            Assert.That(AccessTools_TypeByName_Patch.cachedResults, Is.Empty);
        }

        [Test]
        public void Postfix_WhenResultIsNull_DoesNotCache()
        {
            AccessTools_TypeByName_Patch.Postfix(null, "NonExistentType", __runOriginal: true);

            Assert.That(AccessTools_TypeByName_Patch.cachedResults, Is.Empty);
        }

        [Test]
        public void LanguageReloading_ClearsCache()
        {
            AccessTools_TypeByName_Patch.cachedResults["Test"] = typeof(string);

            SessionLifecycle.Raise(LifecyclePhase.LanguageReloading);

            Assert.That(AccessTools_TypeByName_Patch.cachedResults, Is.Empty);
        }
    }
}
