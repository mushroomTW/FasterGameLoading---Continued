using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;
using Verse;

namespace FasterGameLoading.Tests.Settings
{
    [TestFixture]
    public class SessionCacheTests
    {
        private static Harmony harmony;
        private static List<ModMetaData> mockActiveMods = new();

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            harmony = new Harmony("FasterGameLoading.Tests.Settings.SessionCacheTests");

            try
            {
                var modsConfigType = AccessTools.TypeByName("Verse.ModsConfig");
                var activeModsGetter = modsConfigType != null
                    ? AccessTools.PropertyGetter(modsConfigType, "ActiveModsInLoadOrder")
                    : null;
                if (activeModsGetter != null)
                {
                    harmony.Patch(activeModsGetter, prefix: new HarmonyMethod(AccessTools.Method(typeof(SessionCacheTests), nameof(MockActiveModsInLoadOrder))));
                }
            }
            catch { }
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            harmony?.UnpatchAll("FasterGameLoading.Tests.Settings.SessionCacheTests");
        }

        [SetUp]
        public void SetUp()
        {
            mockActiveMods.Clear();
            ResetSessionCache();
        }

        [TearDown]
        public void TearDown()
        {
            mockActiveMods.Clear();
            ResetSessionCache();
            Scribe.mode = LoadSaveMode.Inactive;
        }

        private static bool MockActiveModsInLoadOrder(ref IEnumerable<ModMetaData> __result)
        {
            __result = mockActiveMods;
            return false;
        }

        private static ModMetaData CreateMockModMetaData(string packageId)
        {
            var meta = (ModMetaData)FormatterServices.GetUninitializedObject(typeof(ModMetaData));
            var field = AccessTools.Field(typeof(ModMetaData), "packageIdLowerCase") ?? AccessTools.Field(typeof(ModMetaData), "packageId");
            field?.SetValue(meta, packageId.ToLowerInvariant());
            return meta;
        }

        private static void ResetSessionCache()
        {
            TypeLookupCache.FullNamesFromLastSession = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            SessionCache.modsInLastSession = new List<string>();
            AdaptiveAtlasBaker.BakeSpeedHistory = new List<float>();
            // 預設與本次組件一致，讓只驗證 mod 清單比對的測試不受組件指紋影響。
            TypeLookupCache.PersistedFingerprint = TypeLookupCache.ComputeCurrentFingerprint();
        }

        [Test]
        public void Weights_LengthMatchesHistorySize()
        {
            Assert.That(AdaptiveAtlasBaker.BakeSpeedWeights.Length, Is.EqualTo(4));
            Assert.That(AdaptiveAtlasBaker.BakeSpeedWeights.Length, Is.EqualTo(AdaptiveAtlasBaker.BakeSpeedHistorySize));
            Assert.That(AdaptiveAtlasBaker.BakeSpeedWeights.Sum(), Is.EqualTo(1.0f).Within(0.001f));
        }

        [Test]
        public void ExposeData_WhenModsUnchanged_PreservesCachedEntries()
        {
            mockActiveMods.Add(CreateMockModMetaData("ludeon.rimworld"));
            mockActiveMods.Add(CreateMockModMetaData("fgl.mod"));

            SessionCache.modsInLastSession = new List<string> { "ludeon.rimworld", "fgl.mod" };
            TypeLookupCache.FullNamesFromLastSession["typeA"] = "assemblyA";

            Scribe.mode = LoadSaveMode.PostLoadInit;
            SessionCache.ExposeData();

            Assert.That(TypeLookupCache.FullNamesFromLastSession, Has.Count.EqualTo(1));
            Assert.That(TypeLookupCache.FullNamesFromLastSession["typeA"], Is.EqualTo("assemblyA"));
        }

        [Test]
        public void ExposeData_WhenModsChanged_ClearsCache()
        {
            mockActiveMods.Add(CreateMockModMetaData("ludeon.rimworld"));
            mockActiveMods.Add(CreateMockModMetaData("fgl.mod.v2"));

            SessionCache.modsInLastSession = new List<string> { "ludeon.rimworld", "fgl.mod.v1" };
            TypeLookupCache.FullNamesFromLastSession["typeA"] = "assemblyA";

            Scribe.mode = LoadSaveMode.PostLoadInit;
            SessionCache.ExposeData();

            Assert.That(TypeLookupCache.FullNamesFromLastSession, Is.Empty);
        }

