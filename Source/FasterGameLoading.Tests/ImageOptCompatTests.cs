using System;
using System.IO;
using NUnit.Framework;

namespace FasterGameLoading.Tests
{
    [TestFixture]
    public class ImageOptCompatTests
    {
        private string tempDir;

        [SetUp]
        public void SetUp()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "FGL_ImageOpt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Test]
        public void TestCleanupInvalidDdsZstdCaches_DeletesOnlyCorruptCachesWithSourceImage()
        {
            var texturesDir = Path.Combine(tempDir, "Textures");
            Directory.CreateDirectory(texturesDir);

            var corrupt = Path.Combine(texturesDir, "corrupt.dds.zstd");
            var valid = Path.Combine(texturesDir, "valid.dds.zstd");
            var orphan = Path.Combine(texturesDir, "orphan.dds.zstd");

            File.WriteAllBytes(Path.Combine(texturesDir, "corrupt.png"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(texturesDir, "valid.png"), new byte[] { 1 });
            File.WriteAllBytes(corrupt, new byte[] { 0, 0, 0, 0 });
            File.WriteAllBytes(valid, new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0 });
            File.WriteAllBytes(orphan, new byte[] { 0, 0, 0, 0 });

            var deleted = ImageOptCompat.CleanupInvalidDdsZstdCaches(new[] { tempDir });

            Assert.AreEqual(1, deleted);
            Assert.IsFalse(File.Exists(corrupt));
            Assert.IsTrue(File.Exists(valid));
            Assert.IsTrue(File.Exists(orphan));
        }

        [Test]
        public void TestCleanupInvalidDdsZstdCaches_AlsoDeletesCorruptDdsCachesWithSourceImage()
        {
            var texturesDir = Path.Combine(tempDir, "Textures");
            Directory.CreateDirectory(texturesDir);

            var corrupt = Path.Combine(texturesDir, "corrupt.dds");
            var valid = Path.Combine(texturesDir, "valid.dds");
            var orphan = Path.Combine(texturesDir, "orphan.dds");

            File.WriteAllBytes(Path.Combine(texturesDir, "corrupt.png"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(texturesDir, "valid.png"), new byte[] { 1 });
            File.WriteAllBytes(corrupt, new byte[] { 0, 0, 0, 0 });
            File.WriteAllBytes(valid, new byte[] { (byte)'D', (byte)'D', (byte)'S', (byte)' ', 0 });
            File.WriteAllBytes(orphan, new byte[] { 0, 0, 0, 0 });

            var deleted = ImageOptCompat.CleanupInvalidDdsZstdCaches(new[] { tempDir });

            Assert.AreEqual(1, deleted);
            Assert.IsFalse(File.Exists(corrupt));
            Assert.IsTrue(File.Exists(valid));
            Assert.IsTrue(File.Exists(orphan));
        }

        [Test]
        public void TestCleanupInvalidDdsZstdCaches_SkipsNonTextureFolders()
        {
            var defsDir = Path.Combine(tempDir, "Defs");
            Directory.CreateDirectory(defsDir);

            var corrupt = Path.Combine(defsDir, "corrupt.dds.zstd");
            File.WriteAllBytes(Path.Combine(defsDir, "corrupt.png"), new byte[] { 1 });
            File.WriteAllBytes(corrupt, new byte[] { 0, 0, 0, 0 });

            var deleted = ImageOptCompat.CleanupInvalidDdsZstdCaches(new[] { tempDir });

            Assert.AreEqual(0, deleted);
            Assert.IsTrue(File.Exists(corrupt));
        }
    }
}
