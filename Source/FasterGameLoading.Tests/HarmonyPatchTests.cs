using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Threading;
using HarmonyLib;
using NUnit.Framework;
using UnityEngine;
using Verse;


namespace FasterGameLoading.Tests
{
    [TestFixture]
    public class HarmonyPatchTests
    {
        private static Harmony harmony;

        // 期望值陣列：避免每個斷言反覆建立常數陣列 (CA1861)
        private static readonly string[] ExpectedSessionTypes = { "type" };
        private static readonly string[] ExpectedSessionMods = { "test.mod" };
        private static readonly float[] ExpectedSessionBakeSpeeds = { 45f };

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            harmony = new Harmony("FasterGameLoading.Tests.Harmony");

            // 1. 手動套用 AccessTools.TypeByName 補丁
            var typeByNameOriginal = AccessTools.Method(typeof(AccessTools), nameof(AccessTools.TypeByName));
            var typeByNamePrefix = AccessTools.Method(typeof(AccessTools_TypeByName_Patch), nameof(AccessTools_TypeByName_Patch.Prefix));
            var typeByNamePostfix = AccessTools.Method(typeof(AccessTools_TypeByName_Patch), nameof(AccessTools_TypeByName_Patch.Postfix));
            harmony.Patch(typeByNameOriginal, prefix: new HarmonyMethod(typeByNamePrefix), postfix: new HarmonyMethod(typeByNamePostfix));

