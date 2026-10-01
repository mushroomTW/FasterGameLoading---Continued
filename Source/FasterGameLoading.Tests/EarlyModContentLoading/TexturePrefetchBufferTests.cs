using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace FasterGameLoading.Tests.EarlyModContentLoading
{
    [TestFixture]
    public class TexturePrefetchBufferTests
    {
        private static long Prefetch(TexturePrefetchBuffer buffer, string path, int length, int group = 0)
        {
            Assert.That(buffer.TryRegister(path, group, out var sequence), Is.True);
            Assert.That(buffer.WaitForRoom(group, sequence, length, timeoutMs: 0), Is.True);
            buffer.Store(group, sequence, path, new byte[length]);
            return sequence;
        }

        [Test]
        public void TryTake_FileOfAnotherMod_KeepsEarlierModsPrefetchedFiles()
        {
            // mod 之間的順序只是推測：提早載入沒跑完時原版依載入順序接手，可能先讀到排在後面的 mod。
            // 這不代表前面 mod 的貼圖被略過，不能因此淘汰它們。
            var buffer = new TexturePrefetchBuffer(100);
            Prefetch(buffer, "a.png", 10, group: 0);
            Prefetch(buffer, "b.png", 10, group: 0);
            Prefetch(buffer, "x.png", 10, group: 1);

            Assert.That(buffer.TryTake("x.png", out _), Is.True);

            Assert.That(buffer.Evicted, Is.EqualTo(0));
            Assert.That(buffer.TryTake("a.png", out _), Is.True);
            Assert.That(buffer.TryTake("b.png", out _), Is.True);
        }

        [Test]
        public void TryTake_ReturnsPrefetchedBytesOnlyOnce()
        {
            var buffer = new TexturePrefetchBuffer(100);
            Prefetch(buffer, "a.png", 10);

            Assert.That(buffer.TryTake("a.png", out var data), Is.True);
            Assert.That(data, Has.Length.EqualTo(10));
            Assert.That(buffer.TryTake("a.png", out _), Is.False);
            Assert.That(buffer.Hits, Is.EqualTo(1));
            Assert.That(buffer.Misses, Is.EqualTo(1));
        }

        [Test]
        public void TryTake_LaterFile_EvictsSkippedEarlierFilesAndFreesBudget()
        {
            // 主執行緒取到 c，代表 a、b 已被略過（例如改由降質快取載入），它們不能一直佔著預算。
            var buffer = new TexturePrefetchBuffer(30);
            Prefetch(buffer, "a.png", 10);
            Prefetch(buffer, "b.png", 10);
            Prefetch(buffer, "c.png", 10);

            Assert.That(buffer.TryTake("c.png", out _), Is.True);

            Assert.That(buffer.Evicted, Is.EqualTo(2));
            Assert.That(buffer.TryTake("a.png", out _), Is.False);
            Assert.That(buffer.TryRegister("d.png", 0, out var sequence), Is.True);
            Assert.That(buffer.WaitForRoom(0, sequence, 30, timeoutMs: 0), Is.True);
        }

        [Test]
        public void WaitForRoom_WhenBudgetFull_WaitsUntilMainThreadTakesAFile()
        {
            var buffer = new TexturePrefetchBuffer(20);
            Prefetch(buffer, "a.png", 10);
            Prefetch(buffer, "b.png", 10);
            Assert.That(buffer.TryRegister("c.png", 0, out var sequence), Is.True);

            Assert.That(buffer.WaitForRoom(0, sequence, 10, timeoutMs: 0), Is.False);

            var waiting = Task.Run(() => buffer.WaitForRoom(0, sequence, 10, timeoutMs: 5000));
            Thread.Sleep(50);
            Assert.That(waiting.IsCompleted, Is.False);
            Assert.That(buffer.TryTake("a.png", out _), Is.True);
            Assert.That(waiting.Wait(5000) && waiting.Result, Is.True);
        }

        [Test]
        public void WaitForRoom_EmptyBuffer_AdmitsFileLargerThanRemainingBudget()
        {
            var buffer = new TexturePrefetchBuffer(10);
            Assert.That(buffer.TryRegister("big.png", 0, out var sequence), Is.True);

            Assert.That(buffer.WaitForRoom(0, sequence, 10, timeoutMs: 0), Is.True);
        }

        [Test]
        public void TryRegister_FileAlreadyReadOnDemand_IsSkippedAndAdvancesPastIt()
        {
            // 主執行緒比預讀端快：它自行讀過的檔案，預讀端登記時直接越過，不再讀第二次。
            var buffer = new TexturePrefetchBuffer(100);
            Assert.That(buffer.TryTake("a.png", out _), Is.False);

            Assert.That(buffer.TryRegister("a.png", 0, out _), Is.False);
            Assert.That(buffer.TryRegister("b.png", 0, out _), Is.True);
        }

        [Test]
        public void Store_AfterMainThreadPassedTheFile_DiscardsIt()
        {
            var buffer = new TexturePrefetchBuffer(100);
            Assert.That(buffer.TryRegister("a.png", 0, out var sequenceA), Is.True);
            Assert.That(buffer.TryRegister("b.png", 0, out var sequenceB), Is.True);
            Prefetch(buffer, "c.png", 10);
            buffer.Store(0, sequenceB, "b.png", new byte[10]);

            // 預讀端還在讀 a 時，主執行緒已經取用了更後面的 c。
            Assert.That(buffer.TryTake("c.png", out _), Is.True);
            buffer.Store(0, sequenceA, "a.png", new byte[10]);

            Assert.That(buffer.TryTake("a.png", out _), Is.False);
        }

        [Test]
        public void Stop_ReleasesWaitingPrefetcherAndClearsBuffer()
        {
            var buffer = new TexturePrefetchBuffer(10);
            Prefetch(buffer, "a.png", 10);
            Assert.That(buffer.TryRegister("b.png", 0, out var sequence), Is.True);
            var waiting = Task.Run(() => buffer.WaitForRoom(0, sequence, 10, timeoutMs: 5000));
            Thread.Sleep(50);

            buffer.Stop();

            Assert.That(waiting.Wait(5000), Is.True);
            Assert.That(waiting.Result, Is.False);
            Assert.That(buffer.TryTake("a.png", out _), Is.False);
            Assert.That(buffer.TryRegister("c.png", 0, out _), Is.False);
        }
    }
}
