using System;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RimWorld.IO;
using UnityEngine;

namespace FasterGameLoading.Tests.TextureDownscaler
{
    [TestFixture]
    public class MainThreadTextureLoaderTests
    {
        private const int TestTimeoutMs = 300;

        private int originalTimeoutMs;
        private Func<VirtualFile, Texture2D> originalLoad;
        private int loaderCalls;

        /// <summary>不觸碰檔案系統的 VirtualFile 替身，只需要 FullPath 可讀。</summary>
        private sealed class FakeVirtualFile : VirtualFile
        {
            private readonly string path;

            public FakeVirtualFile(string path)
            {
                this.path = path;
            }

            public override string Name => Path.GetFileName(path);
            public override string FullPath => path;
            public override bool Exists => true;
            public override Stream CreateReadStream() => new MemoryStream(Array.Empty<byte>());
            public override byte[] ReadAllBytes() => Array.Empty<byte>();
            public override string ReadAllText() => string.Empty;
            public override string[] ReadAllLines() => Array.Empty<string>();
            public override long Length => 0L;
        }

        private static Texture2D NewDetachedTexture()
        {
            return (Texture2D)FormatterServices.GetUninitializedObject(typeof(Texture2D));
        }

        [SetUp]
        public void SetUp()
        {
            originalTimeoutMs = MainThreadTextureLoader.RedirectTimeoutMs;
            originalLoad = MainThreadTextureLoader.LoadOnMainThread;
            MainThreadTextureLoader.RedirectTimeoutMs = TestTimeoutMs;
            loaderCalls = 0;
        }

        [TearDown]
        public void TearDown()
        {
            MainThreadTextureLoader.Drain();
            MainThreadTextureLoader.RedirectTimeoutMs = originalTimeoutMs;
            MainThreadTextureLoader.LoadOnMainThread = originalLoad;
        }

        private void UseLoader(Func<VirtualFile, Texture2D> load)
        {
            MainThreadTextureLoader.LoadOnMainThread = file =>
            {
                Interlocked.Increment(ref loaderCalls);
                return load(file);
            };
        }

        /// <summary>在背景呼叫 Load，並以本執行緒扮演主執行緒持續泵送，直到 Load 回傳。</summary>
        private static Texture2D LoadWhilePumping(string path)
        {
            var load = Task.Run(() => MainThreadTextureLoader.Load(new FakeVirtualFile(path)));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!load.IsCompleted && DateTime.UtcNow < deadline)
            {
                MainThreadTextureLoader.Drain();
                Thread.Yield();
            }
            Assert.That(load.Wait(TimeSpan.FromSeconds(5)), Is.True, "Load 必須在泵送後返回。");
            return load.Result;
        }

        [Test]
        public void Load_ReturnsTextureLoadedByPump()
        {
            var expected = NewDetachedTexture();
            UseLoader(_ => expected);

            Assert.That(LoadWhilePumping("a.png"), Is.SameAs(expected));
            Assert.That(loaderCalls, Is.EqualTo(1));
        }

        [Test]
        public void Load_WhenLoaderThrows_ReturnsNullWithoutWaitingForTimeout()
        {
            UseLoader(_ => throw new InvalidOperationException("Simulated missing graphics device"));
            var watch = System.Diagnostics.Stopwatch.StartNew();

            Assert.That(LoadWhilePumping("b.png"), Is.Null);
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(TestTimeoutMs * 3), "即使載入失敗也必須喚醒等待端，否則呼叫端會空等到逾時。");
        }

        [Test]
        public void Load_WhenPumpNeverRuns_TimesOutAndLaterDrainSkipsCancelledRequest()
        {
            UseLoader(_ => NewDetachedTexture());

            Assert.That(MainThreadTextureLoader.Load(new FakeVirtualFile("c.png")), Is.Null);
            MainThreadTextureLoader.Drain();

            Assert.That(loaderCalls, Is.Zero, "等待端已放棄的請求不得再載入，否則貼圖無人持有而洩漏。");
            Assert.That(MainThreadTextureLoader.PendingCount, Is.Zero);
        }

        [Test]
        public void Load_WhenMainThreadTookRequestBeforeTimeout_WaitsForItsResult()
        {
            // 主執行緒已取得處理權，但完成時間晚於逾時：等待端不得放棄，
            // 否則主執行緒載入出的貼圖會無人持有而洩漏，呼叫端也拿不到貼圖。
            var expected = NewDetachedTexture();
            UseLoader(_ =>
            {
                Thread.Sleep(TestTimeoutMs + 300);
                return expected;
            });

            Assert.That(LoadWhilePumping("race.png"), Is.SameAs(expected));
        }

        [Test]
        public void Drain_WhenReenteredDuringLoad_DoesNotConsumeQueueRecursively()
        {
            int depth = 0;
            int maxDepth = 0;
            UseLoader(_ =>
            {
                maxDepth = Math.Max(maxDepth, ++depth);
                MainThreadTextureLoader.Drain();
                depth--;
                return NewDetachedTexture();
            });
            var first = Task.Run(() => MainThreadTextureLoader.Load(new FakeVirtualFile("d.png")));
            var second = Task.Run(() => MainThreadTextureLoader.Load(new FakeVirtualFile("e.png")));
            Assert.That(SpinWait.SpinUntil(() => MainThreadTextureLoader.PendingCount == 2, TimeSpan.FromSeconds(2)), Is.True);

            MainThreadTextureLoader.Drain();

            Assert.That(Task.WaitAll(new Task[] { first, second }, TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(loaderCalls, Is.EqualTo(2));
            Assert.That(maxDepth, Is.EqualTo(1), "已在泵送中時再次呼叫必須直接返回。");
        }
    }
}
