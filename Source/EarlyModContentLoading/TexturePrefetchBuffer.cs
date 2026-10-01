using System;
using System.Collections.Generic;
using System.Threading;

namespace FasterGameLoading
{
    /// <summary>
    /// 原始貼圖背景預讀的暫存區。預讀端依貼圖的載入順序逐一登記、讀檔、存入，總量不超過預算；
    /// 主執行緒依同一順序取用。檔案依所屬 mod 分組：同一個 mod 的貼圖一定依序載入，取用到該組第 N 個檔案，
    /// 代表同組更早而未被取用的檔案已被略過（例如改由降質快取或 .dds 載入），這些項目會被淘汰以釋放預算，
    /// 預讀端也不再讀同組編號不大於 N 的檔案。mod 之間的先後只是推測（提早載入沒跑完時原版會依載入順序接手），
    /// 所以亂序取用其他 mod 的檔案不會淘汰別組的內容。順序與實際載入不一致時只會多讀檔，不影響正確性：
    /// 沒取到的檔案由原方法照常讀取。所有成員皆可跨執行緒呼叫。
    /// </summary>
    internal sealed class TexturePrefetchBuffer
    {
        private readonly object gate = new();
        private readonly Dictionary<string, (long sequence, int group)> registered = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> buffered = new(StringComparer.Ordinal);
        private readonly Dictionary<int, Queue<(long sequence, string path)>> bufferOrderByGroup = new();
        private readonly Dictionary<int, long> consumedSequenceByGroup = new();
        /// <summary>主執行緒在預讀端登記前就自行讀取的檔案；預讀端登記到它們時直接越過。</summary>
        private readonly HashSet<string> readBeforeRegistered = new(StringComparer.Ordinal);
        private long nextSequence;
        private long bufferedBytes;
        private bool stopped;

        internal TexturePrefetchBuffer(long budgetBytes)
        {
            BudgetBytes = budgetBytes;
        }

        internal long BudgetBytes { get; }

        /// <summary>從記憶體取用的檔案數。</summary>
        internal int Hits { get; private set; }

        /// <summary>從記憶體取用的總位元組數。</summary>
        internal long HitBytes { get; private set; }

        /// <summary>主執行緒自行讀檔的次數（預讀端尚未讀到、或已被淘汰）。</summary>
        internal int Misses { get; private set; }

        /// <summary>已預讀但被略過而淘汰的檔案數。</summary>
        internal int Evicted { get; private set; }

        internal bool IsStopped
        {
            get { lock (gate) return stopped; }
        }

        /// <summary>
        /// 依載入順序登記 <paramref name="group"/>（所屬 mod）的下一個要預讀的檔案。
        /// 主執行緒已經讀過或越過它時回傳 false，不必再讀。
        /// </summary>
        internal bool TryRegister(string path, int group, out long sequence)
        {
            lock (gate)
            {
                sequence = nextSequence++;
                if (stopped) return false;
                if (readBeforeRegistered.Remove(path))
                {
                    Advance(group, sequence);
                    return false;
                }
                if (IsPassed(group, sequence)) return false;
                registered[path] = (sequence, group);
                return true;
            }
        }

        /// <summary>
        /// 等到暫存區放得下 <paramref name="length"/> 位元組；暫存區是空的時一律放行，單一大檔不會卡住。
        /// 停止、主執行緒已越過這個檔案或逾時時回傳 false。
        /// </summary>
        internal bool WaitForRoom(int group, long sequence, long length, int timeoutMs = Timeout.Infinite)
        {
            lock (gate)
            {
                while (!stopped && !IsPassed(group, sequence) && bufferedBytes > 0 && bufferedBytes + length > BudgetBytes)
                {
                    if (!Monitor.Wait(gate, timeoutMs)) return false;
                }
                return !stopped && !IsPassed(group, sequence);
            }
        }

        /// <summary>存入讀好的內容；讀檔期間主執行緒已越過這個檔案時丟棄。</summary>
        internal void Store(int group, long sequence, string path, byte[] data)
        {
            lock (gate)
            {
                if (stopped || IsPassed(group, sequence)) return;
                buffered[path] = data;
                if (!bufferOrderByGroup.TryGetValue(group, out var order))
                {
                    order = new Queue<(long sequence, string path)>();
                    bufferOrderByGroup[group] = order;
                }
                order.Enqueue((sequence, path));
                bufferedBytes += data.Length;
            }
        }

        /// <summary>取出預讀好的內容；沒有時回傳 false，由呼叫端自行讀檔。每個項目只能取用一次。</summary>
        internal bool TryTake(string path, out byte[] data)
        {
            lock (gate)
            {
                if (stopped)
                {
                    data = null;
                    return false;
                }

                var isRegistered = registered.TryGetValue(path, out var entry);
                if (buffered.TryGetValue(path, out data))
                {
                    buffered.Remove(path);
                    bufferedBytes -= data.Length;
                    Hits++;
                    HitBytes += data.Length;
                    Advance(entry.group, entry.sequence);
                    Monitor.PulseAll(gate);
                    return true;
                }

                Misses++;
                if (isRegistered)
                {
                    Advance(entry.group, entry.sequence);
                    Monitor.PulseAll(gate);
                }
                else
                {
                    readBeforeRegistered.Add(path);
                }
                return false;
            }
        }

        /// <summary>停止預讀並釋放所有暫存內容；等待中的預讀端會被喚醒並結束。</summary>
        internal void Stop()
        {
            lock (gate)
            {
                stopped = true;
                buffered.Clear();
                bufferOrderByGroup.Clear();
                consumedSequenceByGroup.Clear();
                registered.Clear();
                readBeforeRegistered.Clear();
                bufferedBytes = 0;
                Monitor.PulseAll(gate);
            }
        }

        private bool IsPassed(int group, long sequence)
            => consumedSequenceByGroup.TryGetValue(group, out var consumed) && sequence <= consumed;

        /// <summary>主執行緒已處理到 <paramref name="group"/> 的 <paramref name="sequence"/>：淘汰同組更早而未被取用的項目。</summary>
        private void Advance(int group, long sequence)
        {
            if (IsPassed(group, sequence)) return;
            consumedSequenceByGroup[group] = sequence;
            if (!bufferOrderByGroup.TryGetValue(group, out var order)) return;
            while (order.Count > 0 && order.Peek().sequence < sequence)
            {
                var path = order.Dequeue().path;
                if (buffered.TryGetValue(path, out var skipped))
                {
                    buffered.Remove(path);
                    bufferedBytes -= skipped.Length;
                    Evicted++;
                }
            }
        }
    }
}
