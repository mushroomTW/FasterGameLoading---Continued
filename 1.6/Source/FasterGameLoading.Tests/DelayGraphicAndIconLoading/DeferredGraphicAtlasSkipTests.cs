using System;
using NUnit.Framework;

namespace FasterGameLoading.Tests.DelayGraphicAndIconLoading
{
    /// <summary>
    /// 靜態圖集插入閘門的單元測試。真正的 Harmony 前綴只在遊戲啟動時套用，
    /// 這裡直接呼叫 <see cref="DeferredGraphicAtlasSkip.Prefix"/>，驗證抑制範圍的開關與還原。
    /// </summary>
    [TestFixture]
    public class DeferredGraphicAtlasSkipTests
    {
        [Test]
        public void Prepare_ReflectsDelayGraphicLoadingSetting()
        {
            var previous = FasterGameLoadingSettings.DelayGraphicLoading;
            try
            {
                // 先回到「尚未定案」，Prepare 才會直接讀設定值。
                DelayedActions.ReleaseStartupSettingsForTests();

                FasterGameLoadingSettings.DelayGraphicLoading = true;
                Assert.That(DeferredGraphicAtlasSkip.Prepare(), Is.True);

                FasterGameLoadingSettings.DelayGraphicLoading = false;
                Assert.That(DeferredGraphicAtlasSkip.Prepare(), Is.False);
            }
            finally
            {
                FasterGameLoadingSettings.DelayGraphicLoading = previous;
                DelayedActions.ReleaseStartupSettingsForTests();
            }
        }

        [Test]
        public void Prefix_WithoutSuppression_AllowsInsertion()
        {
            Assert.That(DeferredGraphicAtlasSkip.Prefix(), Is.True);
        }

        [Test]
        public void RunWithoutStaticAtlas_SuppressesInsertionOnlyInsideTheAction()
        {
            bool? suppressedInside = null;

            DeferredGraphicAtlasSkip.RunWithoutStaticAtlas(() => suppressedInside = !DeferredGraphicAtlasSkip.Prefix());

            Assert.That(suppressedInside, Is.True);
            Assert.That(DeferredGraphicAtlasSkip.Prefix(), Is.True, "範圍結束後必須還原，否則後續所有圖形都不會進圖集。");
        }

        [Test]
        public void RunWithoutStaticAtlas_WhenActionThrows_RestoresInsertion()
        {
            Assert.Throws<InvalidOperationException>(() =>
                DeferredGraphicAtlasSkip.RunWithoutStaticAtlas(() => throw new InvalidOperationException("boom")));

            Assert.That(DeferredGraphicAtlasSkip.Prefix(), Is.True);
        }

        [Test]
        public void RunWithoutStaticAtlas_NestedScopes_RestoreAfterTheOutermost()
        {
            bool? suppressedAfterInner = null;

            DeferredGraphicAtlasSkip.RunWithoutStaticAtlas(() =>
            {
                DeferredGraphicAtlasSkip.RunWithoutStaticAtlas(() => { });
                suppressedAfterInner = !DeferredGraphicAtlasSkip.Prefix();
            });

            Assert.That(suppressedAfterInner, Is.True, "內層結束後外層仍在抑制中。");
            Assert.That(DeferredGraphicAtlasSkip.Prefix(), Is.True);
        }
    }
}
