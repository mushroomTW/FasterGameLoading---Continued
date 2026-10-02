using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;
using UnityEngine;
using Verse;

namespace FasterGameLoading.Tests.Compatibility
{
    [TestFixture]
    public class ProtectedModsTests
    {
        private bool previousStaticAtlasesBaking;

        [SetUp]
        public void SetUp()
        {
            previousStaticAtlasesBaking = FasterGameLoadingSettings.StaticAtlasesBaking;
            SessionLifecycle.Raise(LifecyclePhase.LanguageReloading);
        }

        [TearDown]
        public void TearDown()
        {
            FasterGameLoadingSettings.StaticAtlasesBaking = previousStaticAtlasesBaking;
            SessionLifecycle.Raise(LifecyclePhase.LanguageReloading);
        }

        private static ModContentPack CreateMod(string packageId, string rootDir = null, string packageIdPlayerFacing = null)
        {
            var mod = (ModContentPack)FormatterServices.GetUninitializedObject(typeof(ModContentPack));
            AccessTools.Field(typeof(ModContentPack), "packageIdInt").SetValue(mod, packageId);
            AccessTools.Field(typeof(ModContentPack), "packageIdPlayerFacingInt").SetValue(mod, packageIdPlayerFacing);
            if (rootDir != null)
            {
                AccessTools.Field(typeof(ModContentPack), "rootDirInt").SetValue(mod, new System.IO.DirectoryInfo(rootDir));
            }
            return mod;
        }

        private static void WithRunningMods(List<ModContentPack> mods, System.Action body)
        {
            var runningModsField = AccessTools.Field(typeof(LoadedModManager), "runningMods");
            var originalRunning = runningModsField.GetValue(null);
            try
            {
                runningModsField.SetValue(null, mods);
                body();
            }
            finally
            {
                runningModsField.SetValue(null, originalRunning);
            }
        }

        // ── 貼圖保護 ──

        [TestCase(null)]
        [TestCase("")]
        public void IsProtectedTexturePath_WhenPathIsNullOrEmpty_ReturnsFalse(string path)
        {
            Assert.That(ProtectedMods.IsProtectedTexturePath(path), Is.False);
        }

        [Test]
        public void IsProtectedTexturePath_MatchesNormalizedPathAndIgnoresCase()
        {
            ProtectedMods.SetProtectedTextureRootsForTests("C:/Steam/RimWorld/Mods/AlienRaces");

            // 正斜線與反斜線比對
            Assert.That(ProtectedMods.IsProtectedTexturePath(@"C:\Steam\RimWorld\Mods\AlienRaces\Textures\Pawn.png"), Is.True);
            Assert.That(ProtectedMods.IsProtectedTexturePath("C:/Steam/RimWorld/Mods/AlienRaces/Textures/Pawn.png"), Is.True);
            // 大小寫不敏感
            Assert.That(ProtectedMods.IsProtectedTexturePath(@"c:\steam\rimworld\mods\alienraces\textures\pawn.png"), Is.True);
            // Root 本身
            Assert.That(ProtectedMods.IsProtectedTexturePath("C:/Steam/RimWorld/Mods/AlienRaces"), Is.True);
            // 非子路徑（前綴相似但非同目錄）
            Assert.That(ProtectedMods.IsProtectedTexturePath("C:/Steam/RimWorld/Mods/AlienRacesExtended/Textures/Pawn.png"), Is.False);
            // 其他 Mod 路徑
            Assert.That(ProtectedMods.IsProtectedTexturePath("C:/Steam/RimWorld/Mods/OtherMod/Textures/Pawn.png"), Is.False);
        }

        [Test]
        public void ShouldSkipBaking_OnlyAppliesWhenAdaptiveBakingIsEnabled()
        {
            ProtectedMods.SetProtectedTextureRootsForTests("C:/Mods/TargetMod");

            FasterGameLoadingSettings.StaticAtlasesBaking = false;
            Assert.That(ProtectedMods.ShouldSkipBaking("C:/Mods/TargetMod/Textures/Body.png"), Is.False);

            FasterGameLoadingSettings.StaticAtlasesBaking = true;
            Assert.That(ProtectedMods.ShouldSkipBaking("C:/Mods/TargetMod/Textures/Body.png"), Is.True);
            Assert.That(ProtectedMods.ShouldSkipBaking("C:/Mods/OtherMod/Textures/Body.png"), Is.False);
        }

