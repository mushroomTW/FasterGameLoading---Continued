using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace FasterGameLoading.Tests.EarlyModContentLoading
{
    [TestFixture]
    public class TypeLookupCacheTests
    {
        [TearDown]
        public void TearDown()
        {
            TypeLookupCache.Invalidate();
        }

        private static void FillAllNameCaches()
        {
            GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults["Some.Type"] = typeof(string);
            GenTypes_GetTypeInAnyAssemblyInt_Patch.loadedTypesThisSession["Type"] = "Some.Type";
            AccessTools_TypeByName_Patch.cachedResults["Type"] = typeof(string);
            GenTypes_AllLeafSubclasses_Patch.keyValuePairs[typeof(object)] = new HashSet<Type> { typeof(string) };
        }

        private static void AssertAllNameCachesEmpty()
        {
            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults, Is.Empty);
            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.loadedTypesThisSession, Is.Empty);
            Assert.That(AccessTools_TypeByName_Patch.cachedResults, Is.Empty);
            Assert.That(GenTypes_AllLeafSubclasses_Patch.keyValuePairs, Is.Empty);
        }

        [Test]
        public void VanillaGenTypesClearCache_InvalidatesEveryNameCache()
        {
            FillAllNameCaches();

            GenTypes_ClearCache_Patch.Postfix();

            AssertAllNameCachesEmpty();
        }

        [Test]
        public void LanguageReloading_InvalidatesEveryNameCache()
        {
            FillAllNameCaches();

            SessionLifecycle.Raise(LifecyclePhase.LanguageReloading);

            AssertAllNameCachesEmpty();
        }

        [Test]
        public void StartupCompleted_FirstRoundReplacesThenReloadRoundsMerge()
        {
            var original = TypeLookupCache.FullNamesFromLastSession;
            var originalSettled = TypeLookupCache.SessionMappingSettled;
            try
            {
                TypeLookupCache.SessionMappingSettled = false;
                TypeLookupCache.FullNamesFromLastSession = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Stale"] = "Last.Session",
                };
                GenTypes_GetTypeInAnyAssemblyInt_Patch.loadedTypesThisSession["First"] = "Round.One";
                SessionLifecycle.Raise(LifecyclePhase.StartupCompleted);
                Assert.That(TypeLookupCache.FullNamesFromLastSession.ContainsKey("Stale"), Is.False, "第一輪整份取代，上次 session 沒用到的名稱要丟掉。");
                Assert.That(TypeLookupCache.FullNamesFromLastSession["First"], Is.EqualTo("Round.One"));

                // 語言重載不會重跑 Mod 建構子與提早載入：第一輪才查過的名稱必須保留，重載輪新查到的併入。
                SessionLifecycle.Raise(LifecyclePhase.LanguageReloading);
                GenTypes_GetTypeInAnyAssemblyInt_Patch.loadedTypesThisSession["Second"] = "Round.Two";
                SessionLifecycle.Raise(LifecyclePhase.StartupCompleted);

                Assert.That(TypeLookupCache.FullNamesFromLastSession["First"], Is.EqualTo("Round.One"));
                Assert.That(TypeLookupCache.FullNamesFromLastSession["Second"], Is.EqualTo("Round.Two"));
            }
            finally
            {
                TypeLookupCache.FullNamesFromLastSession = original;
                TypeLookupCache.SessionMappingSettled = originalSettled;
            }
        }
    }
}