            // 2. 手動套用 AccessTools.AllTypes 補丁
            var allTypesOriginal = AccessTools.Method(typeof(AccessTools), nameof(AccessTools.AllTypes));
            var allTypesPrefix = AccessTools.Method(typeof(AccessTools_AllTypes_Patch), nameof(AccessTools_AllTypes_Patch.Prefix));
            harmony.Patch(allTypesOriginal, prefix: new HarmonyMethod(allTypesPrefix));
        }



        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            harmony.UnpatchAll("FasterGameLoading.Tests.Harmony");
        }

        [SetUp]
        public void SetUp()
        {
            // 清理與初始化靜態快取
            GenTypes_GetTypeInAnyAssemblyInt_Patch.ClearCache();
            AccessTools_TypeByName_Patch.cachedResults.Clear();
            TypeLookupCache.FullNamesFromLastSession.Clear();
        }


        [TearDown]
        public void TearDown()
        {
            // 清理靜態快取
            GenTypes_GetTypeInAnyAssemblyInt_Patch.ClearCache();
            AccessTools_TypeByName_Patch.cachedResults.Clear();
            TypeLookupCache.FullNamesFromLastSession.Clear();

            // 清除 AccessTools_AllTypes_Patch 的快取欄位以利後續測試
            var field = typeof(AccessTools_AllTypes_Patch).GetField("allTypesCached", BindingFlags.NonPublic | BindingFlags.Static);
            if (field != null)
            {
                field.SetValue(null, null);
            }
        }

        [Test]
        public void TestModAssetBundlesHandler_ReloadAll_Patch_RunsAfterChezhouLib()
        {
            var harmonyAfter = typeof(ModAssetBundlesHandler_ReloadAll_Patch)
                .GetCustomAttributes(typeof(HarmonyAfter), inherit: false)
                .Cast<HarmonyAfter>()
                .FirstOrDefault();

            Assert.IsNotNull(harmonyAfter, "ReloadAll patch should declare HarmonyAfter for ChezhouLib.");
            Assert.Contains("ChezhouLib.lib", harmonyAfter.info.after);
        }



        [Test]
        public void TestAlienRacesCompat_IsRemoved()
        {
            Assert.IsNull(
                typeof(FasterGameLoadingMod).Assembly.GetType("FasterGameLoading.AlienRacesCompat"),
                "HAR 相容性應靠 early-loading skip 保留原生時序，不再用非冪等的事後重掃。");
        }

        [Test]
        public void TestAccessTools_TypeByName_Patch_HitsRuntimeCache()
        {
            // 1. 注入 mock 型別到執行期快取中
            var mockType = typeof(string);
            string typeName = "MockTypeForTest";
            AccessTools_TypeByName_Patch.cachedResults[typeName] = mockType;

            // 2. 呼叫目標方法，應被 Prefix 攔截並直接返回快取型別
            var resolvedType = AccessTools.TypeByName(typeName);

            // 3. 斷言結果與快取相符
            Assert.AreEqual(mockType, resolvedType);
        }

        [Test]
        public void TestAccessTools_TypeByName_Patch_IsolatedFromGenTypesCaches()
        {
            // GenTypes 的快取與跨 session 對照依 GenTypes 的解析規則產生，
            // AccessTools.TypeByName 不得讀取，否則兩套規則的結果會互相污染。
            const string shortName = "MyIntTypeForIsolationTest";
            GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults[shortName] = typeof(int);
            TypeLookupCache.FullNamesFromLastSession[shortName] = typeof(int).FullName;

            Assert.IsNull(AccessTools.TypeByName(shortName));
            Assert.IsFalse(AccessTools_TypeByName_Patch.cachedResults.ContainsKey(shortName));
        }

        [Test]
        public void TestAccessTools_TypeByName_Patch_CachesResolvedType()
        {
            string fullName = typeof(Verse.ThingDef).FullName;

            Assert.AreEqual(typeof(Verse.ThingDef), AccessTools.TypeByName(fullName));
            Assert.IsTrue(AccessTools_TypeByName_Patch.cachedResults.TryGetValue(fullName, out var cached));
            Assert.AreEqual(typeof(Verse.ThingDef), cached);
        }

        [Test]
        public void TestAccessTools_TypeByName_Patch_DoesNotCacheNullResult()
        {
            string missingTypeName = "DefinitelyMissingTypeForFGLTest";

            AccessTools_TypeByName_Patch.Postfix(null, missingTypeName, __runOriginal: true);

            Assert.IsFalse(
                AccessTools_TypeByName_Patch.cachedResults.ContainsKey(missingTypeName),
                "A transient miss must not be cached as null because later-loaded assemblies may define the type.");
        }

        [Test]
        public void TestAccessTools_AllTypes_Patch_BypassesWithCache()
        {
            // 1. 透過反射向 allTypesCached 與 cachedAssembliesCount 欄位注入測試用的 mock 清單
            var fieldCached = typeof(AccessTools_AllTypes_Patch).GetField("allTypesCached", BindingFlags.NonPublic | BindingFlags.Static);
            var fieldCount = typeof(AccessTools_AllTypes_Patch).GetField("cachedAssembliesCount", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(fieldCached);
            Assert.IsNotNull(fieldCount);

            var mockList = new List<Type> { typeof(string), typeof(int), typeof(double) };
            fieldCached.SetValue(null, mockList);
            fieldCount.SetValue(null, AppDomain.CurrentDomain.GetAssemblies().Length);

            // 2. 呼叫 AccessTools.AllTypes()
            var result = AccessTools.AllTypes();

            // 3. 驗證返回的集合與我們注入的 mock 內容一致
            Assert.AreEqual(mockList, result);
        }


        [Test]
        public void TestDirectXmlLoader_XmlAssetsInModFolder_Patch_NullModGuard()
        {
            // 驗證當 mod 參數為 null 時，Prefix 會直接回傳 true 讓 vanilla 處理，
            // 而非嘗試執行並行載入（這是 null 防禦提前返回路徑，不是例外 fallback 路徑）。
            // 注意：若要測試例外 fallback（try/catch 區塊），需要真實的 ModContentPack 實例，
            // 但 ModContentPack 的建構依賴完整的 RimWorld 執行環境，在單元測試中不可行。
            LoadableXmlAsset[] result = null;
            bool shouldRunOriginal = DirectXmlLoader_XmlAssetsInModFolder_Patch.Prefix(ref result, null, null, null);
            Assert.IsTrue(shouldRunOriginal, "mod 為 null 時，Prefix 應回傳 true 交由 vanilla 處理");
            Assert.IsNull(result, "mod 為 null 時，result 應保持 null 不變");
        }

        [Test]
        public void TestDirectXmlLoader_XmlAssetsInModFolder_Patch_PreservesVanillaFolderOrder()
        {
            var firstRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FGL_First_" + Guid.NewGuid());
            var secondRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FGL_Second_" + Guid.NewGuid());
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(firstRoot, "Patches"));
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(secondRoot, "Patches"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(firstRoot, "Patches", "Same.xml"), "<Patch />");
                System.IO.File.WriteAllText(System.IO.Path.Combine(secondRoot, "Patches", "Same.xml"), "<Patch />");
                System.IO.File.WriteAllText(System.IO.Path.Combine(secondRoot, "Patches", "Other.xml"), "<Patch />");
                System.IO.File.WriteAllText(System.IO.Path.Combine(firstRoot, "Patches", "._Hidden.xml"), "<Patch />");

                var method = AccessTools.Method(typeof(DirectXmlLoader_XmlAssetsInModFolder_Patch), "XmlFilesInVanillaOrder");
                var files = (List<System.IO.FileInfo>)method.Invoke(
                    null,
                    new object[] { null, "Patches/", new List<string> { firstRoot, secondRoot } });

                Assert.AreEqual(2, files.Count);
                Assert.AreEqual(System.IO.Path.Combine(firstRoot, "Patches", "Same.xml"), files[0].FullName);
                Assert.AreEqual(System.IO.Path.Combine(secondRoot, "Patches", "Other.xml"), files[1].FullName);
            }
            finally
            {
                if (System.IO.Directory.Exists(firstRoot)) System.IO.Directory.Delete(firstRoot, true);
                if (System.IO.Directory.Exists(secondRoot)) System.IO.Directory.Delete(secondRoot, true);
            }
        }

        [Test]
        public void TestBuildableDef_PostLoad_Patch_PrepareAndTranspilerMatchDeferredQueue()
        {
            var patchType = typeof(ThingDef_PostLoad_Patch).Assembly.GetType("FasterGameLoading.BuildableDef_PostLoad_Patch");
            Assert.IsNotNull(patchType, "BuildableDef.PostLoad patch should feed the deferred icon queue.");

            var prepare = patchType.GetMethod("Prepare", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(prepare);

            bool originalDelayGraphicLoading = FasterGameLoadingSettings.DelayGraphicLoading;
            try
            {
                FasterGameLoadingSettings.DelayGraphicLoading = false;
                Assert.IsFalse((bool)prepare.Invoke(null, null));

                FasterGameLoadingSettings.DelayGraphicLoading = true;
                Assert.IsTrue((bool)prepare.Invoke(null, null));
            }
            finally
            {
                FasterGameLoadingSettings.DelayGraphicLoading = originalDelayGraphicLoading;
            }

            var transpiler = patchType.GetMethod("Transpiler", BindingFlags.Public | BindingFlags.Static);
            var executeDelayed = AccessTools.Method(patchType, "ExecuteDelayed");
            Assert.IsNotNull(transpiler);
            Assert.IsNotNull(executeDelayed);

            var executeWhenFinished = AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.ExecuteWhenFinished));
            var input = new[] { new CodeInstruction(OpCodes.Call, executeWhenFinished) };
            var output = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { input })).ToList();

            Assert.AreEqual(2, output.Count);
            Assert.AreEqual(OpCodes.Ldarg_0, output[0].opcode);
            Assert.AreEqual(OpCodes.Call, output[1].opcode);
            Assert.AreEqual(executeDelayed, output[1].operand);
        }



        [Test]
        public void TestEarlyModContentLoader_UsesImageOptSynchronousScopeWithoutGlobalBypass()
        {
            var imageOptActiveGetter = typeof(TextureOwnership)
                .GetProperty(nameof(TextureOwnership.Current))
                .GetGetMethod();
            var enterSyncScope = typeof(ImageOptEarlyLoadCoordinator)
                .GetMethod("EnterEarlyLoadSyncScope", BindingFlags.NonPublic | BindingFlags.Static);
            var imageOptInstalledGetter = typeof(ImageOptEarlyLoadCoordinator)
                .GetProperty("IsInstalled", BindingFlags.NonPublic | BindingFlags.Static)
                .GetGetMethod(true);

            // 掃描 EarlyModContentLoader 宣告的所有方法，而非只看 Update：此測試守的是
            // 「提早載入走 ImageOpt 同步 scope，且不因 ImageOpt 而全域停用」這個不變式，
            // 呼叫落在 Update 本體或它抽出的私有輔助方法內都同樣成立。
            var declaredMethods = typeof(EarlyModContentLoader).GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.IsTrue(
                Array.Exists(declaredMethods, m => MethodBodyContainsMetadataToken(m, enterSyncScope)),
                "FGL early content loading should enter the ImageOpt synchronous scope.");
            Assert.IsTrue(
                Array.Exists(declaredMethods, m => MethodBodyContainsMetadataToken(m, imageOptInstalledGetter)),
                "FGL should cache whether the ImageOpt synchronous scope is required.");
            Assert.IsFalse(
                Array.Exists(declaredMethods, m => MethodBodyContainsMetadataToken(m, imageOptActiveGetter)),
                "ImageOpt should not globally disable FGL early content loading.");
        }

        private static bool MethodBodyContainsMetadataToken(MethodInfo method, MethodInfo calledMethod)
        {
            return MethodBodyContainsMetadataToken(method, calledMethod.MetadataToken);
        }

        private static bool MethodBodyContainsMetadataToken(MethodInfo method, int token)
        {
            var il = method?.GetMethodBody()?.GetILAsByteArray();
            if (il == null) return false;

            for (int i = 0; i <= il.Length - sizeof(int); i++)
            {
                if (BitConverter.ToInt32(il, i) == token) return true;
            }
            return false;
        }

        private static bool MethodOrStateMachineReferencesMethod(MethodInfo method, MethodInfo referencedMethod)
        {
            if (MethodBodyReferencesMethod(method, referencedMethod)) return true;

            var stateMachineType = method.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;
            if (stateMachineType != null && TypeReferencesMethod(stateMachineType, referencedMethod)) return true;

            foreach (var nestedType in method.DeclaringType.GetNestedTypes(BindingFlags.NonPublic))
            {
                if (!nestedType.Name.Contains(method.Name)) continue;

                if (TypeReferencesMethod(nestedType, referencedMethod)) return true;
            }
            return false;
        }

        private static bool TypeReferencesMethod(Type type, MethodInfo referencedMethod)
        {
            foreach (var nestedMethod in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (MethodBodyReferencesMethod(nestedMethod, referencedMethod)) return true;
            }
            return false;
        }

        private static bool MethodBodyReferencesMethod(MethodInfo method, MethodInfo referencedMethod)
        {
            var il = method?.GetMethodBody()?.GetILAsByteArray();
            if (il == null) return false;

            for (int i = 0; i <= il.Length - sizeof(int); i++)
            {
                var token = BitConverter.ToInt32(il, i);
                if (token == referencedMethod.MetadataToken) return true;

                try
                {
                    var candidate = method.Module.ResolveMethod(token);
                    if (candidate.Name == referencedMethod.Name
                        && candidate.DeclaringType?.FullName == referencedMethod.DeclaringType?.FullName)
                    {
                        return true;
                    }
                }
                catch
                {
                    // Ignore non-token bytes while scanning compact test IL.
                }
            }
            return false;
        }

        [Test]
        public void TestStartupPostfix_UsesExecuteWhenFinishedForCompletionActions()
        {
            var executeWhenFinished = AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.ExecuteWhenFinished));

            // 掃描 Startup 宣告的所有方法，而非只看 Postfix：此測試守的是
            // 「排程一律經由 ExecuteWhenFinished」這個不變式，該呼叫落在
            // Postfix 本體或它抽出的私有輔助方法內都同樣成立。
            var declaredMethods = typeof(Startup).GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.IsTrue(
                Array.Exists(declaredMethods, m => MethodBodyReferencesMethod(m, executeWhenFinished)),
                "Startup completion actions must use ExecuteWhenFinished instead of mutating LongEventHandler.toExecuteWhenFinished directly.");
        }

        [Test]
        public void TestDelayedActionsPerformActions_UnpatchesSoundStarterDirectly()
        {
            var performActions = typeof(DelayedActions).GetMethod(nameof(DelayedActions.PerformActions));
            var unpatch = typeof(SoundStarter_Patch).GetMethod(nameof(SoundStarter_Patch.Unpatch), BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsTrue(
                MethodOrStateMachineReferencesMethod(performActions, unpatch),
                "DelayedActions.PerformActions must directly release the startup sound guard even if deferred loading exits early.");
        }

        [Test]
        public void TestSubSoundExecuteDelayed_IgnoresNullAction()
        {
            var executeDelayed = typeof(SubSoundDef_ResolvePatch).GetMethod("ExecuteDelayed", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.DoesNotThrow(() => executeDelayed.Invoke(null, new object[] { null, null }));
        }

        [Test]
#pragma warning disable MA0051 // 涵蓋所有設定欄位往返驗證，拆分成多個方法會降低可讀性
        public void TestSettingsExposeData_PreservesSettingsAndSessionCacheOutsideScribe()
        {
            var originalScribeMode = Scribe.mode;
            var originalVerboseLogging = FasterGameLoadingSettings.VerboseLogging;
            var originalDelayGraphicLoading = FasterGameLoadingSettings.DelayGraphicLoading;
            var originalEarlyModContentLoading = FasterGameLoadingSettings.earlyModContentLoading;
            var originalStaticAtlasesBaking = FasterGameLoadingSettings.StaticAtlasesBaking;
            var originalEnableMultiThreading = FasterGameLoadingSettings.EnableMultiThreading;
            var originalTypes = TypeLookupCache.FullNamesFromLastSession;
            var originalMods = SessionCache.modsInLastSession;
            var originalBakeSpeeds = AdaptiveAtlasBaker.BakeSpeedHistory;

            try
            {
                Scribe.mode = LoadSaveMode.Inactive;
                FasterGameLoadingSettings.VerboseLogging = true;
                FasterGameLoadingSettings.DelayGraphicLoading = true;
                FasterGameLoadingSettings.earlyModContentLoading = false;
                FasterGameLoadingSettings.StaticAtlasesBaking = true;
                FasterGameLoadingSettings.EnableMultiThreading = false;
                TypeLookupCache.FullNamesFromLastSession = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
                TypeLookupCache.FullNamesFromLastSession.TryAdd("type", "System.String");
                SessionCache.modsInLastSession = new List<string> { "test.mod" };
                AdaptiveAtlasBaker.BakeSpeedHistory = new List<float> { 45f };

                new FasterGameLoadingSettings().ExposeData();

                Assert.IsTrue(FasterGameLoadingSettings.VerboseLogging);
                Assert.IsTrue(FasterGameLoadingSettings.DelayGraphicLoading);
                Assert.IsFalse(FasterGameLoadingSettings.earlyModContentLoading);
                Assert.IsTrue(FasterGameLoadingSettings.StaticAtlasesBaking);
                Assert.IsFalse(FasterGameLoadingSettings.EnableMultiThreading);
                Assert.That(TypeLookupCache.FullNamesFromLastSession.Keys, Is.EquivalentTo(ExpectedSessionTypes));
                Assert.That(SessionCache.modsInLastSession, Is.EqualTo(ExpectedSessionMods));
                Assert.That(AdaptiveAtlasBaker.BakeSpeedHistory, Is.EqualTo(ExpectedSessionBakeSpeeds));
            }
            finally
            {
                Scribe.mode = originalScribeMode;
                FasterGameLoadingSettings.VerboseLogging = originalVerboseLogging;
                FasterGameLoadingSettings.DelayGraphicLoading = originalDelayGraphicLoading;
                FasterGameLoadingSettings.earlyModContentLoading = originalEarlyModContentLoading;
                FasterGameLoadingSettings.StaticAtlasesBaking = originalStaticAtlasesBaking;
                FasterGameLoadingSettings.EnableMultiThreading = originalEnableMultiThreading;
                TypeLookupCache.FullNamesFromLastSession = originalTypes;
                SessionCache.modsInLastSession = originalMods;
                AdaptiveAtlasBaker.BakeSpeedHistory = originalBakeSpeeds;
            }
        }
#pragma warning restore MA0051

        [Test]
#pragma warning disable MA0051 // 涵蓋設定序列化往返之完整驗證，拆分成多個方法會降低可讀性
        public void TestSettingsExposeData_SavesSessionCache()
        {
            var originalTypes = TypeLookupCache.FullNamesFromLastSession;
            var originalMods = SessionCache.modsInLastSession;
            var originalBakeSpeeds = AdaptiveAtlasBaker.BakeSpeedHistory;
            var savePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"FGL_Settings_{Guid.NewGuid():N}.xml");

            try
            {
                TypeLookupCache.FullNamesFromLastSession = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
                TypeLookupCache.FullNamesFromLastSession.TryAdd("type", "System.String");
                SessionCache.modsInLastSession = new List<string> { "test.mod" };
                AdaptiveAtlasBaker.BakeSpeedHistory = new List<float> { 45f };

                Scribe.saver.InitSaving(savePath, "settings");
                try
                {
                    new FasterGameLoadingSettings().ExposeData();
                }
                finally
                {
                    Scribe.saver.FinalizeSaving();
                }

                var savedXml = System.IO.File.ReadAllText(savePath);
                // 貼圖載入清單從未被讀取，不得再寫進設定檔（每次啟動都要解析、收尾時又整份重寫）。
                Assert.That(savedXml, Does.Not.Contain("loadedTexturesSinceLastSession"));
                Assert.That(savedXml, Does.Contain(FGLConsts.LoadedTypesKey));
                Assert.That(savedXml, Does.Contain(FGLConsts.TypeCacheAssemblyFingerprintKey));
                Assert.That(savedXml, Does.Contain(FGLConsts.HistoricalBakeSpeedsKey));
            }
            finally
            {
                if (Scribe.mode is not LoadSaveMode.Inactive)
                {
                    Scribe.ForceStop();
                }

                TypeLookupCache.FullNamesFromLastSession = originalTypes;
                SessionCache.modsInLastSession = originalMods;
                AdaptiveAtlasBaker.BakeSpeedHistory = originalBakeSpeeds;

                if (System.IO.File.Exists(savePath))
                {
                    System.IO.File.Delete(savePath);
                }
            }
        }
#pragma warning restore MA0051

    }
}
