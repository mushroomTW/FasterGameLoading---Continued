using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Xml;
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

            // 3. 手動套用 XmlNode.SelectSingleNode 補丁
            var selectSingleNodeOriginal = typeof(XmlNode).GetMethod(nameof(XmlNode.SelectSingleNode), new Type[] { typeof(string) });
            var selectSingleNodePrefix = AccessTools.Method(typeof(XmlNode_SelectSingleNode_Patch), nameof(XmlNode_SelectSingleNode_Patch.Prefix));
            var selectSingleNodePostfix = AccessTools.Method(typeof(XmlNode_SelectSingleNode_Patch), nameof(XmlNode_SelectSingleNode_Patch.Postfix));
            harmony.Patch(selectSingleNodeOriginal, prefix: new HarmonyMethod(selectSingleNodePrefix), postfix: new HarmonyMethod(selectSingleNodePostfix));
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
            SessionCache.loadedTypesByFullNameSinceLastSession.Clear();
            SessionCache.xmlPathsSinceLastSession.Clear();
            XmlNode_SelectSingleNode_Patch.xmlPathsThisSession.Clear();

            // 手動啟用 XML 掃描完成標記，以便測試快取攔截邏輯
            XmlNode_SelectSingleNode_Patch.isXmlScanComplete = true;

            // 重設 Settings 以防其他測試修改
            FasterGameLoadingSettings.XPathCaching = true;

            // 重設 patchEnabled
            var patchEnabledField = typeof(XmlNode_SelectSingleNode_Patch).GetField("patchEnabled", BindingFlags.NonPublic | BindingFlags.Static);
            if (patchEnabledField != null)
            {
                patchEnabledField.SetValue(null, true);
            }
        }


        [TearDown]
        public void TearDown()
        {
            // 清理靜態快取
            GenTypes_GetTypeInAnyAssemblyInt_Patch.ClearCache();
            SessionCache.loadedTypesByFullNameSinceLastSession.Clear();
            SessionCache.xmlPathsSinceLastSession.Clear();
            XmlNode_SelectSingleNode_Patch.xmlPathsThisSession.Clear();

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
        public void TestEarlyLoadSkipList_AppliesPackageIdSkipRules()
        {
            Assert.IsFalse(EarlyLoadSkipList.ShouldSkip("chezhou.chezhoulib.lib"));
            Assert.IsTrue(EarlyLoadSkipList.ShouldSkip("Ayameduki.SomeMod"));
            Assert.IsTrue(EarlyLoadSkipList.ShouldSkip("erdelf.HumanoidAlienRaces"));
            Assert.IsTrue(EarlyLoadSkipList.ShouldSkip("some.race.mod", new FakeModMetaData()));
        }

        [Test]
        public void TestTextureReverseCache_DoesNotHoldStrongTextureKeys()
        {
            var field = typeof(ModContentLoaderTexture2D_LoadTexture_Patch)
                .GetField("savedTextures", BindingFlags.Public | BindingFlags.Static);

            Assert.IsNotNull(field);
            Assert.AreEqual(typeof(ConcurrentDictionary<string, System.WeakReference<Texture2D>>), field.FieldType);
        }

        private sealed class FakeModMetaData
        {
            public List<FakeDependency> modDependencies = new List<FakeDependency>
            {
                new FakeDependency { packageId = "erdelf.HumanoidAlienRaces" }
            };
        }

        private sealed class FakeDependency
        {
            public string packageId;
        }

        [Test]
        public void TestAlienRacesCompat_IsRemoved()
        {
            Assert.IsNull(
                typeof(FasterGameLoadingMod).Assembly.GetType("FasterGameLoading.AlienRacesCompat"),
                "HAR 相容性應靠 early-loading skip 保留原生時序，不再用非冪等的事後重掃。");
        }

        [Test]
        public void TestXmlNode_NoPatch()
        {
            var doc = new XmlDocument();
            doc.LoadXml("<root><missing>exists</missing></root>");
            string xpath = "/root/missing";
            var result = doc.SelectSingleNode(xpath);
            Assert.IsNotNull(result, "Baseline SelectSingleNode should not return null without patches");
        }

        [Test]
        public void TestAccessTools_TypeByName_Patch_HitsRuntimeCache()
        {
            // 1. 注入 mock 型別到執行期快取中
            var mockType = typeof(string);
            string typeName = "MockTypeForTest";
            GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults[typeName] = mockType;

            // 2. 呼叫目標方法，應被 Prefix 攔截並直接返回快取型別
            var resolvedType = AccessTools.TypeByName(typeName);

            // 3. 斷言結果與快取相符
            Assert.AreEqual(mockType, resolvedType);
        }

        [Test]
        public void TestAccessTools_TypeByName_Patch_HitsSessionCache()
        {
            // 1. 設定跨 session 型別對照（將簡稱對應至 System.Int32 完整型別名）
            string shortName = "MyIntType";
            SessionCache.loadedTypesByFullNameSinceLastSession[shortName] = typeof(int).FullName;

            // 2. 呼叫目標方法
            var resolvedType = AccessTools.TypeByName(shortName);

            // 3. 斷言應該被正確解析為 System.Int32
            Assert.AreEqual(typeof(int), resolvedType);

            // 4. 驗證新解析出來的型別也被更新至執行期快取中
            Assert.IsTrue(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults.ContainsKey(shortName));
            Assert.IsTrue(GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults.ContainsKey(typeof(int).FullName));
        }

        [Test]
        public void TestAccessTools_TypeByName_Patch_DoesNotCacheNullResult()
        {
            string missingTypeName = "DefinitelyMissingTypeForFGLTest";

            AccessTools_TypeByName_Patch.Postfix(
                null,
                missingTypeName,
                (isCached: false, originalName: missingTypeName));

            Assert.IsFalse(
                GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults.ContainsKey(missingTypeName),
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
        public void TestXmlNode_SelectSingleNode_Patch_Prefix_InterceptsCachedMissingNode()
        {
            // 1. 模擬跨 session 快取記錄：/root/missing 節點是不存在的 (false)
            string xpath = "/root/missing";
            SessionCache.xmlPathsSinceLastSession.TryAdd(xpath, 0);

            // 2. 建立一個包含此節點的 XML 檔案（實際上它是存在的）
            var doc = new XmlDocument();
            doc.LoadXml("<root><missing>exists</missing></root>");

            // 3. 進行查詢，因為快取中判定它不存在，應被 Prefix 攔截直接返回 null
            var result = doc.SelectSingleNode(xpath);

            // 4. 斷言結果為 null
            Assert.IsNull(result);
        }

        [Test]
        public void TestXmlNode_SelectSingleNode_Patch_BypassesWhenXmlExtensionsActive()
        {
            // 1. 模擬跨 session 快取記錄：/root/missing 節點是不存在的 (false)
            string xpath = "/root/missing";
            SessionCache.xmlPathsSinceLastSession.TryAdd(xpath, 0);

            // 2. 建立一個包含此節點的 XML 檔案（實際上它是存在的）
            var doc = new XmlDocument();
            doc.LoadXml("<root><missing>exists</missing></root>");

            // 3. 用反射強行將 isXmlExtensionsActive 設為 true 模擬 XML Extensions 啟用
            var field = typeof(XmlNode_SelectSingleNode_Patch).GetField("isXmlExtensionsActive", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field);
            field.SetValue(null, (bool?)true);

            try
            {
                // 4. 進行查詢，此時因為 XML Extensions 啟用，Prefix 應該直接放行，返回真實存在的節點
                var result = doc.SelectSingleNode(xpath);
                Assert.IsNotNull(result, "Should bypass cache and return the node because XML Extensions is active");
            }
            finally
            {
                // 重設狀態
                field.SetValue(null, (bool?)null);
            }
        }

        [Test]
        public void TestXmlNode_SelectSingleNode_Patch_Postfix_RecordsNodes()
        {
            // 1. 建立測試用的 XML
            var doc = new XmlDocument();
            doc.LoadXml("<root><item>hello</item></root>");

            // 2. 使用變數進行查詢，避免編譯器常數摺疊或 JIT 優化導致內聯
            string pathItem = "/root/item";
            string pathNonexistent = "/root/nonexistent";
            var nodeFound = doc.SelectSingleNode(pathItem);
            var nodeMissing = doc.SelectSingleNode(pathNonexistent);

            // 3. 斷言基本查詢結果正確
            Assert.IsNotNull(nodeFound);
            Assert.IsNull(nodeMissing);

            // 4. 驗證 Postfix 有將這些查詢結果記錄到當前 session 快取中
            Assert.IsTrue(XmlNode_SelectSingleNode_Patch.xmlPathsThisSession.TryGetValue(pathItem, out bool foundStatus), $"Should contain key: {pathItem}");
            Assert.IsTrue(foundStatus);

            Assert.IsTrue(XmlNode_SelectSingleNode_Patch.xmlPathsThisSession.TryGetValue(pathNonexistent, out bool missingStatus), $"Should contain key: {pathNonexistent}");
            Assert.IsFalse(missingStatus);
        }

        [Test]
        public void TestXmlNode_SelectSingleNode_Patch_Postfix_PreservesAnyPreviousMatch()
        {
            const string xpath = "/root/item";
            var document = new XmlDocument();
            document.LoadXml("<root><item /></root>");

            XmlNode_SelectSingleNode_Patch.Postfix(xpath, document.SelectSingleNode(xpath), true);
            XmlNode_SelectSingleNode_Patch.Postfix(xpath, null, true);

            Assert.IsTrue(XmlNode_SelectSingleNode_Patch.xmlPathsThisSession[xpath],
                "An XPath that matched in any XML context must never be persisted as missing.");
        }

        [Test]
        public void TestXmlNode_SelectSingleNode_Patch_ConcurrencySafety()
        {
            // 模擬跨 session 快取中有一部分已知不存在的節點
            for (int i = 0; i < 50; i++)
            {
                SessionCache.xmlPathsSinceLastSession.TryAdd($"/root/missing_{i}", 0);
            }

            var nodes = new XmlNode[500];

            // 多執行緒併發呼叫 SelectSingleNode
            System.Threading.Tasks.Parallel.For(0, 500, i =>
            {
                var doc = new XmlDocument();
                doc.LoadXml("<root><item>hello</item></root>");

                // 測試命中快取攔截的 xpath
                string xpathMissing = $"/root/missing_{i % 50}";
                nodes[i] = doc.SelectSingleNode(xpathMissing);

                // 測試未命中快取但實際不存在，觸發 Postfix 記錄的 xpath
                string xpathNew = $"/root/new_{i}";
                doc.SelectSingleNode(xpathNew);
            });

            // 在主執行緒統一斷言
            for (int i = 0; i < 500; i++)
            {
                Assert.IsNull(nodes[i]);
            }

            // 驗證 ConcurrentDictionary 記錄正確
            Assert.AreEqual(500, XmlNode_SelectSingleNode_Patch.xmlPathsThisSession.Count);
            for (int i = 0; i < 500; i++)
            {
                Assert.IsTrue(XmlNode_SelectSingleNode_Patch.xmlPathsThisSession.TryGetValue($"/root/new_{i}", out bool status));
                Assert.IsFalse(status);
            }
        }

        [Test]
        public void TestXmlChangeDetector_CalculatesConsistentHash()
        {
            // 1. 建立一個臨時資料夾並寫入 XML 檔案
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FGL_Test_Mods_" + Guid.NewGuid());
            string defsPath = System.IO.Path.Combine(tempDir, "Defs");
            System.IO.Directory.CreateDirectory(defsPath);

            string xmlFile = System.IO.Path.Combine(defsPath, "test.xml");
            System.IO.File.WriteAllText(xmlFile, "<Defs><ThingDef>mock</ThingDef></Defs>");

            try
            {
                // 2. 重設掃描狀態
                XmlNode_SelectSingleNode_Patch.isXmlScanComplete = false;
                SessionCache.xmlCombinedHashSinceLastSession = 0;
                XmlChangeDetector.needWriteSettings = false;

                // 3. 呼叫掃描
                XmlChangeDetector.ScanXmlFiles(new List<string> { tempDir });

                // 4. 驗證掃描完成與雜湊儲存
                Assert.IsTrue(XmlNode_SelectSingleNode_Patch.isXmlScanComplete);
                long hash1 = SessionCache.xmlCombinedHashSinceLastSession;
                Assert.AreNotEqual(0, hash1);
                Assert.IsTrue(XmlChangeDetector.needWriteSettings);

                // 5. 修改檔案內容並重新掃描，雜湊應變更
                XmlChangeDetector.needWriteSettings = false;
                XmlNode_SelectSingleNode_Patch.isXmlScanComplete = false;

                // 稍微延遲並更新 LastWriteTime 確保變更
                System.IO.File.SetLastWriteTimeUtc(xmlFile, DateTime.UtcNow.AddSeconds(5));
                System.IO.File.WriteAllText(xmlFile, "<Defs><ThingDef>mock_modified</ThingDef></Defs>");

                XmlChangeDetector.ScanXmlFiles(new List<string> { tempDir });

                Assert.IsTrue(XmlNode_SelectSingleNode_Patch.isXmlScanComplete);
                long hash2 = SessionCache.xmlCombinedHashSinceLastSession;
                Assert.AreNotEqual(hash1, hash2);
                Assert.IsTrue(XmlChangeDetector.needWriteSettings);
            }
            finally
            {
                // 清理臨時資料夾
                if (System.IO.Directory.Exists(tempDir))
                {
                    System.IO.Directory.Delete(tempDir, true);
                }
            }
        }


        [Test]
        public void TestXmlChangeDetector_UsesMetadataOnlyForCacheVersion()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FGL_Test_Metadata_" + Guid.NewGuid());
            string defsPath = System.IO.Path.Combine(tempDir, "Defs");
            System.IO.Directory.CreateDirectory(defsPath);

            string xmlFile = System.IO.Path.Combine(defsPath, "test.xml");
            var originalTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            System.IO.File.WriteAllText(xmlFile, "<Defs><ThingDef>aaaa</ThingDef></Defs>");
            System.IO.File.SetLastWriteTimeUtc(xmlFile, originalTime);

            try
            {
                XmlNode_SelectSingleNode_Patch.isXmlScanComplete = false;
                SessionCache.xmlCombinedHashSinceLastSession = 0;
                SessionCache.xmlMetadataHashByMod.Clear();
                SessionCache.xmlContentHashByMod.Clear();
                XmlChangeDetector.needWriteSettings = false;

                XmlChangeDetector.ScanXmlFiles(new List<string> { tempDir });
                var hash1 = SessionCache.xmlCombinedHashSinceLastSession;
                Assert.AreNotEqual(0, hash1);
                Assert.IsTrue(XmlChangeDetector.needWriteSettings);

                XmlChangeDetector.needWriteSettings = false;
                XmlNode_SelectSingleNode_Patch.isXmlScanComplete = false;
                System.IO.File.SetLastWriteTimeUtc(xmlFile, originalTime.AddSeconds(5));

                XmlChangeDetector.ScanXmlFiles(new List<string> { tempDir });
                Assert.AreNotEqual(hash1, SessionCache.xmlCombinedHashSinceLastSession);
                Assert.IsTrue(XmlChangeDetector.needWriteSettings);
            }
            finally
            {
                if (System.IO.Directory.Exists(tempDir))
                {
                    System.IO.Directory.Delete(tempDir, true);
                }
            }
        }

        [Test]
        public void TestXmlChangeDetector_MetadataHashIncludesXmlPath()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FGL_Test_Metadata_Path_" + Guid.NewGuid());
            string defsPath = System.IO.Path.Combine(tempDir, "Defs");
            System.IO.Directory.CreateDirectory(defsPath);

            string firstFile = System.IO.Path.Combine(defsPath, "A.xml");
            string secondFile = System.IO.Path.Combine(defsPath, "B.xml");
            var originalTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            System.IO.File.WriteAllText(firstFile, "<Defs><ThingDef>same</ThingDef></Defs>");
            System.IO.File.SetLastWriteTimeUtc(firstFile, originalTime);

            try
            {
                XmlNode_SelectSingleNode_Patch.isXmlScanComplete = false;
                SessionCache.xmlCombinedHashSinceLastSession = 0;
                SessionCache.xmlMetadataHashByMod.Clear();
                SessionCache.xmlContentHashByMod.Clear();
                XmlChangeDetector.needWriteSettings = false;

                XmlChangeDetector.ScanXmlFiles(new List<string> { tempDir });
                var hash1 = SessionCache.xmlCombinedHashSinceLastSession;

                System.IO.File.Move(firstFile, secondFile);
                System.IO.File.SetLastWriteTimeUtc(secondFile, originalTime);
                XmlChangeDetector.needWriteSettings = false;
                XmlNode_SelectSingleNode_Patch.isXmlScanComplete = false;

                XmlChangeDetector.ScanXmlFiles(new List<string> { tempDir });

                Assert.AreNotEqual(hash1, SessionCache.xmlCombinedHashSinceLastSession);
                Assert.IsTrue(XmlChangeDetector.needWriteSettings);
            }
            finally
            {
                if (System.IO.Directory.Exists(tempDir))
                {
                    System.IO.Directory.Delete(tempDir, true);
                }
            }
        }

        [Test]
        public void TestXmlChangeDetector_MetadataHashIncludesXmlFolder()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FGL_Test_Metadata_Folder_" + Guid.NewGuid());
            string defsPath = System.IO.Path.Combine(tempDir, "Defs");
            string patchesPath = System.IO.Path.Combine(tempDir, "Patches");
            System.IO.Directory.CreateDirectory(defsPath);
            System.IO.Directory.CreateDirectory(patchesPath);

            string defsFile = System.IO.Path.Combine(defsPath, "Same.xml");
            string patchesFile = System.IO.Path.Combine(patchesPath, "Same.xml");
            var originalTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            System.IO.File.WriteAllText(defsFile, "<Defs><ThingDef>same</ThingDef></Defs>");
            System.IO.File.SetLastWriteTimeUtc(defsFile, originalTime);

            try
            {
                XmlNode_SelectSingleNode_Patch.isXmlScanComplete = false;
                SessionCache.xmlCombinedHashSinceLastSession = 0;
                SessionCache.xmlMetadataHashByMod.Clear();
                SessionCache.xmlContentHashByMod.Clear();
                XmlChangeDetector.needWriteSettings = false;

                XmlChangeDetector.ScanXmlFiles(new List<string> { tempDir });
                var hash1 = SessionCache.xmlCombinedHashSinceLastSession;

                System.IO.File.Move(defsFile, patchesFile);
                System.IO.File.SetLastWriteTimeUtc(patchesFile, originalTime);
                XmlChangeDetector.needWriteSettings = false;
                XmlNode_SelectSingleNode_Patch.isXmlScanComplete = false;

                XmlChangeDetector.ScanXmlFiles(new List<string> { tempDir });

                Assert.AreNotEqual(hash1, SessionCache.xmlCombinedHashSinceLastSession);
                Assert.IsTrue(XmlChangeDetector.needWriteSettings);
            }
            finally
            {
                if (System.IO.Directory.Exists(tempDir))
                {
                    System.IO.Directory.Delete(tempDir, true);
                }
            }
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

        [TestCase(false, false)]
        [TestCase(true, true)]
        public void TestDelayedActions_ShouldRunDeferredVisualPipeline_FollowsDelayGraphicLoading(
            bool delayGraphicLoading,
            bool expected)
        {
            bool originalDelayGraphicLoading = FasterGameLoadingSettings.DelayGraphicLoading;
            try
            {
                FasterGameLoadingSettings.DelayGraphicLoading = delayGraphicLoading;

                Assert.AreEqual(expected, DelayedActions.ShouldRunDeferredVisualPipeline());
            }
            finally
            {
                FasterGameLoadingSettings.DelayGraphicLoading = originalDelayGraphicLoading;
            }
        }

        [Test]
        public void TestBuildableDef_PostLoad_Patch_RoutesIconCallbacksToDelayedHandler()
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
        public void TestTexturePathReverseLookup_UsesSavedTexturePath()
        {
            var texture = (Texture2D)FormatterServices.GetUninitializedObject(typeof(Texture2D));
            const string path = @"C:\Mods\Test\Textures\Thing.png";
            var saveTexturePath = typeof(ModContentLoaderTexture2D_LoadTexture_Patch)
                .GetMethod("SaveTexturePath", BindingFlags.NonPublic | BindingFlags.Static);

            try
            {
                Assert.IsNotNull(saveTexturePath);
                saveTexturePath.Invoke(null, new object[] { path, texture });

                Assert.IsTrue(ModContentLoaderTexture2D_LoadTexture_Patch.TryGetSavedTexturePath(texture, out var foundPath));
                Assert.AreEqual(path, foundPath);
            }
            finally { }
        }

        [Test]
        public void TestAdaptiveBakingSkipList_ProtectsAlienRaceTextureRoots()
        {
            var type = typeof(AdaptiveBakingSkipList);
            var targetMods = (HashSet<string>)type.GetField("targetMods", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            CollectionAssert.Contains(targetMods, "erdelf.HumanoidAlienRaces");

            var roots = (HashSet<string>)type.GetField("targetModRoots", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var rootsInitialized = type.GetField("rootsInitialized", BindingFlags.NonPublic | BindingFlags.Static);
            var oldRoots = roots.ToList();
            var oldRootsInitialized = (bool)rootsInitialized.GetValue(null);

            try
            {
                roots.Clear();
                roots.Add("C:/Mods/AlienRaces");
                rootsInitialized.SetValue(null, true);

                Assert.IsTrue(AdaptiveBakingSkipList.IsProtectedModTexturePath(@"C:\Mods\AlienRaces\Textures\Body.png"));
                Assert.IsFalse(AdaptiveBakingSkipList.IsProtectedModTexturePath(@"C:\Mods\Other\Textures\Body.png"));
            }
            finally
            {
                roots.Clear();
                foreach (var root in oldRoots) roots.Add(root);
                rootsInitialized.SetValue(null, oldRootsInitialized);
            }
        }

        [Test]
        public void TestEarlyModContentLoader_DoesNotBypassWhenImageOptActive()
        {
            var update = typeof(EarlyModContentLoader).GetMethod(nameof(EarlyModContentLoader.Update));
            var imageOptActiveGetter = typeof(ImageOptCompat)
                .GetProperty(nameof(ImageOptCompat.IsActive))
                .GetGetMethod();

            Assert.IsFalse(
                MethodBodyContainsMetadataToken(update, imageOptActiveGetter),
                "Image Opt should only bypass FGL texture replacement, not early content loading.");
        }

        [Test]
        public void TestEarlyModContentLoader_DoesNotWaitForVanillaLoadModContent()
        {
            var update = typeof(EarlyModContentLoader).GetMethod(nameof(EarlyModContentLoader.Update));
            var gate = typeof(DelayedActions).GetField("VanillaModContentLoadCompleted");

            Assert.IsFalse(
                gate != null && MethodBodyContainsMetadataToken(update, gate.MetadataToken),
                "Early content loading must run before vanilla ReloadContentInt starts, otherwise the normal loader can consume the whole queue first.");
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
        public void TestUtils_IsMissileGirlActiveCache_IsResetByCacheResetter()
        {
            var field = typeof(Utils).GetField("isMissileGirlActive", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field);

            field.SetValue(null, true);
            CacheResetter.ResetAll();

            Assert.IsNull(field.GetValue(null), "CacheResetter should clear the cached Missile Girl active state.");
        }

        [Test]
        public void TestStartupPostfix_UsesExecuteWhenFinishedForCompletionActions()
        {
            var postfix = typeof(Startup).GetMethod(nameof(Startup.Postfix));
            var executeWhenFinished = AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.ExecuteWhenFinished));

            Assert.IsTrue(
                MethodBodyReferencesMethod(postfix, executeWhenFinished),
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

    }
}
