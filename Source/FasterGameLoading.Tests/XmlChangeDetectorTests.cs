using System.Collections.Generic;
using NUnit.Framework;
using System.Reflection;

namespace FasterGameLoading
{
    /// <summary>
    /// XmlChangeDetector.CombineMetadataHashes 的純函式單元測試。
    /// 不需 RimWorld 執行環境（不觸發 SessionCache、ModsConfig、檔案系統），
    /// 僅驗證確定性折疊的順序敏感與碰撞避免性質。
    /// </summary>
    [TestFixture]
    public class XmlChangeDetectorTests
    {
        [Test]
        public void CombineMetadataHashes_EmptyDictionary_ReturnsZero()
        {
            Assert.AreEqual(0L, XmlChangeDetector.CombineMetadataHashes(new Dictionary<string, long>()));
        }

        [Test]
        public void CombineMetadataHashes_SingleEntry_FoldsBy31()
        {
            // unchecked(0 * 31 + v) == v
            var input = new Dictionary<string, long> { ["a"] = 42L };
            Assert.AreEqual(42L, XmlChangeDetector.CombineMetadataHashes(input));
        }

        [Test]
        public void CombineMetadataHashes_IsDeterministicAcrossInsertionOrder()
        {
            // 同一組鍵值、不同插入順序應產生相同結果（內部以序排序）。
            var a = new Dictionary<string, long>
            {
                ["modZ"] = 1L,
                ["modA"] = 2L,
                ["modM"] = 3L,
            };
            var b = new Dictionary<string, long>
            {
                ["modM"] = 3L,
                ["modZ"] = 1L,
                ["modA"] = 2L,
            };

            Assert.AreEqual(
                XmlChangeDetector.CombineMetadataHashes(a),
                XmlChangeDetector.CombineMetadataHashes(b));
        }

        [Test]
        public void CombineMetadataHashes_IsOrderSensitive()
        {
            // 順序敏感的 polynomial hash：交換兩個值應改變結果。
            var first = new Dictionary<string, long> { ["a"] = 1L, ["b"] = 2L };
            var second = new Dictionary<string, long> { ["a"] = 2L, ["b"] = 1L };

            Assert.AreNotEqual(
                XmlChangeDetector.CombineMetadataHashes(first),
                XmlChangeDetector.CombineMetadataHashes(second));
        }

        [Test]
        public void CombineMetadataHashes_DifferentMetadataValues_ProduceDifferentHash()
        {
            // 真正的 mod 組合變更會帶來不同的 per-mod metadata values，
            // 此處驗證「values 不同 → 合計不同」這條主要失效偵測路徑。
            var a = new Dictionary<string, long> { ["modA"] = 1L, ["modB"] = 2L };
            var b = new Dictionary<string, long> { ["modA"] = 1L, ["modB"] = 999L };

            Assert.AreNotEqual(
                XmlChangeDetector.CombineMetadataHashes(a),
                XmlChangeDetector.CombineMetadataHashes(b));
        }

        [Test]
        public void CombineMetadataHashes_FoldsWithPolynomialFactor31()
        {
            // 確認乘 31 的折疊公式長相，防止回歸到「加法折疊」之類的弱雜湊。
            // combined_aB = (0 * 31 + 1) * 31 + 2 = 33
            var input = new Dictionary<string, long> { ["a"] = 1L, ["b"] = 2L };
            Assert.AreEqual(33L, XmlChangeDetector.CombineMetadataHashes(input));
        }

        [Test]
        public void ScanXmlMetadata_DoesNotCommitSessionState()
        {
            var original = SessionCache.xmlMetadataHashByMod;
            try
            {
                SessionCache.xmlMetadataHashByMod = new Dictionary<string, long> { ["existing"] = 7L };

                var result = XmlChangeDetector.ScanXmlMetadata(new List<string>(), null);

                Assert.IsTrue(result.Bypassed);
                Assert.AreEqual(7L, SessionCache.xmlMetadataHashByMod["existing"]);
            }
            finally
            {
                SessionCache.xmlMetadataHashByMod = original;
            }
        }

        [Test]
        public void ScanXmlMetadata_IgnoresConfigXml()
        {
            var missileGirlField = typeof(Utils).GetField("isMissileGirlActive", BindingFlags.NonPublic | BindingFlags.Static);
            var originalMissileGirlValue = missileGirlField.GetValue(null);
            var modPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FGL_Test_Mod_" + System.Guid.NewGuid());
            var configPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FGL_Test_Config_" + System.Guid.NewGuid());
            var defsPath = System.IO.Path.Combine(modPath, "Defs");
            System.IO.Directory.CreateDirectory(defsPath);
            System.IO.Directory.CreateDirectory(configPath);
            System.IO.File.WriteAllText(System.IO.Path.Combine(defsPath, "test.xml"), "<Defs />");
            var configFile = System.IO.Path.Combine(configPath, "OtherMod.xml");
            System.IO.File.WriteAllText(configFile, "<settings>first</settings>");

            try
            {
                missileGirlField.SetValue(null, false);
                Assert.AreEqual(false, missileGirlField.GetValue(null));
                var first = XmlChangeDetector.ScanXmlMetadata(new List<string> { modPath }, configPath);
                System.IO.File.WriteAllText(configFile, "<settings>changed</settings>");
                System.IO.File.SetLastWriteTimeUtc(configFile, System.DateTime.UtcNow.AddSeconds(5));
                var second = XmlChangeDetector.ScanXmlMetadata(new List<string> { modPath }, configPath);

                Assert.AreEqual(first.MetadataHashes[modPath.ToLowerInvariant()], second.MetadataHashes[modPath.ToLowerInvariant()]);
                Assert.False(second.MetadataHashes.ContainsKey(configPath.ToLowerInvariant()));
            }
            finally
            {
                missileGirlField.SetValue(null, originalMissileGirlValue);
                System.IO.Directory.Delete(modPath, true);
                System.IO.Directory.Delete(configPath, true);
            }
        }
    }
}