        [Test]
        public void ExposeData_WhenModCountDiffers_ClearsCache()
        {
            mockActiveMods.Add(CreateMockModMetaData("ludeon.rimworld"));
            mockActiveMods.Add(CreateMockModMetaData("fgl.mod"));

            SessionCache.modsInLastSession = new List<string> { "ludeon.rimworld" };
            TypeLookupCache.FullNamesFromLastSession["typeA"] = "assemblyA";

            Scribe.mode = LoadSaveMode.PostLoadInit;
            SessionCache.ExposeData();

            Assert.That(TypeLookupCache.FullNamesFromLastSession, Is.Empty);
        }

        // ── 真實存讀檔循環 ──
        // 依 LoadedModManager.WriteModSettings／ReadModSettings 的流程走完 Saving → LoadingVars →
        // ResolvingCrossRefs → PostLoadInit。只呼叫 PostLoadInit 那一輪會漏掉「資料只在 LoadingVars
        // 讀進區域變數」這類跨輪次的錯誤（過去型別對照就因此從未被讀回來）。

        [Test]
        public void SettingsRoundTrip_RestoresPersistedTypeMapping()
        {
            mockActiveMods.Add(CreateMockModMetaData("ludeon.rimworld"));
            SessionCache.modsInLastSession = new List<string> { "ludeon.rimworld" };
            TypeLookupCache.FullNamesFromLastSession["ThingDef"] = "Verse.ThingDef";
            string path = Path.Combine(Path.GetTempPath(), $"FGL_RoundTrip_{Guid.NewGuid():N}.xml");

            try
            {
                var saved = new FasterGameLoadingSettings();
                Scribe.saver.InitSaving(path, "SettingsBlock");
                try
                {
                    Scribe_Deep.Look(ref saved, "ModSettings");
                }
                finally
                {
                    Scribe.saver.FinalizeSaving();
                }

                ResetSessionCache();
                TypeLookupCache.PersistedFingerprint = null;

                FasterGameLoadingSettings loaded = null;
                Scribe.loader.InitLoading(path);
                try
                {
                    Scribe_Deep.Look(ref loaded, "ModSettings");
                }
                finally
                {
                    Scribe.loader.FinalizeLoading();
                }

                Assert.That(TypeLookupCache.FullNamesFromLastSession.TryGetValue("ThingDef", out var fullName), Is.True,
                    "上次 session 存下的型別對照必須在讀檔後還原。");
                Assert.That(fullName, Is.EqualTo("Verse.ThingDef"));
                Assert.That(TypeLookupCache.PersistedFingerprint, Is.EqualTo(TypeLookupCache.ComputeCurrentFingerprint()));
            }
            finally
            {
                if (Scribe.mode is not LoadSaveMode.Inactive)
                {
                    Scribe.ForceStop();
                }
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        // ── 組件指紋：mod 清單不變但組件內容更新時，型別對照必須失效 ──

        [Test]
        public void ExposeData_WhenAssemblyFingerprintChanged_ClearsOnlyTypeCache()
        {
            mockActiveMods.Add(CreateMockModMetaData("ludeon.rimworld"));
            SessionCache.modsInLastSession = new List<string> { "ludeon.rimworld" };
            TypeLookupCache.FullNamesFromLastSession["typeA"] = "Old.Namespace.TypeA";
            TypeLookupCache.PersistedFingerprint = "fingerprint-of-an-older-build";

            Scribe.mode = LoadSaveMode.PostLoadInit;
            SessionCache.ExposeData();

            Assert.That(TypeLookupCache.FullNamesFromLastSession, Is.Empty);
            Assert.That(SessionCache.modsInLastSession, Is.EqualTo(new[] { "ludeon.rimworld" }),
                "組件更新只影響型別對照，不應連帶改動 mod 清單記錄。");
        }

        [Test]
        public void ExposeData_WhenFingerprintMissingFromOldSettings_ClearsTypeCache()
        {
            mockActiveMods.Add(CreateMockModMetaData("ludeon.rimworld"));
            SessionCache.modsInLastSession = new List<string> { "ludeon.rimworld" };
            TypeLookupCache.FullNamesFromLastSession["typeA"] = "Some.TypeA";
            TypeLookupCache.PersistedFingerprint = null;

            Scribe.mode = LoadSaveMode.PostLoadInit;
            SessionCache.ExposeData();

            Assert.That(TypeLookupCache.FullNamesFromLastSession, Is.Empty);
        }

        [Test]
        public void ComputeAssemblyFingerprint_IsOrderIndependentAndSensitiveToMembership()
        {
            var a = typeof(int).Assembly;
            var b = typeof(SessionCache).Assembly;

            Assert.That(TypeLookupCache.ComputeAssemblyFingerprint(new[] { a, b }),
                Is.EqualTo(TypeLookupCache.ComputeAssemblyFingerprint(new[] { b, a })));
            Assert.That(TypeLookupCache.ComputeAssemblyFingerprint(new[] { a }),
                Is.Not.EqualTo(TypeLookupCache.ComputeAssemblyFingerprint(new[] { a, b })));
        }

        // ── RestoreAfterLoad：舊存檔缺欄位時的補齊行為 ──

        [Test]
        public void RestoreAfterLoad_NullCollectionsAreReplacedWithEmptyOnes()
        {
            mockActiveMods.Add(CreateMockModMetaData("ludeon.rimworld"));
            SessionCache.modsInLastSession = null;
            TypeLookupCache.FullNamesFromLastSession = null;

            InvokeRestoreAfterLoad();

            // 烘焙速度記錄的空值補齊在 AdaptiveAtlasBaker.ExposeBakeSpeedHistory 讀檔當下處理。
            Assert.That(TypeLookupCache.FullNamesFromLastSession, Is.Not.Null.And.Empty);
            Assert.That(SessionCache.modsInLastSession, Is.Not.Null.And.Empty);
        }

        [Test]
        public void RestoreAfterLoad_WhenModSetChanged_KeepsDownscaleCacheOfRunningMods()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FGLSessionCache_" + Guid.NewGuid().ToString("N"));
            string cacheDir = Path.Combine(tempDir, "TextureCache");
            Directory.CreateDirectory(cacheDir);
            var cacheManager = new TextureCacheManager(cacheDir);
            string runningModTexture = Path.Combine(tempDir, "RunningMod", "Textures", "a.png");
            string removedModTexture = Path.Combine(tempDir, "RemovedMod", "Textures", "b.png");
            cacheManager.SetCacheEntry(runningModTexture, Path.Combine(cacheDir, "a_cache.png"));
            cacheManager.SetCacheEntry(removedModTexture, Path.Combine(cacheDir, "b_cache.png"));

            var runningModsField = AccessTools.Field(typeof(LoadedModManager), "runningMods");
            var originalRunningMods = runningModsField.GetValue(null);
            var originalInstance = FasterGameLoadingMod.Instance;
            SetModInstance(CreateModWithCacheManager(cacheManager));
            try
            {
                runningModsField.SetValue(null, new List<ModContentPack> { CreateModContentPack(Path.Combine(tempDir, "RunningMod")) });
                mockActiveMods.Add(CreateMockModMetaData("newly.added.mod"));
                SessionCache.modsInLastSession = new List<string>();

                InvokeRestoreAfterLoad();

                // 快取以「原始路徑＋大小＋修改時間」自我驗證，清單變動不代表快取過期；
                // 只有原始檔已不屬於任何執行中 mod 的項目才移除。
                Assert.That(Directory.Exists(cacheDir), Is.True, "mod 清單變動不得刪除整個降質快取目錄。");
                Assert.That(cacheManager.ResizedTextureCache.ContainsKey(runningModTexture), Is.True);
                Assert.That(cacheManager.ResizedTextureCache.ContainsKey(removedModTexture), Is.False);
            }
            finally
            {
                runningModsField.SetValue(null, originalRunningMods);
                SetModInstance(originalInstance);
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        private static ModContentPack CreateModContentPack(string rootDir)
        {
            var mod = (ModContentPack)FormatterServices.GetUninitializedObject(typeof(ModContentPack));
            AccessTools.Field(typeof(ModContentPack), "rootDirInt").SetValue(mod, new DirectoryInfo(rootDir));
            return mod;
        }

        private static void InvokeRestoreAfterLoad()
        {
            AccessTools.Method(typeof(SessionCache), "RestoreAfterLoad")
                .Invoke(obj: null, parameters: null);
        }

        private static FasterGameLoadingMod CreateModWithCacheManager(TextureCacheManager cacheManager)
        {
            var mod = (FasterGameLoadingMod)FormatterServices.GetUninitializedObject(typeof(FasterGameLoadingMod));
            typeof(FasterGameLoadingMod).GetProperty(nameof(FasterGameLoadingMod.CacheManager))
                .SetValue(mod, cacheManager);
            return mod;
        }

        private static void SetModInstance(FasterGameLoadingMod mod)
        {
            AccessTools.Property(typeof(FasterGameLoadingMod), nameof(FasterGameLoadingMod.Instance))
                .SetValue(obj: null, value: mod);
        }
    }
}
