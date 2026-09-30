using System;
using NUnit.Framework;

namespace FasterGameLoading.Tests.Compatibility
{
    [TestFixture]
    public class ImageOptEarlyLoadCoordinatorTests
    {
        [SetUp]
        public void SetUp()
        {
            ImageOptEarlyLoadCoordinator.ResetTestConfiguration();
        }

        [TearDown]
        public void TearDown()
        {
            ImageOptEarlyLoadCoordinator.ResetTestConfiguration();
        }

        [Test]
        public void TryInstall_WhenImageOptNotActive_RemainsUninstalled()
        {
            // 在單元測試環境中 TextureOwnership.Current 不是 ImageOpt，TryInstall 應保持 installed 為 false
            ImageOptEarlyLoadCoordinator.TryInstall();
            Assert.That(ImageOptEarlyLoadCoordinator.IsInstalled, Is.False);
        }

        [Test]
        public void EnterEarlyLoadSyncScope_WhenInstalled_ManagesStartedFlagLifecycle()
        {
            var started = false;
            ImageOptEarlyLoadCoordinator.ConfigureForTests(
                () => started,
                value => started = value,
                enabled: true);

            Assert.That(ImageOptEarlyLoadCoordinator.IsInstalled, Is.True);
            Assert.That(started, Is.False);

            using (var scope = ImageOptEarlyLoadCoordinator.EnterEarlyLoadSyncScope())
            {
                Assert.That(started, Is.True);

                // 巢狀 Scope
                using (var innerScope = ImageOptEarlyLoadCoordinator.EnterEarlyLoadSyncScope())
                {
                    Assert.That(started, Is.True);
                }

                // 內部 scope dispose 後 started 仍應為 true
                Assert.That(started, Is.True);
            }

            // 外層 scope dispose 後 started 恢復為 false
            Assert.That(started, Is.False);
        }

        [Test]
        public void EnterEarlyLoadSyncScope_WhenStartedWasAlreadyTrue_DoesNotResetOnDispose()
        {
            var started = true;
            var setterCalls = 0;
            ImageOptEarlyLoadCoordinator.ConfigureForTests(
                () => started,
                value =>
                {
                    setterCalls++;
                    started = value;
                },
                enabled: true);

            using (ImageOptEarlyLoadCoordinator.EnterEarlyLoadSyncScope())
            {
                Assert.That(started, Is.True);
            }

            Assert.That(started, Is.True);
            Assert.That(setterCalls, Is.EqualTo(0));
        }

        [Test]
        public void EnterEarlyLoadSyncScope_WhenNotInstalled_ReturnsDisposableScope()
        {
            var started = false;
            ImageOptEarlyLoadCoordinator.ConfigureForTests(
                () => started,
                value => started = value,
                enabled: false);

            using (var scope = ImageOptEarlyLoadCoordinator.EnterEarlyLoadSyncScope())
            {
                // 未安裝時回傳可用的空 scope：不觸碰 started 旗標
                Assert.That(scope, Is.Not.Null);
                Assert.That(started, Is.False);
                // 重複 Dispose 亦不應拋出例外
                scope.Dispose();
            }

            Assert.That(started, Is.False);
        }

        [Test]
        public void EnterEarlyLoadSyncScope_MultipleDisposeCallsOnSameScope_OnlyDisposesOnce()
        {
            var started = false;
            ImageOptEarlyLoadCoordinator.ConfigureForTests(
                () => started,
                value => started = value,
                enabled: true);

            var scope = ImageOptEarlyLoadCoordinator.EnterEarlyLoadSyncScope();
            Assert.That(started, Is.True);

            scope.Dispose();
            Assert.That(started, Is.False);

            // 第二次呼叫 Dispose 不應產生負計數或副作用
            Assert.DoesNotThrow(() => scope.Dispose());
            Assert.That(started, Is.False);
        }

        [Test]
        public void ReportInstallFailureForTests_LogsWarningOnlyOnce()
        {
            var warningCount = 0;
            ImageOptEarlyLoadCoordinator.SetWarningSinkForTests((msg, ex) => warningCount++);

            ImageOptEarlyLoadCoordinator.ReportInstallFailureForTests(new Exception("Error 1"));
            ImageOptEarlyLoadCoordinator.ReportInstallFailureForTests(new Exception("Error 2"));

            Assert.That(ImageOptEarlyLoadCoordinator.IsInstalled, Is.False);
            Assert.That(ImageOptEarlyLoadCoordinator.WarningLogged, Is.True);
            Assert.That(ImageOptEarlyLoadCoordinator.WarningLogCount, Is.EqualTo(1));
            Assert.That(warningCount, Is.EqualTo(1));
        }
    }
}
