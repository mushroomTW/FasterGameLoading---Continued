using NUnit.Framework;
using System.Collections.Generic;

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
            var first  = new Dictionary<string, long> { ["a"] = 1L, ["b"] = 2L };
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
    }
}
