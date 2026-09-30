using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace FasterGameLoading.Tests.TextureDownscaler
{
    /// <summary>
    /// TextureCacheManager 的目錄生命週期測試：暫存目錄建立、升級為正式快取、
    /// 失敗回滾、狀態還原，以及清理流程中各條容錯分支。
    /// 這些路徑在遊戲中只有在使用者按下「降質全部紋理」時才會跑到。
    /// </summary>
    [TestFixture]
    public class TextureCacheManagerLifecycleTests
    {
        private string rootDir;
        private string cacheDir;
        private TextureCacheManager manager;

        [SetUp]
        public void SetUp()
        {
            rootDir = Path.Combine(Path.GetTempPath(), "FGLCacheLifecycle_" + Guid.NewGuid().ToString("N"));
            cacheDir = Path.Combine(rootDir, FGLConsts.TextureCacheDir);
            Directory.CreateDirectory(rootDir);
            manager = new TextureCacheManager(cacheDir);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(rootDir))
                {
                    Directory.Delete(rootDir, recursive: true);
                }
            }
            catch (IOException ex)
            {
                Assert.Fail($"清理測試暫存目錄失敗：{rootDir}\n{ex}");
            }
        }

        private string StagingDir => Path.Combine(rootDir, FGLConsts.TextureCacheStagingDir);

        /// <summary>在 rebuild 的暫存目錄寫一個快取檔並登記，回傳暫存路徑。</summary>
        private static string StageFile(TextureCacheManager.CacheRebuild rebuild, string originalPath, byte content)
        {
            string stagedPath = rebuild.GetCachePath(originalPath);
            File.WriteAllBytes(stagedPath, new[] { content });
            rebuild.Add(originalPath, stagedPath);
            return stagedPath;
        }

        // 註：無自訂根目錄的預設建構式無法在此測試 —— CacheDirectory 會讀
        // GenFilePaths.SaveDataFolderPath，該路徑在 headless 環境下取不到。

        // ── 快取新鮮度 ──

        [Test]
        public void TryGetCachedTexturePath_MissingOriginalFileKeepsCacheEntry()
        {
            string originalPath = Path.Combine(rootDir, "gone.png");
            string cachePath = Path.Combine(rootDir, "gone_cache.png");
            File.WriteAllBytes(cachePath, new byte[] { 1 });
            manager.SetCacheEntry(originalPath, cachePath);

            // 原始檔已不在（例如 Mod 被移除）：不應在此判定為過期，
            // 過期與否交由 CleanupObsoleteCacheFiles 統一處理。
            Assert.That(manager.TryGetCachedTexturePath(originalPath, out string resolved), Is.True);
            Assert.That(resolved, Is.EqualTo(cachePath));
        }

        [Test]
        public void TryGetCachedTexturePath_NewerOriginalWithSameIdentityRefreshesCacheTimestamp()
        {
            Directory.CreateDirectory(cacheDir);
            string originalPath = Path.Combine(rootDir, "same-length.png");
            var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            File.WriteAllBytes(originalPath, new byte[] { 1, 2, 3 });
            File.SetLastWriteTimeUtc(originalPath, baseTime);

            // 快取路徑必須是由目前檔案身分推導出的雜湊路徑，才會命中「內容未變」的快速通道。
            string cachePath = manager.GetCachePath(originalPath);
            File.WriteAllBytes(cachePath, new byte[] { 9 });
            File.SetLastWriteTimeUtc(cachePath, baseTime.AddMinutes(-5));
            manager.SetCacheEntry(originalPath, cachePath);

            Assert.That(manager.TryGetCachedTexturePath(originalPath, out string resolved), Is.True);
            Assert.That(resolved, Is.EqualTo(cachePath));
            Assert.That(File.GetLastWriteTimeUtc(cachePath), Is.EqualTo(baseTime),
                "檔案身分未變時應把快取檔的時間戳對齊原始檔，而不是重新降質一次。");
        }

        // ── 對照表操作 ──

        [Test]
        public void RemoveCachedTexturePath_DropsSingleEntry()
        {
            manager.SetCacheEntry(Path.Combine(rootDir, "a.png"), Path.Combine(rootDir, "a_cache.png"));
            manager.SetCacheEntry(Path.Combine(rootDir, "b.png"), Path.Combine(rootDir, "b_cache.png"));

            manager.RemoveCachedTexturePath(Path.Combine(rootDir, "a.png"));

            Assert.That(manager.CacheCount, Is.EqualTo(1));
            Assert.That(manager.ResizedTextureCache.ContainsKey(Path.Combine(rootDir, "b.png")), Is.True);
        }

        [Test]
        public void GetResizedTextureCacheCopy_ReturnsDetachedSnapshot()
        {
            string originalPath = Path.Combine(rootDir, "snap.png");
            manager.SetCacheEntry(originalPath, Path.Combine(rootDir, "snap_cache.png"));

            var snapshot = manager.GetResizedTextureCacheCopy();
            snapshot["injected"] = "value";

            Assert.That(manager.CacheCount, Is.EqualTo(1));
            Assert.That(manager.ResizedTextureCache.ContainsKey("injected"), Is.False);
        }

        // ── 重建交易 ──

        [Test]
        public void Rebuild_PromotesStagedEntriesAndRepointsCacheMap()
        {
            Directory.CreateDirectory(cacheDir);
            File.WriteAllBytes(Path.Combine(cacheDir, "old.png"), new byte[] { 1 });
            manager.SetCacheEntry(Path.Combine(rootDir, "old-source.png"), Path.Combine(cacheDir, "old.png"));
            string originalPath = Path.Combine(rootDir, "promote.png");
            string stagedName = null;

            bool promoted = manager.Rebuild(rebuild => stagedName = Path.GetFileName(StageFile(rebuild, originalPath, 2)));

            Assert.That(promoted, Is.True);
            Assert.That(Directory.Exists(StagingDir), Is.False);
            Assert.That(Directory.Exists(cacheDir + "_Backup"), Is.False, "升級成功後備份目錄必須清掉，否則快取空間會翻倍。");
            Assert.That(File.Exists(Path.Combine(cacheDir, stagedName)), Is.True);
            Assert.That(File.Exists(Path.Combine(cacheDir, "old.png")), Is.False);
            Assert.That(manager.CacheCount, Is.EqualTo(1), "對照表必須整份換成本次重建的項目。");
            Assert.That(manager.ResizedTextureCache[originalPath],
                Is.EqualTo(Path.Combine(cacheDir, stagedName)),
                "對照表必須改指向正式目錄，否則升級後每一筆快取都會查無檔案。");
        }

        [Test]
        public void Rebuild_DiscardsLeftoverStagingFilesFromAnInterruptedRun()
        {
            Directory.CreateDirectory(StagingDir);
            File.WriteAllBytes(Path.Combine(StagingDir, "leftover.png"), new byte[] { 1 });

            manager.Rebuild(rebuild =>
            {
                Assert.That(Directory.GetFiles(rebuild.StagingDirectory), Is.Empty,
                    "上一輪殘留的暫存檔必須清空，否則會被誤認為本輪產物而升級為正式快取。");
                StageFile(rebuild, Path.Combine(rootDir, "fresh.png"), 2);
            });

            Assert.That(File.Exists(Path.Combine(cacheDir, "leftover.png")), Is.False);
        }

        [Test]
        public void Rebuild_KeepsLiveCacheQueryableWhilePopulating()
        {
            Directory.CreateDirectory(cacheDir);
            string originalPath = Path.Combine(rootDir, "live.png");
            string livePath = Path.Combine(cacheDir, "live_cache.png");
            File.WriteAllBytes(livePath, new byte[] { 1 });
            manager.SetCacheEntry(originalPath, livePath);

            manager.Rebuild(rebuild =>
            {
                Assert.That(manager.TryGetCachedTexturePath(originalPath, out var resolved), Is.True,
                    "重建期間正式對照表必須維持原狀，貼圖載入仍要命中舊快取。");
                Assert.That(resolved, Is.EqualTo(livePath));
                Assert.That(manager.GetCachePath(originalPath), Does.StartWith(cacheDir),
                    "正式快取的路徑計算不可被重建的暫存目錄影響。");
            });

            Assert.That(manager.CacheCount, Is.EqualTo(1));
            Assert.That(File.Exists(livePath), Is.True, "沒有登記任何項目的重建不得動到正式快取。");
        }

        [Test]
        public void Rebuild_WithNoStagedEntries_KeepsExistingCacheAndDeletesStaging()
        {
            Directory.CreateDirectory(cacheDir);
            string retainedFile = Path.Combine(cacheDir, "retained.png");
            File.WriteAllBytes(retainedFile, new byte[] { 1 });
            manager.SetCacheEntry(Path.Combine(rootDir, "retained-source.png"), retainedFile);

            bool promoted = manager.Rebuild(_ => { });

            Assert.That(promoted, Is.False);
            Assert.That(File.Exists(retainedFile), Is.True);
            Assert.That(manager.CacheCount, Is.EqualTo(1));
            Assert.That(Directory.Exists(StagingDir), Is.False);
        }

        [Test]
        public void Rebuild_WhenPopulateThrows_PropagatesAndKeepsExistingCache()
        {
            Directory.CreateDirectory(cacheDir);
            string retainedFile = Path.Combine(cacheDir, "retained.png");
            File.WriteAllBytes(retainedFile, new byte[] { 1 });
            manager.SetCacheEntry(Path.Combine(rootDir, "retained-source.png"), retainedFile);

            Assert.Throws<InvalidOperationException>(() => manager.Rebuild(rebuild =>
            {
                StageFile(rebuild, Path.Combine(rootDir, "half.png"), 2);
                throw new InvalidOperationException("Simulated resize failure");
            }));

            Assert.That(File.Exists(retainedFile), Is.True);
            Assert.That(manager.CacheCount, Is.EqualTo(1));
            Assert.That(manager.ResizedTextureCache.ContainsKey(Path.Combine(rootDir, "half.png")), Is.False);
            Assert.That(Directory.Exists(StagingDir), Is.False);
        }

        [Test]
        public void Rebuild_BackgroundCleanupWaitsUntilRebuildFinishes()
        {
            Directory.CreateDirectory(cacheDir);
            string originalPath = Path.Combine(rootDir, "kept.png");
            File.WriteAllBytes(originalPath, new byte[] { 1 });
            System.Threading.Tasks.Task cleanup = null;

            manager.Rebuild(rebuild =>
            {
                StageFile(rebuild, originalPath, 2);
                cleanup = System.Threading.Tasks.Task.Run(manager.CleanupObsoleteCacheFiles);
                // 清理若在升級搬移目錄的途中執行，會把剛搬進正式目錄、還沒登記的新檔當成未引用檔刪掉。
                Assert.That(cleanup.Wait(200), Is.False, "背景清理必須等重建結束才開始。");
            });
            cleanup.Wait();

            Assert.That(manager.TryGetCachedTexturePath(originalPath, out var resolved), Is.True);
            Assert.That(File.Exists(resolved), Is.True, "重建後的新快取檔不可被清理刪除。");
        }

        [Test]
        public void Rebuild_LockedCacheDirectoryFailsAndKeepsExistingCache()
        {
            Directory.CreateDirectory(cacheDir);
            string lockedFile = Path.Combine(cacheDir, "locked.png");
            File.WriteAllBytes(lockedFile, new byte[] { 1 });
            string previousSource = Path.Combine(rootDir, "previous.png");
            manager.SetCacheEntry(previousSource, lockedFile);

            bool promoted;
            using (var handle = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // 開啟中的檔案會讓 Directory.Move 失敗，觸發升級流程的回滾分支。
                promoted = manager.Rebuild(rebuild => StageFile(rebuild, Path.Combine(rootDir, "new.png"), 2));
            }

            Assert.That(promoted, Is.False);
            Assert.That(File.Exists(lockedFile), Is.True, "升級失敗後原本的快取目錄必須原封不動。");
            Assert.That(manager.ResizedTextureCache[previousSource], Is.EqualTo(lockedFile), "升級失敗後對照表必須維持原狀。");
        }

        [Test]
        public void Rebuild_LockedStagingRollsBackPreviousCacheFromBackup()
        {
            string backupDir = cacheDir + "_Backup";

            Directory.CreateDirectory(cacheDir);
            string survivor = Path.Combine(cacheDir, "survivor.png");
            File.WriteAllBytes(survivor, new byte[] { 1 });

            // 上一輪中斷留下的備份目錄：升級流程必須先把它清掉才動手。
            Directory.CreateDirectory(backupDir);
            File.WriteAllBytes(Path.Combine(backupDir, "stale_backup.png"), new byte[] { 9 });

            FileStream handle = null;
            bool promoted;
            try
            {
                promoted = manager.Rebuild(rebuild =>
                {
                    var stagedPath = StageFile(rebuild, Path.Combine(rootDir, "new.png"), 2);
                    // 正式目錄已搬去備份、暫存目錄卻搬不動：必須把備份搬回原位，
                    // 否則使用者會在一次失敗的降質後完全失去既有快取。
                    handle = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.None);
                });
            }
            finally
            {
                handle?.Dispose();
            }

            Assert.That(promoted, Is.False);
            Assert.That(Directory.Exists(backupDir), Is.False);
            Assert.That(File.Exists(survivor), Is.True, "回滾後原本的快取檔必須回到正式目錄。");
        }

        // ── 清理流程的容錯分支 ──

        [Test]
        public void CleanupObsoleteCacheFiles_MissingCacheDirectoryIsNoOp()
        {
            manager.SetCacheEntry(Path.Combine(rootDir, "a.png"), Path.Combine(cacheDir, "a_cache.png"));

            Assert.That(Directory.Exists(cacheDir), Is.False);
            manager.CleanupObsoleteCacheFiles();

            Assert.That(manager.CacheCount, Is.EqualTo(1), "快取目錄還沒建立時不該動到對照表。");
        }

        [Test]
        public void CleanupObsoleteCacheFiles_DropsEntryWhoseCacheFileVanished()
        {
            Directory.CreateDirectory(cacheDir);
            string originalPath = Path.Combine(rootDir, "present.png");
            File.WriteAllBytes(originalPath, new byte[] { 1 });
            manager.SetCacheEntry(originalPath, Path.Combine(cacheDir, "never_written.png"));

            manager.CleanupObsoleteCacheFiles();

            Assert.That(manager.CacheCount, Is.Zero);
        }

        [Test]
        public void CleanupObsoleteCacheFiles_AllEntriesValidLeavesEverythingInPlace()
        {
            Directory.CreateDirectory(cacheDir);
            string originalPath = Path.Combine(rootDir, "valid.png");
            string cachePath = Path.Combine(cacheDir, "valid_cache.png");
            File.WriteAllBytes(originalPath, new byte[] { 1 });
            File.WriteAllBytes(cachePath, new byte[] { 2 });
            manager.SetCacheEntry(originalPath, cachePath);

            manager.CleanupObsoleteCacheFiles();

            Assert.That(manager.CacheCount, Is.EqualTo(1));
            Assert.That(File.Exists(cachePath), Is.True);
        }

        [Test]
        public void CleanupObsoleteCacheFiles_UndeletableUnreferencedFileIsSkipped()
        {
            Directory.CreateDirectory(cacheDir);
            string strayPath = Path.Combine(cacheDir, "stray.png");
            File.WriteAllBytes(strayPath, new byte[] { 3 });

            using (var handle = new FileStream(strayPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // 檔案被佔用時刪除會失敗；清理流程必須容錯而非整批中止。
                Assert.DoesNotThrow(manager.CleanupObsoleteCacheFiles);
            }

            Assert.That(File.Exists(strayPath), Is.True);
        }

        [Test]
        public void CleanupObsoleteCacheFiles_UndeletableObsoleteFileStillDropsMapEntry()
        {
            Directory.CreateDirectory(cacheDir);
            string originalPath = Path.Combine(rootDir, "deleted-source.png"); // 刻意不建立
            string cachePath = Path.Combine(cacheDir, "orphan_cache.png");
            File.WriteAllBytes(cachePath, new byte[] { 4 });
            manager.SetCacheEntry(originalPath, cachePath);

            using (var handle = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                manager.CleanupObsoleteCacheFiles();
            }

            Assert.That(manager.CacheCount, Is.Zero,
                "即使快取檔刪不掉，對照表中指向已消失原始檔的項目仍必須移除。");
            Assert.That(File.Exists(cachePath), Is.True);
        }

        [Test]
        public void CleanupObsoleteCacheFiles_EntriesAddedAfterSnapshotAreNotDeleted()
        {
            Directory.CreateDirectory(cacheDir);
            string originalPath = Path.Combine(rootDir, "late.png");
            string cachePath = Path.Combine(cacheDir, "late_cache.png");
            File.WriteAllBytes(originalPath, new byte[] { 1 });
            File.WriteAllBytes(cachePath, new byte[] { 2 });

            var beforeCleanup = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [originalPath] = cachePath,
            };
            manager.ReplaceCacheMap(beforeCleanup);

            manager.CleanupObsoleteCacheFiles();

            Assert.That(File.Exists(cachePath), Is.True);
            Assert.That(manager.CacheCount, Is.EqualTo(1));
        }

        // ── 對照表的值來自設定檔，不可信任 ──

        [Test]
        public void CleanupObsoleteCacheFiles_NeverDeletesFilesOutsideFglFolder()
        {
            Directory.CreateDirectory(cacheDir);
            string victim = Path.Combine(Path.GetTempPath(), "FGLVictim_" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(victim, "must survive");
            try
            {
                // 原始檔都不存在，舊流程會直接刪除對照表指向的「快取檔」。
                manager.SetCacheEntry(Path.Combine(rootDir, "gone1.png"), victim);
                manager.SetCacheEntry(Path.Combine(rootDir, "gone2.png"),
                    Path.Combine(cacheDir, "..", "..", Path.GetFileName(victim)));

                manager.CleanupObsoleteCacheFiles();

                Assert.That(File.Exists(victim), Is.True, "清理只能刪除 FGL 自己資料夾內的檔案（含 .. 穿越後的路徑）。");
                Assert.That(manager.CacheCount, Is.Zero, "指向資料夾外的項目仍要從對照表移除。");
            }
            finally
            {
                File.Delete(victim);
            }
        }

        [Test]
        public void TryGetCachedTexturePath_RejectsCacheFileOutsideFglFolder()
        {
            string originalPath = Path.Combine(rootDir, "orig.png");
            string outsideCache = Path.Combine(Path.GetTempPath(), "FGLOutside_" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(originalPath, new byte[] { 1 });
            File.WriteAllBytes(outsideCache, new byte[] { 2 });
            var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(originalPath, baseTime);
            File.SetLastWriteTimeUtc(outsideCache, baseTime.AddMinutes(1));
            try
            {
                manager.SetCacheEntry(originalPath, outsideCache);

                Assert.That(manager.TryGetCachedTexturePath(originalPath, out var cachePath), Is.False);
                Assert.That(cachePath, Is.Null);
                Assert.That(manager.CacheCount, Is.Zero);
            }
            finally
            {
                File.Delete(outsideCache);
            }
        }
    }
}
