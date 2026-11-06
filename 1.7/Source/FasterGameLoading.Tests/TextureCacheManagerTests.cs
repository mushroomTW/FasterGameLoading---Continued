using System;
using System.IO;
using NUnit.Framework;

namespace FasterGameLoading.Tests;
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
        catch (Exception ex)
        {
            Assert.Fail($"清理測試暫存目錄失敗：{tempDir}\n{ex}");
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
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // 1. 檔案不存在時，使用純路徑作為 Key
        string path1 = manager.GetCachePath(originalPath);

        // 2. 建立檔案並寫入初始內容，設定明確的最後修改時間 (-5 分鐘)
        File.WriteAllBytes(originalPath, [1, 2, 3]);
        File.SetLastWriteTimeUtc(originalPath, baseTime);
        string path2 = manager.GetCachePath(originalPath);

        // 3. 修改檔案大小，並設定明確的最後修改時間 (-4 分鐘)
        File.WriteAllBytes(originalPath, [1, 2, 3, 4, 5]);
        File.SetLastWriteTimeUtc(originalPath, baseTime.AddMinutes(1));
        string path3 = manager.GetCachePath(originalPath);

        // 4. 不修改大小，但修改最後修改時間 (-3 分鐘)
        File.SetLastWriteTimeUtc(originalPath, baseTime.AddMinutes(2));
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
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        File.WriteAllBytes(originalPath, [1]);
        File.SetLastWriteTimeUtc(originalPath, baseTime);

        // 快取檔名由原始檔目前的路徑、大小與修改時間算出。
        string cachePath = WriteCacheFileFor(originalPath);
        manager.SetCacheEntry(originalPath, cachePath);

        bool success = manager.TryGetCachedTexturePath(originalPath, out string resolvedPath);

        Assert.IsTrue(success);
        Assert.AreEqual(cachePath, resolvedPath);
        Assert.AreEqual(1, manager.CacheCount);
    }

    // 依原始檔目前的身分寫出快取檔，回傳快取路徑。
    private string WriteCacheFileFor(string originalPath)
    {
        string cachePath = manager.GetCachePath(originalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
        File.WriteAllBytes(cachePath, [2]);
        return cachePath;
    }

    // 不論原始檔的修改時間往前或往後、大小是否改變，都要作廢；往前（更舊）是只比時間先後會漏掉的情況。
    [TestCase(1, 1)]
    [TestCase(-1, 1)]
    [TestCase(-1, 2)]
    [TestCase(0, 2)]
    public void TestTryGetCachedTexturePath_StaleCache_ReturnsFalseAndRemovesEntry(int minutesShift, int newLength)
    {
        string originalPath = Path.Combine(tempDir, "original.png");
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        File.WriteAllBytes(originalPath, [1]);
        File.SetLastWriteTimeUtc(originalPath, baseTime);
        string cachePath = WriteCacheFileFor(originalPath);
        manager.SetCacheEntry(originalPath, cachePath);

        File.WriteAllBytes(originalPath, new byte[newLength]);
        File.SetLastWriteTimeUtc(originalPath, baseTime.AddMinutes(minutesShift));

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
    public void TestCleanupObsoleteCacheFiles_RemovesUnreferencedFiles()
    {
        // 1. 建立測試環境：一個存在的原始檔案，一個不存在的原始檔案
        string originalExist = Path.Combine(tempDir, "exist.png");
        string originalDeleted = Path.Combine(tempDir, "deleted.png");

        File.WriteAllBytes(originalExist, [1]);
        // deleted.png 刻意不建立，模擬已被刪除或更名的原始檔案

        string cacheDir = manager.CacheDirectory;
        if (!Directory.Exists(cacheDir))
        {
            Directory.CreateDirectory(cacheDir);
        }

        string cacheExistPath = Path.Combine(cacheDir, "cache_exist.png");
        string cacheDeletedPath = Path.Combine(cacheDir, "cache_deleted.png");
        string cacheUnreferencedPath = Path.Combine(cacheDir, "cache_unref.png");

        File.WriteAllBytes(cacheExistPath, [2]);
        File.WriteAllBytes(cacheDeletedPath, [3]);
        File.WriteAllBytes(cacheUnreferencedPath, [4]);

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

    [Test]
    public void RemoveEntriesOutside_KeepsEntriesUnderRootsAndHonoursDirectoryBoundary()
    {
        string keptOriginal = Path.Combine(tempDir, "Mods", "A", "Textures", "kept.png");
        string siblingOriginal = Path.Combine(tempDir, "Mods", "AB", "Textures", "sibling.png");
        manager.SetCacheEntry(keptOriginal, Path.Combine(tempDir, "kept_cache.png"));
        manager.SetCacheEntry(siblingOriginal, Path.Combine(tempDir, "sibling_cache.png"));

        // 結尾斜線與大小寫差異不影響判定；"Mods/A" 不得誤判為 "Mods/AB" 的上層。
        int removed = manager.RemoveEntriesOutside([Path.Combine(tempDir, "mods", "a") + Path.DirectorySeparatorChar]);

        Assert.AreEqual(1, removed);
        Assert.IsTrue(manager.ResizedTextureCache.ContainsKey(keptOriginal));
        Assert.IsFalse(manager.ResizedTextureCache.ContainsKey(siblingOriginal));
    }

    [Test]
    public void RemoveEntriesOutside_WithoutRootsKeepsEverything()
    {
        manager.SetCacheEntry(Path.Combine(tempDir, "Mods", "A", "a.png"), Path.Combine(tempDir, "a_cache.png"));

        // 執行中的 mod 清單尚未就緒時寧可全部保留，不得等同於清空快取。
        Assert.AreEqual(0, manager.RemoveEntriesOutside([]));
        Assert.AreEqual(1, manager.CacheCount);
    }

    [Test]
    public void SafeFileExists_WithInvalidCharacters_ReturnsFalse()
    {
        var method = typeof(TextureCacheManager).GetMethod("SafeFileExists", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        bool result = (bool)method.Invoke(null, new object[] { "invalid\0path" });
        Assert.That(result, Is.False);
    }

    [Test]
    public void AddResolvedPath_WithInvalidCharacters_FallsBackToRawPath()
    {
        var method = typeof(TextureCacheManager).GetMethod("AddResolvedPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var set = new System.Collections.Generic.HashSet<string>();
        method.Invoke(null, new object[] { set, "invalid\0path" });
        Assert.That(set, Does.Contain("invalid\0path"));
    }

    [Test]
    public void RollbackPromotion_WhenMovedStagingCache_DeletesCacheDirectory()
    {
        var method = typeof(TextureCacheManager).GetMethod("RollbackPromotion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.That(Directory.Exists(manager.CacheDirectory), Is.True);
        method.Invoke(manager, new object[] { true, false, null });
        Assert.That(Directory.Exists(manager.CacheDirectory), Is.False);
    }
}
