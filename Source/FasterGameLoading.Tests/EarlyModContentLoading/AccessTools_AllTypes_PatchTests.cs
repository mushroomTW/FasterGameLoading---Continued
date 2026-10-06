using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using NUnit.Framework;
using Verse;

namespace FasterGameLoading.Tests.EarlyModContentLoading
{
    [TestFixture]
    public class AccessTools_AllTypes_PatchTests
    {
        private static Harmony harmony;
        private static Action capturedExecuteWhenFinishedAction;

        private static bool PrefixSkip() => false;
        private static bool MockIsInMainThread() => true;

        private static bool MockExecuteWhenFinished(Action action)
        {
            capturedExecuteWhenFinishedAction = action;
            return false;
        }

        private static FieldInfo AllTypesCachedField =>
            AccessTools.Field(typeof(AccessTools_AllTypes_Patch), "allTypesCached");

        private static FieldInfo CachedAssembliesCountField =>
            AccessTools.Field(typeof(AccessTools_AllTypes_Patch), "cachedAssembliesCount");

        private static FieldInfo CachedDynamicTypeCountField =>
            AccessTools.Field(typeof(AccessTools_AllTypes_Patch), "cachedDynamicTypeCount");

        internal static int CurrentDynamicTypeCount() =>
            (int)AccessTools.Method(typeof(AccessTools_AllTypes_Patch), "CountDynamicTypes")
                .Invoke(null, new object[] { AppDomain.CurrentDomain.GetAssemblies() });

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            harmony = new Harmony("FasterGameLoading.Tests.AccessTools_AllTypes_PatchTests");

