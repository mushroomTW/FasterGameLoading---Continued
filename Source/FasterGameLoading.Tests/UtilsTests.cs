using NUnit.Framework;

namespace FasterGameLoading.Tests
{
    [TestFixture]
    public class UtilsTests
    {
        [Test]
        public void TestNormalizePath_WithNull_ReturnsNull()
        {
            string input = null;
            string result = input.NormalizePath();
            Assert.IsNull(result);
        }

        [Test]
        public void TestNormalizePath_WithWindowsBackslashes_NormalizesToForwardSlashes()
        {
            string input = @"C:\Program Files (x86)\RimWorld\Mods\Textures\UI\Icon.png";
            string expected = "C:/Program Files (x86)/RimWorld/Mods/Textures/UI/Icon.png";
            string result = input.NormalizePath();
            Assert.AreEqual(expected, result);
        }

        [Test]
        public void TestNormalizePath_WithMixedSlashes_NormalizesToForwardSlashes()
        {
            string input = "C:/Program Files (x86)\\RimWorld/Mods\\Textures/UI/Icon.png";
            string expected = "C:/Program Files (x86)/RimWorld/Mods/Textures/UI/Icon.png";
            string result = input.NormalizePath();
            Assert.AreEqual(expected, result);
        }

        [Test]
        public void TestFloorToPowerOfTwo_WithPowerOfTwoInput_ReturnsSameValue()
        {
            Assert.AreEqual(1024, 1024.FloorToPowerOfTwo());
            Assert.AreEqual(256, 256.FloorToPowerOfTwo());
            Assert.AreEqual(1, 1.FloorToPowerOfTwo());
        }

        [Test]
        public void TestFloorToPowerOfTwo_WithNonPowerOfTwoInput_ReturnsLargestPowerOfTwoLessOrEqual()
        {
            Assert.AreEqual(512, 1000.FloorToPowerOfTwo());
            Assert.AreEqual(256, 300.FloorToPowerOfTwo());
            Assert.AreEqual(4, 5.FloorToPowerOfTwo());
            Assert.AreEqual(4, 7.FloorToPowerOfTwo());
        }

        [Test]
        public void TestFloorToPowerOfTwo_WithZeroOrNegativeInput_ReturnsZero()
        {
            Assert.AreEqual(0, 0.FloorToPowerOfTwo());
            Assert.AreEqual(0, (-10).FloorToPowerOfTwo());
        }
    }
}
