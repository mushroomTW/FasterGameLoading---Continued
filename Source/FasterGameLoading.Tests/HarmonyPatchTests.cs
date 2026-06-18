using System;
using System.Collections.Generic;
using System.Reflection;
using System.Xml;
using HarmonyLib;
using NUnit.Framework;
using System.Linq;
using System.Threading;
using Verse;
using UnityEngine;


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
        public void TestXmlNode_SelectSingleNode_Patch_ConcurrencySafety()
        {
            // 模擬跨 session 快取中有一部分已知不存在的節點
            for (int i = 0; i < 50; i++)
            {
                SessionCache.xmlPathsSinceLastSession.TryAdd($"/root/missing_{i}", 0);
            }

            // 多執行緒併發呼叫 SelectSingleNode
            System.Threading.Tasks.Parallel.For(0, 500, i =>
            {
                var doc = new XmlDocument();
                doc.LoadXml("<root><item>hello</item></root>");

                // 測試命中快取攔截的 xpath
                string xpathMissing = $"/root/missing_{i % 50}";
                var node = doc.SelectSingleNode(xpathMissing);
                Assert.IsNull(node);

                // 測試未命中快取但實際不存在，觸發 Postfix 記錄的 xpath
                string xpathNew = $"/root/new_{i}";
                doc.SelectSingleNode(xpathNew);
            });

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


    }
}
