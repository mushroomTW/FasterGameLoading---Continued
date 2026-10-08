using NUnit.Framework;

namespace FasterGameLoading.Tests.Core
{
    [TestFixture]
    public class StartupTimelineTests
    {
        private double now;

        private StartupTimeline NewTimeline() => new StartupTimeline(() => now);

        /// <summary>連續兩幀觀察到主選單：第一幀開始於 <paramref name="firstFrame"/>，畫完後的下一幀開始於 <paramref name="nextFrame"/>。</summary>
        private string ShowMainMenu(StartupTimeline timeline, double firstFrame, double nextFrame)
        {
            now = firstFrame;
            Assert.That(timeline.Observe(mainMenuShown: true, playing: false), Is.Null, "主選單第一幀尚未畫完。");
            now = nextFrame;
            return timeline.Observe(mainMenuShown: true, playing: false);
        }

        [Test]
        public void Observe_DeferredWorkAfterMainMenu_LogsDelaysOnce()
        {
            var timeline = NewTimeline();
            Assert.That(ShowMainMenu(timeline, 48.4, 49.8), Is.Null, "延遲工作尚未完成。");
            now = 50.6;
            timeline.MarkVisualsReady();
            now = 52.8;
            timeline.MarkAllReady();
            now = 52.9;
            var line = timeline.Observe(mainMenuShown: true, playing: false);

            Assert.That(line, Is.EqualTo("Startup timing after the main menu appeared: visuals ready +0.8 s, all deferred work done +3.0 s."));
            Assert.That(timeline.Pending, Is.False);
            Assert.That(timeline.Observe(mainMenuShown: true, playing: false), Is.Null, "只輸出一次。");
        }

        [Test]
        public void Observe_WorkDoneBeforeMainMenu_CountsAsZero()
        {
            // 延遲圖形關閉時管線一開始就進入音效階段，早於主選單；以主選單為下限。
            var timeline = NewTimeline();
            now = 40;
            timeline.MarkVisualsReady();
            timeline.MarkAllReady();
            Assert.That(timeline.Observe(mainMenuShown: false, playing: false), Is.Null);

            Assert.That(ShowMainMenu(timeline, 40.5, 41),
                Is.EqualTo("Startup timing after the main menu appeared: visuals ready +0.0 s, all deferred work done +0.0 s."));
        }

        [Test]
        public void Observe_MainMenuLeftBeforeWorkIsDone_KeepsMainMenuTime()
        {
            // 主選單出現後其他 mod 立刻排入長事件（例如切換語言），之後延遲工作才完成，仍以已記下的主選單時間為準。
            var timeline = NewTimeline();
            ShowMainMenu(timeline, 9.5, 10);
            now = 11;
            Assert.That(timeline.Observe(mainMenuShown: false, playing: false), Is.Null);
            now = 12;
            timeline.MarkVisualsReady();
            timeline.MarkAllReady();
            now = 17;

            Assert.That(timeline.Observe(mainMenuShown: false, playing: false), Does.Contain("visuals ready +2.0 s, all deferred work done +2.0 s."));
        }

        [Test]
        public void Observe_GameEnteredWithoutMainMenu_StopsWithoutLogging()
        {
            // -quicktest 等直接進入地圖：沒有主選單可以比較，不輸出也不再觀察。
            var timeline = NewTimeline();
            now = 30;
            timeline.MarkVisualsReady();
            timeline.MarkAllReady();
            now = 45;

            Assert.That(timeline.Observe(mainMenuShown: false, playing: true), Is.Null);
            Assert.That(timeline.Pending, Is.False);
        }

        [Test]
        public void Marks_KeepFirstOccurrence()
        {
            // 語言重載會再跑一次延遲管線；只保留初次啟動的時間。
            var timeline = NewTimeline();
            ShowMainMenu(timeline, 9.5, 10);
            now = 11;
            timeline.Observe(mainMenuShown: false, playing: false);
            timeline.MarkVisualsReady();
            now = 12;
            timeline.MarkAllReady();
            now = 20;
            timeline.MarkVisualsReady();
            timeline.MarkAllReady();

            Assert.That(timeline.Observe(mainMenuShown: false, playing: false), Does.Contain("visuals ready +1.0 s, all deferred work done +2.0 s."));
        }
    }
}
