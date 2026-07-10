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

            // 驗證來源身分包含長度與 UTC 修改時間；相同大小的替換檔案也必須失效。
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

        [Test]
        public void ReplaceTextureCacheDirectory_MissingStagingPreservesExistingCache()
        {
            Directory.CreateDirectory(manager.CacheDirectory);
            string retainedFile = Path.Combine(manager.CacheDirectory, "retained.png");
            File.WriteAllBytes(retainedFile, new byte[] { 1 });

            bool replaced = manager.ReplaceTextureCacheDirectory(Path.Combine(tempDir, "missing-staging"));

            Assert.IsFalse(replaced);
            Assert.IsTrue(File.Exists(retainedFile));
        }

        [Test]
        public void TestCleanupObsoleteCacheFiles()
        {
            // 1. 建立測試環境：一個存在的原始檔案，一個不存在的原始檔案
            string originalExist = Path.Combine(tempDir, "exist.png");
            string originalDeleted = Path.Combine(tempDir, "deleted.png");

            File.WriteAllBytes(originalExist, new byte[] { 1 });
            // deleted.png 刻意不建立，模擬已被刪除或更名的原始檔案

            string cacheDir = manager.CacheDirectory;
            if (!Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }

            string cacheExistPath = Path.Combine(cacheDir, "cache_exist.png");
            string cacheDeletedPath = Path.Combine(cacheDir, "cache_deleted.png");
            string cacheUnreferencedPath = Path.Combine(cacheDir, "cache_unref.png");

            File.WriteAllBytes(cacheExistPath, new byte[] { 2 });
            File.WriteAllBytes(cacheDeletedPath, new byte[] { 3 });
            File.WriteAllBytes(cacheUnreferencedPath, new byte[] { 4 });

            // 2. 註冊對照字典
            manager.SetCacheEntry(originalExist, cacheExistPath);
            manager.SetCacheEntry(originalDeleted, cacheDeletedPath);
            // cacheUnreferencedPath 刻意不註冊，模擬孤立的無效快取檔案

            Assert.AreEqual(2, manager.CacheCount);
            Assert.IsTrue(File.Exists(cacheExistPath));
            Assert.IsTrue(File.Exists(cacheDeletedPath));
            Assert.IsTrue(File.Exists(cacheUnreferencedPath));

            // 3. 執行清理
            manager.CleanupObsoleteCacheFiles();

            // 4. 驗證字典與實體檔案清理結果
            Assert.AreEqual(1, manager.CacheCount);
            Assert.IsTrue(manager.ResizedTextureCache.ContainsKey(originalExist));
            Assert.IsFalse(manager.ResizedTextureCache.ContainsKey(originalDeleted));

            Assert.IsTrue(File.Exists(cacheExistPath));
            Assert.IsFalse(File.Exists(cacheDeletedPath));
            Assert.IsFalse(File.Exists(cacheUnreferencedPath));
        }
    }
}
