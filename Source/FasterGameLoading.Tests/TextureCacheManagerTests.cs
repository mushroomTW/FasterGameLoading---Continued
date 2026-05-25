using System;
using System.IO;
using NUnit.Framework;

namespace FasterGameLoading.Tests
{
    [TestFixture]
    public class TextureCacheManagerTests
    {
        private string tempDir;
        private TextureCacheManager manager;

        [SetUp]
        public void SetUp()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "FGLTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            manager = new TextureCacheManager(tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch
            {
                // Ignore cleanup errors in tests
            }
        }

        [Test]
        public void TestGetCachePath_ReturnsValidPathInTempDirectory()
        {
            string originalPath = Path.Combine(tempDir, "original.png");
            string cachePath = manager.GetCachePath(originalPath);

            Assert.IsNotNull(cachePath);
            Assert.IsTrue(cachePath.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(cachePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        }

        [Test]
        public void TestCacheKeyInvalidation_WhenFileChanges()
        {
            string originalPath = Path.Combine(tempDir, "original.png");
            
            // 1. 檔案不存在時，使用純路徑作為 Key
            string path1 = manager.GetCachePath(originalPath);

            // 2. 建立檔案並寫入初始內容，設定明確的最後修改時間 (-5 分鐘)
            File.WriteAllBytes(originalPath, new byte[] { 1, 2, 3 });
            File.SetLastWriteTimeUtc(originalPath, DateTime.UtcNow.AddMinutes(-5));
            string path2 = manager.GetCachePath(originalPath);

            // 3. 修改檔案大小，並設定明確的最後修改時間 (-4 分鐘)
            File.WriteAllBytes(originalPath, new byte[] { 1, 2, 3, 4, 5 });
            File.SetLastWriteTimeUtc(originalPath, DateTime.UtcNow.AddMinutes(-4));
            string path3 = manager.GetCachePath(originalPath);

            // 4. 不修改大小，但修改最後修改時間 (-3 分鐘)
            File.SetLastWriteTimeUtc(originalPath, DateTime.UtcNow.AddMinutes(-3));
            string path4 = manager.GetCachePath(originalPath);

            // 驗證當檔案狀態變更時，產生的快取路徑（MD5）皆不相同，從而實現自動失效
            Assert.AreNotEqual(path1, path2);
            Assert.AreNotEqual(path2, path3);
            Assert.AreNotEqual(path3, path4);
        }

        [Test]
        public void TestTryGetCachedTexturePath_FreshCache_ReturnsTrue()
        {
            string originalPath = Path.Combine(tempDir, "original.png");
            string cachePath = Path.Combine(tempDir, "cached_dxt.png");

            File.WriteAllBytes(originalPath, new byte[] { 1 });
            File.WriteAllBytes(cachePath, new byte[] { 2 });

            // 確保快取檔案的修改時間大於等於原始檔案
            File.SetLastWriteTimeUtc(originalPath, DateTime.UtcNow.AddMinutes(-1));
            File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);

            manager.SetCacheEntry(originalPath, cachePath);

            bool success = manager.TryGetCachedTexturePath(originalPath, out string resolvedPath);

            Assert.IsTrue(success);
            Assert.AreEqual(cachePath, resolvedPath);
            Assert.AreEqual(1, manager.CacheCount);
        }

        [Test]
        public void TestTryGetCachedTexturePath_StaleCache_ReturnsFalseAndRemovesEntry()
        {
            string originalPath = Path.Combine(tempDir, "original.png");
            string cachePath = Path.Combine(tempDir, "cached_dxt.png");

            File.WriteAllBytes(originalPath, new byte[] { 1 });
            File.WriteAllBytes(cachePath, new byte[] { 2 });

            // 故意使原始檔案比快取檔案更新，模擬快取過期
            File.SetLastWriteTimeUtc(originalPath, DateTime.UtcNow);
            File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow.AddMinutes(-1));

            manager.SetCacheEntry(originalPath, cachePath);

            bool success = manager.TryGetCachedTexturePath(originalPath, out string resolvedPath);

            Assert.IsFalse(success);
            Assert.IsNull(resolvedPath);
            Assert.AreEqual(0, manager.CacheCount); // 快取管理器應自動移除此過期項目
        }

        [Test]
        public void TestClearCache_DeletesDirectoryAndClearsDictionary()
        {
            string originalPath = Path.Combine(tempDir, "original.png");
            string cachePath = Path.Combine(tempDir, "cached_dxt.png");
            manager.SetCacheEntry(originalPath, cachePath);

            Assert.AreEqual(1, manager.CacheCount);

            manager.ClearCache();

            Assert.AreEqual(0, manager.CacheCount);
            Assert.IsFalse(Directory.Exists(tempDir));
        }
    }
}
