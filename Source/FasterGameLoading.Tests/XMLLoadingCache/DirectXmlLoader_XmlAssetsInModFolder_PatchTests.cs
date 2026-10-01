using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;
using Verse;

namespace FasterGameLoading.Tests.XMLLoadingCache
{
    [TestFixture]
    public class DirectXmlLoader_XmlAssetsInModFolder_PatchTests
    {
        private string tempDir;
        private bool origEnableMultiThreading;

        [SetUp]
        public void SetUp()
        {
            origEnableMultiThreading = FasterGameLoadingSettings.EnableMultiThreading;
            tempDir = Path.Combine(Path.GetTempPath(), "FGL_XmlLoader_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            FasterGameLoadingSettings.EnableMultiThreading = origEnableMultiThreading;
            HyperdriveCompat.ParallelizesModDefs = false;
            try
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex)
            {
                Assert.Fail("清理 XML Loader 測試暫存目錄失敗：" + tempDir + Environment.NewLine + ex);
            }
        }

        private static ModContentPack CreateMockModContentPack(string packageId = "test.mod")
        {
            var mod = (ModContentPack)FormatterServices.GetUninitializedObject(typeof(ModContentPack));
            var packageIdField = AccessTools.Field(typeof(ModContentPack), "packageIdInt")
                ?? AccessTools.Field(typeof(ModContentPack), "packageId");
            packageIdField?.SetValue(mod, packageId);
            return mod;
        }

        [Test]
        public void Prefix_WhenModIsNull_ReturnsTrue()
        {
            FasterGameLoadingSettings.EnableMultiThreading = true;
            LoadableXmlAsset[] result = null;

            var ret = DirectXmlLoader_XmlAssetsInModFolder_Patch.Prefix(
                ref result, mod: null, folderPath: "Defs", foldersToLoadDebug: null);

            Assert.That(ret, Is.True);
            Assert.That(result, Is.Null);
        }

        [Test]
        public void Prefix_WhenMultiThreadingIsDisabled_ReturnsTrue()
        {
            FasterGameLoadingSettings.EnableMultiThreading = false;
            var mod = CreateMockModContentPack("test.normalmod");
            LoadableXmlAsset[] result = null;

            var ret = DirectXmlLoader_XmlAssetsInModFolder_Patch.Prefix(
                ref result, mod, "Defs", foldersToLoadDebug: null);

            Assert.That(ret, Is.True);
            Assert.That(result, Is.Null);
        }

        [Test]
        public void Prefix_WhenModInSkipList_ReturnsTrue()
        {
            FasterGameLoadingSettings.EnableMultiThreading = true;
            var mod = CreateMockModContentPack("erdelf.HumanoidAlienRaces");
            LoadableXmlAsset[] result = null;

            var ret = DirectXmlLoader_XmlAssetsInModFolder_Patch.Prefix(
                ref result, mod, "Defs", foldersToLoadDebug: null);

            Assert.That(ret, Is.True);
            Assert.That(result, Is.Null);
        }

        [Test]
        public void Prefix_WhenNoXmlFilesFound_ReturnsFalseAndEmptyArray()
        {
            FasterGameLoadingSettings.EnableMultiThreading = true;
            var mod = CreateMockModContentPack("test.emptymod");
            var emptyRoot = Path.Combine(tempDir, "EmptyModRoot");
            Directory.CreateDirectory(emptyRoot);
            LoadableXmlAsset[] result = null;

            var ret = DirectXmlLoader_XmlAssetsInModFolder_Patch.Prefix(
                ref result, mod, "Defs", new List<string> { emptyRoot });

            Assert.That(ret, Is.False);
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void Prefix_WhenXmlFilesExist_ParsesInParallelAndReturnsFalse()
        {
            FasterGameLoadingSettings.EnableMultiThreading = true;
            var mod = CreateMockModContentPack("test.validmod");
            var modRoot = Path.Combine(tempDir, "ValidModRoot");
            var defsFolder = Path.Combine(modRoot, "Defs");
            Directory.CreateDirectory(defsFolder);

            File.WriteAllText(Path.Combine(defsFolder, "Items.xml"), "<Defs><ThingDef><defName>TestItem1</defName></ThingDef></Defs>");
            File.WriteAllText(Path.Combine(defsFolder, "Buildings.xml"), "<Defs><ThingDef><defName>TestBuilding1</defName></ThingDef></Defs>");

            LoadableXmlAsset[] result = null;

            var ret = DirectXmlLoader_XmlAssetsInModFolder_Patch.Prefix(
                ref result, mod, "Defs", new List<string> { modRoot });

            Assert.That(ret, Is.False);
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Has.Length.EqualTo(2));
            var names = result.Select(a => a.name).ToList();
            Assert.That(names, Contains.Item("Items.xml"));
            Assert.That(names, Contains.Item("Buildings.xml"));
        }

        [Test]
        public void Prefix_WhenHyperdriveParallelizesDefs_LeavesDefsToOriginal()
        {
            FasterGameLoadingSettings.EnableMultiThreading = true;
            HyperdriveCompat.ParallelizesModDefs = true;
            var mod = CreateMockModContentPack("test.validmod");
            LoadableXmlAsset[] result = null;

            var ret = DirectXmlLoader_XmlAssetsInModFolder_Patch.Prefix(
                ref result, mod, "Defs/", foldersToLoadDebug: null);

            Assert.That(ret, Is.True);
            Assert.That(result, Is.Null);
        }

        [Test]
        public void Prefix_WhenHyperdriveParallelizesDefs_StillParsesPatchesInParallel()
        {
            // Hyperdrive 只跨 mod 平行化 LoadModXML（Defs/）；Patches/ 仍逐 mod 載入，FGL 照常平行。
            FasterGameLoadingSettings.EnableMultiThreading = true;
            HyperdriveCompat.ParallelizesModDefs = true;
            var mod = CreateMockModContentPack("test.validmod");
            var modRoot = Path.Combine(tempDir, "PatchesModRoot");
            var patchesFolder = Path.Combine(modRoot, "Patches");
            Directory.CreateDirectory(patchesFolder);
            File.WriteAllText(Path.Combine(patchesFolder, "Patch.xml"), "<Patch></Patch>");
            LoadableXmlAsset[] result = null;

            var ret = DirectXmlLoader_XmlAssetsInModFolder_Patch.Prefix(
                ref result, mod, "Patches/", new List<string> { modRoot });

            Assert.That(ret, Is.False);
            Assert.That(result, Has.Length.EqualTo(1));
        }
    }
}