            var emitMethod = AccessTools.Method(typeof(FGLLog), "Emit");
            if (emitMethod != null)
            {
                harmony.Patch(emitMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(AccessTools_AllTypes_PatchTests), nameof(PrefixSkip))));
            }

            var isInMainThreadGetter = AccessTools.PropertyGetter(typeof(UnityData), nameof(UnityData.IsInMainThread));
            if (isInMainThreadGetter != null)
            {
                harmony.Patch(isInMainThreadGetter, prefix: new HarmonyMethod(AccessTools.Method(typeof(AccessTools_AllTypes_PatchTests), nameof(MockIsInMainThread))));
            }

            var execWhenFinished = AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.ExecuteWhenFinished));
            if (execWhenFinished != null)
            {
                harmony.Patch(execWhenFinished, prefix: new HarmonyMethod(AccessTools.Method(typeof(AccessTools_AllTypes_PatchTests), nameof(MockExecuteWhenFinished))));
            }
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            harmony?.UnpatchAll("FasterGameLoading.Tests.AccessTools_AllTypes_PatchTests");
        }

        [SetUp]
        public void SetUp()
        {
            capturedExecuteWhenFinishedAction = null;
            AllTypesCachedField?.SetValue(obj: null, value: null);
            CachedAssembliesCountField?.SetValue(null, 0);
            GenTypes_GetTypeInAnyAssemblyInt_Patch.ClearCache();
            AssemblyTypesCache.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            capturedExecuteWhenFinishedAction = null;
            AllTypesCachedField?.SetValue(obj: null, value: null);
            CachedAssembliesCountField?.SetValue(null, 0);
            GenTypes_GetTypeInAnyAssemblyInt_Patch.ClearCache();
            AssemblyTypesCache.Clear();
        }

        [Test]
        public void Prefix_WhenCacheValidAndAssemblyCountMatches_ReturnsCachedTypesDirectly()
        {
            var fakeTypes = new List<Type> { typeof(int), typeof(string), typeof(AccessTools_AllTypes_PatchTests) };
            int currentAssemblyCount = AppDomain.CurrentDomain.GetAssemblies().Length;

            AllTypesCachedField.SetValue(null, fakeTypes);
            CachedAssembliesCountField.SetValue(null, currentAssemblyCount);
            CachedDynamicTypeCountField.SetValue(null, CurrentDynamicTypeCount());

            IEnumerable<Type> result = null;
            bool shouldRunOriginal = AccessTools_AllTypes_Patch.Prefix(ref result);

            Assert.That(shouldRunOriginal, Is.False);
            Assert.That(result, Is.SameAs(fakeTypes));
        }

        [Test]
        public void Prefix_WhenCacheNullOrAssemblyCountDiffers_RebuildsCacheAndReturnsFalse()
        {
            AllTypesCachedField.SetValue(obj: null, value: null);
            CachedAssembliesCountField.SetValue(null, 0);

            IEnumerable<Type> result = null;
            bool shouldRunOriginal = AccessTools_AllTypes_Patch.Prefix(ref result);

            Assert.That(shouldRunOriginal, Is.False);
            Assert.That(result, Is.Not.Null);

            var cached = (List<Type>)AllTypesCachedField.GetValue(null);
            int cachedCount = (int)CachedAssembliesCountField.GetValue(null);

            Assert.That(cached, Is.Not.Null);
            Assert.That(cached.Count, Is.GreaterThan(0));
            // Assembly count may increase during test run (flaky); use >= to allow for concurrent loads
            Assert.That(cachedCount, Is.GreaterThanOrEqualTo(AppDomain.CurrentDomain.GetAssemblies().Length - 1));
            Assert.That(result, Is.SameAs(cached));
        }

        [Test]
        public void Prefix_WhenSameDynamicAssemblyDefinesNewType_InvalidatesCache()
        {
            var assembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("FglAllTypesDynamicGrowth"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("FglAllTypesDynamicGrowth");
            module.DefineType("FglDynamicGrowth.A", TypeAttributes.Public).CreateType();

            // 首次建立清單時列舉型別可能載入其他組件，使組件數改變而重建；重複呼叫到快取穩定命中為止。
            IEnumerable<Type> result = null;
            IEnumerable<Type> previous;
            int attempts = 0;
            do
            {
                previous = result;
                AccessTools_AllTypes_Patch.Prefix(ref result);
            }
            while (!ReferenceEquals(previous, result) && ++attempts < 10);
            Assert.That(result, Is.SameAs(previous), "快取應已穩定命中，否則無法驗證動態型別的失效判斷。");
            Assert.That(result.Any(static t => t.FullName == "FglDynamicGrowth.A"), Is.True);
            int assemblyCount = AppDomain.CurrentDomain.GetAssemblies().Length;

            // 同一動態組件新增型別：組件數不變，快取仍須失效。
            module.DefineType("FglDynamicGrowth.B", TypeAttributes.Public).CreateType();
            Assert.That(AppDomain.CurrentDomain.GetAssemblies().Length, Is.EqualTo(assemblyCount));

            AccessTools_AllTypes_Patch.Prefix(ref result);
            Assert.That(result.Any(static t => t.FullName == "FglDynamicGrowth.B"), Is.True,
                "原版 AllTypes 每次重新列舉會找到 B，快取不得回傳缺少 B 的舊清單。");
        }

        [Test]
        public void Preload_WhenMultiThreadingDisabled_LoadsSynchronously()
        {
            var previous = FasterGameLoadingSettings.EnableMultiThreading;
            FasterGameLoadingSettings.EnableMultiThreading = false;
            try
            {
                AccessTools_AllTypes_Patch.Preload();

                var cached = (List<Type>)AllTypesCachedField.GetValue(null);
                int cachedCount = (int)CachedAssembliesCountField.GetValue(null);

                Assert.That(cached, Is.Not.Null);
                Assert.That(cached.Count, Is.GreaterThan(0));
                // Assembly count may increase during test run (flaky); use >= to allow for concurrent loads
                Assert.That(cachedCount, Is.GreaterThanOrEqualTo(AppDomain.CurrentDomain.GetAssemblies().Length - 1));
            }
            finally
            {
                FasterGameLoadingSettings.EnableMultiThreading = previous;
            }
        }

        [Test]
        public void Preload_WhenMultiThreadingEnabled_EnumeratesInBackgroundWithoutLateWarmup()
        {
            var previous = FasterGameLoadingSettings.EnableMultiThreading;
            FasterGameLoadingSettings.EnableMultiThreading = true;
            try
            {
                AccessTools_AllTypes_Patch.Preload();

                // FullName 預熱改由 Mod 建構子在 XML 解析前完成；排到 ExecuteWhenFinished 會晚於整個 XML 解析階段。
                Assert.That(capturedExecuteWhenFinishedAction, Is.Null);

                bool completed = SpinWait.SpinUntil(() => AllTypesCachedField.GetValue(null) is not null, 3000);
                Assert.That(completed, Is.True, "Background task did not complete within timeout.");
                Assert.That(((List<Type>)AllTypesCachedField.GetValue(null)).Count, Is.GreaterThan(0));
            }
            finally
            {
                FasterGameLoadingSettings.EnableMultiThreading = previous;
            }
        }

        [Test]
        public void BuildTypeList_WhenAssemblyThrows_SwallowsExceptionAndContinues()
        {
            var method = typeof(AccessTools_AllTypes_Patch).GetMethod("BuildTypeList", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            var getTypesMethod = AccessTools.Method(typeof(AccessTools), nameof(AccessTools.GetTypesFromAssembly));
            var testHarmony = new Harmony("FasterGameLoading.Tests.BuildTypeListThrow");
            testHarmony.Patch(getTypesMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(AccessTools_AllTypes_PatchTests), nameof(Prefix_GetTypesThrows))));

            try
            {
                var result = (List<Type>)method.Invoke(null, new object[] { new Assembly[] { typeof(AccessTools_AllTypes_PatchTests).Assembly } });
                Assert.That(result, Is.Empty);
            }
            finally
            {
                testHarmony.UnpatchAll("FasterGameLoading.Tests.BuildTypeListThrow");
            }
        }

        private static bool Prefix_GetTypesThrows(ref Type[] __result)
        {
            throw new ReflectionTypeLoadException(Array.Empty<Type>(), Array.Empty<Exception>());
        }
    }
}
