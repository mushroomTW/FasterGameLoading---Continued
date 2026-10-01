using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NUnit.Framework;

namespace FasterGameLoading.Tests.EarlyModContentLoading
{
    [TestFixture]
    public class AssemblyTypesCacheTests
    {
        private const string HarmonyId = "FasterGameLoading.Tests.AssemblyTypesCacheTests";
        private static Assembly countedAssembly;
        private static int enumerations;
        private Harmony harmony;

        private static void CountEnumerations(Assembly assembly)
        {
            if (ReferenceEquals(assembly, countedAssembly)) enumerations++;
        }

        [SetUp]
        public void SetUp()
        {
            AssemblyTypesCache.Clear();
            GenTypes_GetTypeInAnyAssemblyInt_Patch.ClearCache();
            enumerations = 0;
            countedAssembly = typeof(AssemblyTypesCacheTests).Assembly;
            harmony = new Harmony(HarmonyId);
            harmony.Patch(AccessTools.Method(typeof(AccessTools), nameof(AccessTools.GetTypesFromAssembly)),
                prefix: new HarmonyMethod(typeof(AssemblyTypesCacheTests), nameof(CountEnumerations)));
        }

        [TearDown]
        public void TearDown()
        {
            harmony.UnpatchAll(HarmonyId);
            AssemblyTypesCache.Clear();
            GenTypes_GetTypeInAnyAssemblyInt_Patch.ClearCache();
        }

        [Test]
        public void WarmupAndAllTypesPreload_EnumerateEachAssemblyOnce()
        {
            // FullName 預熱與 AllTypes 預載各掃一遍所有組件，是這個快取要省掉的重複工作。
            GenTypes_GetTypeInAnyAssemblyInt_Patch.WarmupFullNames(new[] { countedAssembly });
            var buildTypeList = AccessTools.Method(typeof(AccessTools_AllTypes_Patch), "BuildTypeList");
            var types = (List<Type>)buildTypeList.Invoke(null, new object[] { new[] { countedAssembly } });

            Assert.That(enumerations, Is.EqualTo(1));
            Assert.That(types, Is.EquivalentTo(AccessTools.GetTypesFromAssembly(countedAssembly)));
        }

        [Test]
        public void Get_DynamicAssembly_EnumeratesEveryTime()
        {
            // 動態組件之後仍可定義新型別，快取住會讓 AllTypes 漏掉新型別。
            var dynamicAssembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("FglDynamicTypesProbe"), AssemblyBuilderAccess.Run);
            countedAssembly = dynamicAssembly;

            AssemblyTypesCache.Get(dynamicAssembly);
            AssemblyTypesCache.Get(dynamicAssembly);

            Assert.That(enumerations, Is.EqualTo(2));
        }
    }
}
