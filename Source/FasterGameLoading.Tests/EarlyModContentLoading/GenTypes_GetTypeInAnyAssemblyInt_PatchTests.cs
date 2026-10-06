using System;
using NUnit.Framework;

namespace FasterGameLoading.Tests.EarlyModContentLoading
{
    [TestFixture]
    public class GenTypes_GetTypeInAnyAssemblyInt_PatchTests
    {
        [SetUp]
        public void SetUp()
        {
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
                Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.Prepare(), Is.True);

                FasterGameLoadingSettings.TypeLookupCache = false;
                Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.Prepare(), Is.False);
            }
            finally
            {
                FasterGameLoadingSettings.TypeLookupCache = previous;
            }
        }

        [TearDown]
        public void TearDown()
        {
            GenTypes_GetTypeInAnyAssemblyInt_Patch.ClearCache();
            TypeLookupCache.FullNamesFromLastSession.Clear();
        }

        [Test]
        public void MakeCacheKey_FormatsStringCorrectly()
        {
            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.MakeCacheKey(typeName: null, namespaceIfAmbiguous: null), Is.EqualTo(string.Empty));
            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.MakeCacheKey("MyType", namespaceIfAmbiguous: null), Is.EqualTo("MyType"));
            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.MakeCacheKey("MyType", string.Empty), Is.EqualTo("MyType"));
            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.MakeCacheKey("MyType", "Verse"), Is.EqualTo("MyType|ns|Verse"));
        }

        [Test]
        public void Prefix_WhenCachedResultsHit_SetsResultAndStateAndReturnsFalse()
        {
            GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults["TestType"] = typeof(string);

            Type result = null;
            string typeName = "TestType";

            bool shouldRunOriginal = GenTypes_GetTypeInAnyAssemblyInt_Patch.Prefix(
                ref result,
                out var state,
                ref typeName,
                namespaceIfAmbiguous: null);

            Assert.That(shouldRunOriginal, Is.False);
            Assert.That(result, Is.EqualTo(typeof(string)));
            Assert.That(state.isCached, Is.True);
            Assert.That(state.originalTypeName, Is.EqualTo("TestType"));
            Assert.That(state.cacheKey, Is.EqualTo("TestType"));
        }

        [Test]
        public void Prefix_WithNamespaceHint_ServesExactFullNameEntry()
        {
            // 模擬 FullName 預熱的結果。XML 的 Class= 屬性都會帶命名空間提示（例如 "Verse"）。
            GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults["Verse.ThingDef"] = typeof(Verse.ThingDef);

            Type result = null;
            string typeName = "Verse.ThingDef";
            bool shouldRunOriginal = GenTypes_GetTypeInAnyAssemblyInt_Patch.Prefix(ref result, out var state, ref typeName, "Verse");

            // 原版第一步就是不看命名空間、以原名查詢；查得到的完整名稱與提示無關，應直接命中。
            Assert.That(shouldRunOriginal, Is.False);
            Assert.That(result, Is.EqualTo(typeof(Verse.ThingDef)));
            Assert.That(state.isCached, Is.True);
        }

        [Test]
        public void Prefix_WithNamespaceHint_DoesNotServeEntryResolvedByNamespaceProbing()
        {
            // "AI.JobDriver_Wait" 是在命名空間探測後才解析出 Verse.AI.JobDriver_Wait；
            // 換成別的提示時原版會先試該命名空間，結果可能不同，因此不得沿用。
            GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults["AI.JobDriver_Wait"] = typeof(Verse.AI.JobDriver_Wait);

            Type result = null;
            string typeName = "AI.JobDriver_Wait";
            bool shouldRunOriginal = GenTypes_GetTypeInAnyAssemblyInt_Patch.Prefix(ref result, out _, ref typeName, "RimWorld");

            Assert.That(shouldRunOriginal, Is.True);
            Assert.That(result, Is.Null);
        }

        [Test]
        public void WarmupFullNames_IndexesOnlyGivenAssembliesAndKeepsFirstEntry()
        {
            GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults[typeof(SessionLifecycle).FullName] = typeof(string);

            GenTypes_GetTypeInAnyAssemblyInt_Patch.WarmupFullNames(new[] { typeof(SessionCache).Assembly });

            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults[typeof(SessionCache).FullName], Is.EqualTo(typeof(SessionCache)));
            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults[typeof(SessionLifecycle).FullName], Is.EqualTo(typeof(string)),
                "先登記者優先：搜尋順序較前的組件中的同名型別不得被覆寫。");
            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults.ContainsKey(typeof(Verse.ThingDef).FullName), Is.False);
        }

        [Test]
        public void WarmupFullNames_CrossAssemblyCaseCollision_FallsBackToVanillaFirstMatch()
        {
            var first = DefineTypes("FglCaseFirst", "AuditCase.widget", "AuditCase.Other");
            var second = DefineTypes("FglCaseSecond", "AuditCase.Widget");

            GenTypes_GetTypeInAnyAssemblyInt_Patch.WarmupFullNames(new[] { first, second });

            Type result = null;
            string typeName = "AuditCase.Widget";
            bool shouldRunOriginal = GenTypes_GetTypeInAnyAssemblyInt_Patch.Prefix(ref result, out _, ref typeName, "Verse");

            // 原版依組件順序以 ignoreCase: true 查詢，會先找到較前組件的 AuditCase.widget。
            Assert.That(VanillaIgnoreCaseSearch(typeName, first, second).Assembly.GetName().Name, Is.EqualTo("FglCaseFirst"));
            Assert.That(shouldRunOriginal, Is.True, "大小寫碰撞的名稱必須交回原版解析，不得回傳大小寫完全相符的較後組件型別。");
            Assert.That(result, Is.Null);
            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults.ContainsKey("AuditCase.widget"), Is.False);
            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults["AuditCase.Other"], Is.EqualTo(first.GetType("AuditCase.Other")),
                "沒有碰撞的名稱仍應預熱。");
        }

        [Test]
        public void WarmupFullNames_SameAssemblyCaseAmbiguity_IsNotWarmed()
        {
            var assembly = DefineTypes("FglCaseSame", "Amb.Case", "Amb.case");

            GenTypes_GetTypeInAnyAssemblyInt_Patch.WarmupFullNames(new[] { assembly });

            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults.ContainsKey("Amb.Case"), Is.False);
            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults.ContainsKey("Amb.case"), Is.False);
        }

        [Test]
        public void WarmupFullNames_ExactDuplicateAcrossAssemblies_KeepsFirstLikeVanilla()
        {
            var first = DefineTypes("FglDupFirst", "Dup.Same");
            var second = DefineTypes("FglDupSecond", "Dup.Same");

            GenTypes_GetTypeInAnyAssemblyInt_Patch.WarmupFullNames(new[] { first, second });

            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults["Dup.Same"], Is.EqualTo(VanillaIgnoreCaseSearch("Dup.Same", first, second)));
            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults["Dup.Same"].Assembly.GetName().Name, Is.EqualTo("FglDupFirst"));
        }

        private static System.Reflection.Assembly DefineTypes(string assemblyName, params string[] typeNames)
        {
            var assembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
                new System.Reflection.AssemblyName(assemblyName), System.Reflection.Emit.AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule(assemblyName);
            foreach (var typeName in typeNames)
            {
                module.DefineType(typeName, System.Reflection.TypeAttributes.Public).CreateType();
            }
            return assembly;
        }

        /// <summary>與原版 GenTypes.GetTypeInAnyAssemblyRaw 相同：依組件順序以 ignoreCase: true 查詢，先找到者勝出。</summary>
        private static Type VanillaIgnoreCaseSearch(string typeName, params System.Reflection.Assembly[] assembliesInSearchOrder)
        {
            foreach (var assembly in assembliesInSearchOrder)
            {
                var type = assembly.GetType(typeName, throwOnError: false, ignoreCase: true);
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }

        [Test]
        public void GenTypesSearchAssemblies_ListsGameAssemblyThenRunningModsInLoadOrder()
        {
            var runningModsField = HarmonyLib.AccessTools.Field(typeof(Verse.LoadedModManager), "runningMods");
            var originalRunningMods = runningModsField.GetValue(null);
            var firstModAssembly = typeof(NUnit.Framework.TestAttribute).Assembly;
            var secondModAssembly = typeof(GenTypes_GetTypeInAnyAssemblyInt_PatchTests).Assembly;
            try
            {
                runningModsField.SetValue(null, new System.Collections.Generic.List<Verse.ModContentPack>
                {
                    CreateModWithAssemblies(firstModAssembly),
                    CreateModWithAssemblies(secondModAssembly),
                });

                Assert.That(TypeLookupCache.SearchAssemblies(),
                    Is.EqualTo(new[] { typeof(Verse.GenTypes).Assembly, firstModAssembly, secondModAssembly }));
            }
            finally
            {
                runningModsField.SetValue(null, originalRunningMods);
            }
        }

        private static Verse.ModContentPack CreateModWithAssemblies(params System.Reflection.Assembly[] assemblies)
        {
            var handler = (Verse.ModAssemblyHandler)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Verse.ModAssemblyHandler));
            HarmonyLib.AccessTools.Field(typeof(Verse.ModAssemblyHandler), "loadedAssemblies")
                .SetValue(handler, new System.Collections.Generic.List<System.Reflection.Assembly>(assemblies));
            var mod = (Verse.ModContentPack)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Verse.ModContentPack));
            HarmonyLib.AccessTools.Field(typeof(Verse.ModContentPack), "assemblies").SetValue(mod, handler);
            return mod;
        }

        [Test]
        public void Prefix_WhenCachedResultsMiss_AndSessionCacheHit_UpdatesTypeNameToFullNameAndReturnsTrue()
        {
            TypeLookupCache.FullNamesFromLastSession["ShortType"] = "System.Text.StringBuilder";

            Type result = null;
            string typeName = "ShortType";

            bool shouldRunOriginal = GenTypes_GetTypeInAnyAssemblyInt_Patch.Prefix(
                ref result,
                out var state,
                ref typeName,
                namespaceIfAmbiguous: null);

            Assert.That(shouldRunOriginal, Is.True);
            Assert.That(result, Is.Null);
            Assert.That(typeName, Is.EqualTo("System.Text.StringBuilder"));
            Assert.That(state.isCached, Is.False);
            Assert.That(state.originalTypeName, Is.EqualTo("ShortType"));
            Assert.That(state.usedSessionMapping, Is.True);
        }

        [Test]
        public void Postfix_WhenSessionMappingIsStale_DropsMappingAndResolvesOriginalName()
        {
            // 上次 session 記錄 ThingDef → 一個已不存在的全名（例如 mod 更新後類別改名）。
            TypeLookupCache.FullNamesFromLastSession["ThingDef"] = "Removed.Namespace.ThingDef";
            var state = (originalTypeName: "ThingDef", namespaceIfAmbiguous: null as string, cacheKey: "ThingDef", isCached: false, usedSessionMapping: true);

            Type result = null;
            GenTypes_GetTypeInAnyAssemblyInt_Patch.Postfix(ref result, state);

            Assert.That(result, Is.EqualTo(typeof(Verse.ThingDef)), "舊對照查不到時必須退回以原名解析，而不是回報找不到型別。");
            Assert.That(TypeLookupCache.FullNamesFromLastSession.ContainsKey("ThingDef"), Is.False);
        }

        [Test]
        public void Postfix_WhenOriginalLookupMissesWithoutSessionMapping_DoesNotRetry()
        {
            TypeLookupCache.FullNamesFromLastSession["Unrelated"] = "Some.Unrelated";
            var state = (originalTypeName: "ThingDef", namespaceIfAmbiguous: null as string, cacheKey: "ThingDef", isCached: false, usedSessionMapping: false);

            Type result = null;
            GenTypes_GetTypeInAnyAssemblyInt_Patch.Postfix(ref result, state);

            Assert.That(result, Is.Null);
            Assert.That(TypeLookupCache.FullNamesFromLastSession.ContainsKey("Unrelated"), Is.True);
        }

        [Test]
        public void Prefix_WhenCachedResultsMiss_AndSessionCacheHitWithNamespace_UpdatesTypeName()
        {
            var key = GenTypes_GetTypeInAnyAssemblyInt_Patch.MakeCacheKey("MyType", "Verse");
            TypeLookupCache.FullNamesFromLastSession[key] = "Verse.MyType";

            Type result = null;
            string typeName = "MyType";

            bool shouldRunOriginal = GenTypes_GetTypeInAnyAssemblyInt_Patch.Prefix(
                ref result,
                out var state,
                ref typeName,
                "Verse");

            Assert.That(shouldRunOriginal, Is.True);
            Assert.That(typeName, Is.EqualTo("Verse.MyType"));
            Assert.That(state.isCached, Is.False);
        }

        [Test]
        public void Postfix_WhenNotCached_WritesToCachedResultsAndLoadedTypesThisSession()
        {
            var state = (originalTypeName: "Int32", namespaceIfAmbiguous: null as string, cacheKey: "Int32", isCached: false, usedSessionMapping: false);

            var result = typeof(int);
            GenTypes_GetTypeInAnyAssemblyInt_Patch.Postfix(ref result, state);

            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults.TryGetValue("Int32", out var cachedType), Is.True);
            Assert.That(cachedType, Is.EqualTo(typeof(int)));

            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults.TryGetValue("System.Int32", out var cachedFullNameType), Is.True);
            Assert.That(cachedFullNameType, Is.EqualTo(typeof(int)));

            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.loadedTypesThisSession.TryGetValue("Int32", out var recordedFullName), Is.True);
            Assert.That(recordedFullName, Is.EqualTo("System.Int32"));
        }

        [Test]
        public void Postfix_WhenNotCached_AndNamespaceProvided_WritesNamespaceSpecificKeyToCachedResults()
        {
            var state = (originalTypeName: "Int32", namespaceIfAmbiguous: "System", cacheKey: "Int32|ns|System", isCached: false, usedSessionMapping: false);

            var result = typeof(int);
            GenTypes_GetTypeInAnyAssemblyInt_Patch.Postfix(ref result, state);

            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults.TryGetValue("Int32|ns|System", out var cachedType), Is.True);
            Assert.That(cachedType, Is.EqualTo(typeof(int)));

            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults.TryGetValue("System.Int32|ns|System", out var cachedFullNameNs), Is.True);
            Assert.That(cachedFullNameNs, Is.EqualTo(typeof(int)));
        }

        [Test]
        public void Postfix_WhenAlreadyCached_DoesNotModifyCache()
        {
            var state = (originalTypeName: "Int32", namespaceIfAmbiguous: null as string, cacheKey: "Int32", isCached: true, usedSessionMapping: false);

            var result = typeof(int);
            GenTypes_GetTypeInAnyAssemblyInt_Patch.Postfix(ref result, state);

            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults.ContainsKey("Int32"), Is.False);
            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.loadedTypesThisSession.TryGetValue("Int32", out var recordedFullName), Is.True);
            Assert.That(recordedFullName, Is.EqualTo("System.Int32"));
        }

        [Test]
        public void Postfix_WhenResultIsNull_DoesNothing()
        {
            var state = (originalTypeName: "NonExistentType", namespaceIfAmbiguous: null as string, cacheKey: "NonExistentType", isCached: false, usedSessionMapping: false);

            Type result = null;
            Assert.DoesNotThrow(() => GenTypes_GetTypeInAnyAssemblyInt_Patch.Postfix(ref result, state));
            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults, Is.Empty);
            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.loadedTypesThisSession, Is.Empty);
        }

        [Test]
        public void ClearCache_RemovesAllEntries()
        {
            GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults["Test"] = typeof(string);
            GenTypes_GetTypeInAnyAssemblyInt_Patch.loadedTypesThisSession["Test"] = "System.String";

            GenTypes_GetTypeInAnyAssemblyInt_Patch.ClearCache();

            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults, Is.Empty);
            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.loadedTypesThisSession, Is.Empty);
        }

        [Test]
        public void LanguageReloading_ClearsCache()
        {
            GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults["Test"] = typeof(string);
            GenTypes_GetTypeInAnyAssemblyInt_Patch.loadedTypesThisSession["Test"] = "System.String";

            SessionLifecycle.Raise(LifecyclePhase.LanguageReloading);

            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults, Is.Empty);
            Assert.That(GenTypes_GetTypeInAnyAssemblyInt_Patch.loadedTypesThisSession, Is.Empty);
        }
    }
}