        [Test]
        public void IsProtectedTexturePath_ConcurrentAccess_DoesNotThrow()
        {
            ProtectedMods.SetProtectedTextureRootsForTests("c:/mods/targetmod");

            Assert.DoesNotThrow(() =>
            {
                System.Threading.Tasks.Parallel.For(0, 100, i =>
                {
                    ProtectedMods.IsProtectedTexturePath($"c:/mods/targetmod/textures/tex_{i}.png");
                    ProtectedMods.IsProtectedTexturePath($"c:/mods/othermod/textures/tex_{i}.png");
                });
            });
        }

        [Test]
        public void TextureRoots_AreBuiltFromRunningProtectedModsOnly()
        {
            var har = CreateMod("erdelf.humanoidalienraces", @"C:\TestModRoot\");
            var other = CreateMod("some.other.mod", @"C:\OtherModRoot\");

            WithRunningMods(new List<ModContentPack> { har, other }, ProtectedMods.InitializeTextureRoots);

            Assert.That(ProtectedMods.GetProtectedTextureRoots(), Is.EquivalentTo(new[] { "C:/TestModRoot" }));
        }

        [Test]
        public void TextureRoots_IncludeSteamSuffixedAndDevHar()
        {
            // 本機與 Workshop 副本並存時，Workshop 版的 PackageId 帶 "_steam" 後綴，只有 PackageIdPlayerFacing 不帶。
            var steamHar = CreateMod("erdelf.humanoidalienraces_steam", @"C:\SteamHar\", "erdelf.HumanoidAlienRaces");
            var devHar = CreateMod("erdelf.humanoidalienraces.dev", @"C:\DevHar\");

            WithRunningMods(new List<ModContentPack> { steamHar, devHar }, ProtectedMods.InitializeTextureRoots);

            Assert.That(ProtectedMods.GetProtectedTextureRoots(), Is.EquivalentTo(new[] { "C:/SteamHar", "C:/DevHar" }));
        }

        [Test]
        public void TextureRoots_WhenModRootDirThrows_DoesNotThrow()
        {
            // 不設定 rootDirInt，使 RootDir getter 存取時拋出 NullReferenceException，驗證內部 catch 區塊
            var har = CreateMod("erdelf.humanoidalienraces");

            WithRunningMods(new List<ModContentPack> { har }, () => Assert.DoesNotThrow(ProtectedMods.InitializeTextureRoots));
        }

        // ── 提早載入保護 ──

        [TestCase("Ayameduki.Harpy", ExpectedResult = true)]
        [TestCase("ayameduki.core", ExpectedResult = true)]
        [TestCase("WRK.RaceMod", ExpectedResult = true)]
        [TestCase("wrk.submod", ExpectedResult = true)]
        [TestCase("erdelf.HumanoidAlienRaces", ExpectedResult = true)]
        [TestCase("ERDELF.HUMANOIDALIENRACES", ExpectedResult = true)]
        [TestCase("erdelf.HumanoidAlienRaces.dev", ExpectedResult = true)]
        [TestCase("chezhou.chezhoulib.lib", ExpectedResult = false)]
        [TestCase("Ludeon.RimWorld", ExpectedResult = false)]
        [TestCase("OskarPotocki.VanillaFactionsExpanded", ExpectedResult = false)]
        [TestCase("", ExpectedResult = false)]
        [TestCase(null, ExpectedResult = false)]
        public bool ShouldSkipEarlyLoad_WithPackageId_MatchesExpected(string packageId)
        {
            return ProtectedMods.ShouldSkipEarlyLoad(packageId);
        }

        [Test]
        public void ShouldSkipEarlyLoad_WithHARMetaDataDependency_ReturnsTrue()
        {
            var metaDataWithHar = new MockMetaData(new MockDependency("erdelf.HumanoidAlienRaces"));
            var metaDataWithDevHar = new MockMetaData(new MockDependency("erdelf.HumanoidAlienRaces.dev"));
            var metaDataWithoutHar = new MockMetaData(new MockDependency("other.dependency"));

            Assert.That(ProtectedMods.ShouldSkipEarlyLoad("Custom.RaceMod", metaDataWithHar), Is.True);
            Assert.That(ProtectedMods.ShouldSkipEarlyLoad("Custom.RaceMod", metaDataWithDevHar), Is.True);
            Assert.That(ProtectedMods.ShouldSkipEarlyLoad("Custom.RaceMod", metaDataWithoutHar), Is.False);
            Assert.That(ProtectedMods.ShouldSkipEarlyLoad("Custom.RaceMod", metaData: null), Is.False);
        }

        [Test]
        public void ShouldSkipEarlyLoad_WhenHarGraphicsHookDeferred_DoesNotSkipHar()
        {
            var setDefers = AccessTools.PropertySetter(typeof(AlienRaceGraphicsHookGate), nameof(AlienRaceGraphicsHookGate.DefersGraphicsHook));
            var metaDataWithHar = new MockMetaData(new MockDependency("erdelf.HumanoidAlienRaces"));
            setDefers.Invoke(null, new object[] { true });
            try
            {
                Assert.That(ProtectedMods.ShouldSkipEarlyLoad("erdelf.HumanoidAlienRaces"), Is.False);
                Assert.That(ProtectedMods.ShouldSkipEarlyLoad("erdelf.HumanoidAlienRaces.dev"), Is.False);
                Assert.That(ProtectedMods.ShouldSkipEarlyLoad("Custom.RaceMod", metaDataWithHar), Is.False);
                // Ayameduki 與 AyaTweaks 不受 HAR 閘門影響。
                Assert.That(ProtectedMods.ShouldSkipEarlyLoad("Ayameduki.Harpy"), Is.True);
                Assert.That(ProtectedMods.ShouldSkipEarlyLoad("WRK.RaceMod"), Is.True);
            }
            finally
            {
                setDefers.Invoke(null, new object[] { false });
            }
        }

        [Test]
        public void ShouldSkipEarlyLoad_WithNullModContentPack_ReturnsFalse()
        {
            Assert.That(ProtectedMods.ShouldSkipEarlyLoad((ModContentPack)null), Is.False);
        }

        [Test]
        public void ShouldSkipEarlyLoad_WithModContentPack_IsStableAcrossCallsAndResets()
        {
            var skipped = CreateMod("wrk.custommod");
            var normal = CreateMod("normal.mod");

            Assert.That(ProtectedMods.ShouldSkipEarlyLoad(skipped), Is.True);
            Assert.That(ProtectedMods.ShouldSkipEarlyLoad(skipped), Is.True);
            Assert.That(ProtectedMods.ShouldSkipEarlyLoad(normal), Is.False);

            SessionLifecycle.Raise(LifecyclePhase.LanguageReloading);

            Assert.That(ProtectedMods.ShouldSkipEarlyLoad(skipped), Is.True);
            Assert.That(ProtectedMods.ShouldSkipEarlyLoad(normal), Is.False);
        }

        // ── TryInsertStatic 補丁 ──

        [Test]
        public void BakingPatch_Prepare_FollowsAdaptiveBaking()
        {
            FasterGameLoadingSettings.StaticAtlasesBaking = false;
            Assert.That(AdaptiveBakingSkipList.Prepare(), Is.False);

            FasterGameLoadingSettings.StaticAtlasesBaking = true;
            Assert.That(AdaptiveBakingSkipList.Prepare(), Is.True);
        }

        [Test]
        public void BakingPatch_Prefix_BlocksTexturesMarkedForSkipping()
        {
            var tex1 = (Texture2D)FormatterServices.GetUninitializedObject(typeof(Texture2D));
            var tex2 = (Texture2D)FormatterServices.GetUninitializedObject(typeof(Texture2D));
            ProtectedMods.SetProtectedTextureRootsForTests("C:/Mods/TargetMod");
            FasterGameLoadingSettings.StaticAtlasesBaking = true;
            LoadedTextureRegistry.MarkSkipBakingIfProtected("C:/Mods/TargetMod/Textures/Body.png", tex1);

            Assert.That(AdaptiveBakingSkipList.Prefix(TextureAtlasGroup.Building, tex1, mask: null), Is.False);
            Assert.That(AdaptiveBakingSkipList.Prefix(TextureAtlasGroup.Building, texture: null, mask: tex1), Is.False);
            Assert.That(AdaptiveBakingSkipList.Prefix(TextureAtlasGroup.Building, tex2, mask: null), Is.True);
            Assert.That(AdaptiveBakingSkipList.Prefix(TextureAtlasGroup.Building, texture: null, mask: null), Is.True);
        }

        private sealed class MockMetaData
        {
            public IEnumerable Dependencies { get; }

            public MockMetaData(params object[] dependencies)
            {
                Dependencies = new List<object>(dependencies);
            }
        }

        private sealed class MockDependency
        {
            public string PackageId { get; }

            public MockDependency(string packageId)
            {
                PackageId = packageId;
            }
        }
    }
}
