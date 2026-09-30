using NUnit.Framework;
using UnityEngine;

namespace FasterGameLoading.Tests.StaticAtlasOptimizations
{
    [TestFixture]
    public class StaticTextureAtlas_CalcRectsForAtlasNew_PatchTests
    {
        /// <summary>
        /// 16384 寬的圖集有 6 層 mipmap：每層保留一列空白是原尺寸 32 列，高度以 128 列為單位。
        /// A 16384-wide atlas has 6 mip levels: one empty row per level is 32 rows at full size, in steps of 128 rows.
        /// </summary>
        [TestCase(4300, 6, 4352)]
        [TestCase(100, 1, 104)]
        [TestCase(123, 1, 124)]
        [TestCase(124, 1, 128)]
        [TestCase(0, 3, 16)]
        [TestCase(100, 0, 104)]
        public void TrimmedHeight_KeepsOneEmptyRowPerMipLevelInWholeDxtBlocks(int usedRows, int mipCount, int expected)
        {
            Assert.That(StaticTextureAtlas_CalcRectsForAtlasNew_Patch.TrimmedHeight(usedRows, mipCount), Is.EqualTo(expected));
        }

        [Test]
        public void TrimmedHeight_EveryMipLevelIsAMultipleOfFour(
            [Values(1, 2, 3, 4, 5, 6, 7)] int mipCount,
            [Values(1, 57, 1000, 4301, 8191)] int usedRows)
        {
            int height = StaticTextureAtlas_CalcRectsForAtlasNew_Patch.TrimmedHeight(usedRows, mipCount);
            int lastMip = mipCount - 1;

            Assert.That((height >> lastMip) % 4, Is.EqualTo(0), "the smallest mip level must be whole DXT blocks");
            Assert.That(height - usedRows, Is.GreaterThanOrEqualTo(1 << lastMip), "one empty row per mip level stays above the top texture");
            Assert.That(height - usedRows - (1 << lastMip), Is.LessThan(4 << lastMip), "no more than one block of rounding");
        }

        [Test]
        public void RescaleRows_KeepsEveryTextureOnTheSamePixelRows()
        {
            var rect = new Rect(0.25f, 1000f / 8192f, 0.125f, 64f / 8192f);

            var rescaled = StaticTextureAtlas_CalcRectsForAtlasNew_Patch.RescaleRows(rect, 8192, 4352);

            Assert.That(rescaled.x, Is.EqualTo(rect.x));
            Assert.That(rescaled.width, Is.EqualTo(rect.width));
            Assert.That(rescaled.y * 4352f, Is.EqualTo(1000f).Within(0.001f));
            Assert.That(rescaled.height * 4352f, Is.EqualTo(64f).Within(0.001f));
        }
    }
}
