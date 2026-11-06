using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;
using UnityEngine;
using Verse;

namespace FasterGameLoading.Tests.Compatibility;
[TestFixture]
public class ProtectedModsTests
{

    [SetUp]
    public void SetUp()
    {
        SessionLifecycle.Raise(LifecyclePhase.LanguageReloading);
    }

    [TearDown]
    public void TearDown()
    {
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
    public void IsProtectedTexturePath_RootBoundaryIsExact()
    {
        ProtectedMods.SetProtectedTextureRootsForTests("Mods/HAR");

        // 根目錄本身與其子路徑
        Assert.That(ProtectedMods.IsProtectedTexturePath("Mods/HAR"), Is.True);
        Assert.That(ProtectedMods.IsProtectedTexturePath("Mods/HAR/sub"), Is.True);
        Assert.That(ProtectedMods.IsProtectedTexturePath("mods/har/Sub/x.png"), Is.True);
        Assert.That(ProtectedMods.IsProtectedTexturePath(@"Mods\HAR\sub\x.png"), Is.True);
        // 同前綴的兄弟資料夾與較短的路徑不算
        Assert.That(ProtectedMods.IsProtectedTexturePath("Mods/HAR2/x.png"), Is.False);
        Assert.That(ProtectedMods.IsProtectedTexturePath("Mods/HA"), Is.False);
        Assert.That(ProtectedMods.IsProtectedTexturePath("Mods/HAR_old"), Is.False);
        Assert.That(ProtectedMods.IsProtectedTexturePath("Mods"), Is.False);
    }

    [Test]
    public void IsProtectedTexturePath_ChecksEveryRoot()
    {
        ProtectedMods.SetProtectedTextureRootsForTests("C:/Mods/A", "C:/Mods/B");

        Assert.That(ProtectedMods.IsProtectedTexturePath("C:/Mods/A/x.png"), Is.True);
        Assert.That(ProtectedMods.IsProtectedTexturePath("C:/Mods/B/x.png"), Is.True);
        Assert.That(ProtectedMods.IsProtectedTexturePath("C:/Mods/C/x.png"), Is.False);
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

        WithRunningMods([har, other], ProtectedMods.InitializeTextureRoots);

        Assert.That(ProtectedMods.GetProtectedTextureRoots(), Is.EquivalentTo(["C:/TestModRoot"]));
    }

    [Test]
    public void TextureRoots_IncludeSteamSuffixedAndDevHar()
    {
        // 本機與 Workshop 副本並存時，Workshop 版的 PackageId 帶 "_steam" 後綴，只有 PackageIdPlayerFacing 不帶。
        var steamHar = CreateMod("erdelf.humanoidalienraces_steam", @"C:\SteamHar\", "erdelf.HumanoidAlienRaces");
        var devHar = CreateMod("erdelf.humanoidalienraces.dev", @"C:\DevHar\");

        WithRunningMods([steamHar, devHar], ProtectedMods.InitializeTextureRoots);

        Assert.That(ProtectedMods.GetProtectedTextureRoots(), Is.EquivalentTo(["C:/SteamHar", "C:/DevHar"]));
    }

    [Test]
    public void TextureRoots_WhenModRootDirThrows_DoesNotThrow()
    {
        // 不設定 rootDirInt，使 RootDir getter 存取時拋出 NullReferenceException，驗證內部 catch 區塊
        var har = CreateMod("erdelf.humanoidalienraces");

        WithRunningMods([har], () => Assert.DoesNotThrow(ProtectedMods.InitializeTextureRoots));
    }

    // ── Pumpkin Library 材質包 ──

    private const string PumpkinModuleSettingsDir = @"1.7\Modules\TextureOverride";

    private static string CreatePumpkinRoot(string settingsXml, string settingsDir = PumpkinModuleSettingsDir)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FGLPumpkinTest_" + System.Guid.NewGuid().ToString("N"));
        WritePumpkinSettings(root, settingsDir, settingsXml);
        return root;
    }

    private static void WritePumpkinSettings(string root, string settingsDir, string settingsXml)
    {
        var dir = System.IO.Path.Combine(root, settingsDir);
        System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "TextureOverrideSettings.xml"), settingsXml);
    }

    // 建立 Pumpkin Library；<paramref name="textureOverrideLoaded"/> 模擬 LoadFolders.xml 是否載入了 TextureOverride 模組。
    private static ModContentPack CreatePumpkinMod(string rootDir, bool textureOverrideLoaded = true, string packageId = "pumpkin.pumpkinlibrary", string packageIdPlayerFacing = null)
    {
        var mod = CreateMod(packageId, rootDir, packageIdPlayerFacing);
        var folders = new List<string> { System.IO.Path.Combine(rootDir, "1.7") };
        if (textureOverrideLoaded) folders.Insert(0, System.IO.Path.Combine(rootDir, PumpkinModuleSettingsDir));
        mod.foldersToLoadDescendingOrder = folders;
        return mod;
    }

    private static string PumpkinSettings(string target, string provider)
        => $"<TextureOverrideSettings><targetMods><li>{target}</li></targetMods>"
            + $"<providerMods><li>{provider}</li><li>pumpkin.notrunning</li></providerMods></TextureOverrideSettings>";

    private static readonly string PumpkinSettingsTargetingHar = PumpkinSettings("erdelf.HumanoidAlienRaces", "Pumpkin.HarDdsPack");

    [Test]
    public void TextureRoots_IncludePumpkinProvidersOfProtectedTargets()
    {
        var pumpkinRoot = CreatePumpkinRoot(PumpkinSettingsTargetingHar);
        try
        {
            var pumpkin = CreatePumpkinMod(pumpkinRoot);
            var har = CreateMod("erdelf.humanoidalienraces", @"C:\HarRoot\");
            var provider = CreateMod("pumpkin.harddspack", @"C:\DdsPackRoot\");
            var other = CreateMod("some.other.mod", @"C:\OtherModRoot\");

            WithRunningMods([pumpkin, har, provider, other], ProtectedMods.InitializeTextureRoots);

            Assert.That(ProtectedMods.GetProtectedTextureRoots(), Is.EquivalentTo(["C:/HarRoot", "C:/DdsPackRoot"]));
        }
        finally
        {
            System.IO.Directory.Delete(pumpkinRoot, recursive: true);
        }
    }

    [Test]
    public void TextureRoots_ExcludePumpkinProvidersWhenNoTargetIsProtected()
    {
        var pumpkinRoot = CreatePumpkinRoot(PumpkinSettings("some.other.mod", "pumpkin.harddspack"));
        try
        {
            var pumpkin = CreatePumpkinMod(pumpkinRoot);
            var target = CreateMod("some.other.mod", @"C:\OtherModRoot\");
            var provider = CreateMod("pumpkin.harddspack", @"C:\DdsPackRoot\");

            WithRunningMods([pumpkin, target, provider], ProtectedMods.InitializeTextureRoots);

            Assert.That(ProtectedMods.GetProtectedTextureRoots(), Is.Empty);
        }
        finally
        {
            System.IO.Directory.Delete(pumpkinRoot, recursive: true);
        }
    }

    [Test]
    public void TextureRoots_WhenPumpkinSettingsAreInvalid_KeepOtherRoots()
    {
        var pumpkinRoot = CreatePumpkinRoot("<TextureOverrideSettings><targetMods>");
        try
        {
            var pumpkin = CreatePumpkinMod(pumpkinRoot);
            var har = CreateMod("erdelf.humanoidalienraces", @"C:\HarRoot\");
            var provider = CreateMod("pumpkin.harddspack", @"C:\DdsPackRoot\");

            WithRunningMods([pumpkin, har, provider], ProtectedMods.InitializeTextureRoots);

            Assert.That(ProtectedMods.GetProtectedTextureRoots(), Is.EquivalentTo(["C:/HarRoot"]));
        }
        finally
        {
            System.IO.Directory.Delete(pumpkinRoot, recursive: true);
        }
    }

    [Test]
    public void TextureRoots_ExcludePumpkinProvidersWhenTextureOverrideIsNotLoaded()
    {
        var pumpkinRoot = CreatePumpkinRoot(PumpkinSettingsTargetingHar);
        try
        {
            var pumpkin = CreatePumpkinMod(pumpkinRoot, textureOverrideLoaded: false);
            var har = CreateMod("erdelf.humanoidalienraces", @"C:\HarRoot\");
            var provider = CreateMod("pumpkin.harddspack", @"C:\DdsPackRoot\");

            WithRunningMods([pumpkin, har, provider], ProtectedMods.InitializeTextureRoots);

            Assert.That(ProtectedMods.GetProtectedTextureRoots(), Is.EquivalentTo(["C:/HarRoot"]));
        }
        finally
        {
            System.IO.Directory.Delete(pumpkinRoot, recursive: true);
        }
    }

    [Test]
    public void TextureRoots_MatchSteamSuffixedPumpkinAndProviderCopies()
    {
        // 本機與 Workshop 副本並存時，Workshop 版的 PackageId 帶 "_steam" 後綴，只有 PackageIdPlayerFacing 與設定檔相符。
        var pumpkinRoot = CreatePumpkinRoot(PumpkinSettingsTargetingHar);
        try
        {
            var pumpkin = CreatePumpkinMod(pumpkinRoot, packageId: "pumpkin.pumpkinlibrary_steam", packageIdPlayerFacing: "pumpkin.pumpkinlibrary");
            var har = CreateMod("erdelf.humanoidalienraces", @"C:\HarRoot\");
            var provider = CreateMod("pumpkin.harddspack_steam", @"C:\DdsPackRoot\", "Pumpkin.HarDdsPack");

            WithRunningMods([pumpkin, har, provider], ProtectedMods.InitializeTextureRoots);

            Assert.That(ProtectedMods.GetProtectedTextureRoots(), Is.EquivalentTo(["C:/HarRoot", "C:/DdsPackRoot"]));
        }
        finally
        {
            System.IO.Directory.Delete(pumpkinRoot, recursive: true);
        }
    }

    [Test]
    public void TextureRoots_PreferRootLevelPumpkinSettingsLikePumpkin()
    {
        // Pumpkin 先找 mod 根目錄的設定檔，找到就不看 1.7/Modules/TextureOverride 下的那份。
        var pumpkinRoot = CreatePumpkinRoot(PumpkinSettings("some.other.mod", "pumpkin.otherpack"));
        try
        {
            WritePumpkinSettings(pumpkinRoot, string.Empty, PumpkinSettingsTargetingHar);
            var pumpkin = CreatePumpkinMod(pumpkinRoot);
            var har = CreateMod("erdelf.humanoidalienraces", @"C:\HarRoot\");
            var provider = CreateMod("pumpkin.harddspack", @"C:\DdsPackRoot\");
            var otherProvider = CreateMod("pumpkin.otherpack", @"C:\OtherPackRoot\");

            WithRunningMods([pumpkin, har, provider, otherProvider], ProtectedMods.InitializeTextureRoots);

            Assert.That(ProtectedMods.GetProtectedTextureRoots(), Is.EquivalentTo(["C:/HarRoot", "C:/DdsPackRoot"]));
        }
        finally
        {
            System.IO.Directory.Delete(pumpkinRoot, recursive: true);
        }
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
