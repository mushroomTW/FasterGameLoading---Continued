using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Verse;

namespace FasterGameLoading
{
    /// <summary>一次遊戲行程中，FGL 各模組需要收到通知的時間點。</summary>
    public enum LifecyclePhase
    {
        /// <summary>
        /// StaticConstructorOnStartupUtility.CallAll 完成：初次啟動與每次切換語言重載後都會觸發。
        /// 用來結算本輪載入的資料（例如本 session 查過的型別）、釋放只在載入期間需要的暫存。
        /// </summary>
        StartupCompleted,

        /// <summary>
        /// 即將 ClearAllPlayData 重載所有內容之前：切換語言，或原版載入失敗後的自動恢復。
        /// 原版會銷毀並重新載入 Def 與貼圖，以舊物件為鍵或值的快取必須在此清空，讓重載流程完整執行。
        /// </summary>
        LanguageReloading,
    }

    /// <summary>
    /// 各模組在自己的靜態建構子或 Mod 建構子中以 <see cref="On"/> 登記處理常式，
    /// 由 Startup、LanguageDatabase.SelectLanguage 與 PlayDataLoader.ClearAllPlayData 的補丁在對應時間點 <see cref="Raise"/>。
    /// 同一階段依登記順序執行；個別處理常式的例外只記錄，不影響其餘處理常式。
    /// </summary>
    public static class SessionLifecycle
    {
        private static readonly Dictionary<LifecyclePhase, List<Action>> handlers = new Dictionary<LifecyclePhase, List<Action>>
        {
            [LifecyclePhase.StartupCompleted] = new List<Action>(),
            [LifecyclePhase.LanguageReloading] = new List<Action>(),
        };

        /// <summary>登記在 <paramref name="phase"/> 時要執行的處理常式；每次進入該階段都會執行。</summary>
        public static void On(LifecyclePhase phase, Action handler)
        {
            if (handler == null) return;
            lock (handlers)
            {
                handlers[phase].Add(handler);
            }
        }

        /// <summary>依登記順序執行 <paramref name="phase"/> 的所有處理常式。</summary>
        internal static void Raise(LifecyclePhase phase)
        {
            Action[] snapshot;
            lock (handlers)
            {
                snapshot = handlers[phase].ToArray();
            }
            foreach (var handler in snapshot)
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    FGLLog.Error($"Error in {phase} handler:", ex);
                }
            }
        }

        private sealed class MainThreadRaise
        {
            private const int Pending = 0;
            private const int Taken = 1;
            private const int Cancelled = 2;

            public readonly LifecyclePhase Phase;
            // 只用 Wait／Set、從不存取 WaitHandle，不會配置核心物件，因此不需 Dispose
            //（主執行緒 Set 之後等待端才醒來，此時 Dispose 反而可能與 Set 的收尾競爭）。
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(initialState: false);
            private int state;

            public MainThreadRaise(LifecyclePhase phase) => Phase = phase;

            public bool TryTake() => Interlocked.CompareExchange(ref state, Taken, Pending) is Pending;

            public bool TryCancel() => Interlocked.CompareExchange(ref state, Cancelled, Pending) is Pending;
        }

        private static readonly ConcurrentQueue<MainThreadRaise> pendingMainThreadRaises = new ConcurrentQueue<MainThreadRaise>();

        /// <summary>非主執行緒等待主執行緒代為觸發階段的上限。可於測試中調低。</summary>
        internal static int MainThreadRaiseTimeoutMs { get; set; } = 10_000;

        /// <summary>主執行緒上次呼叫 <see cref="DrainMainThreadRaises"/> 的 Stopwatch 時間戳；0 代表從未呼叫。</summary>
        private static long lastDrainTimestamp;

        /// <summary>
        /// 從任何執行緒觸發 <paramref name="phase"/>：主執行緒直接執行；其他執行緒（例如原版錯誤恢復所在的載入事件緒）
        /// 交由主執行緒在 <see cref="DrainMainThreadRaises"/> 執行並阻塞等待，因為部分處理常式會停止協程或存取只在主執行緒使用的集合。
        /// 逾時仍未開始執行時放棄並回傳 false，不讓過期的請求之後清掉新一輪的狀態。
        /// </summary>
        internal static bool RaiseOnMainThread(LifecyclePhase phase)
        {
            if (UnityData.IsInMainThread)
            {
                Raise(phase);
                return true;
            }

            // 泵送從未執行或已停擺超過等待上限（例如 DelayedActions 被停用或銷毀）：等也不會有人執行，直接放棄。
            long lastDrain = Interlocked.Read(ref lastDrainTimestamp);
            if (lastDrain == 0 || (Stopwatch.GetTimestamp() - lastDrain) * 1000 / Stopwatch.Frequency >= MainThreadRaiseTimeoutMs)
            {
                return false;
            }

            var request = new MainThreadRaise(phase);
            pendingMainThreadRaises.Enqueue(request);
            if (request.Done.Wait(MainThreadRaiseTimeoutMs))
            {
                return true;
            }
            if (request.TryCancel())
            {
                return false;
            }
            // 逾時的同時主執行緒已開始執行：等它完成，避免後續重載與處理常式並行。
            request.Done.Wait();
            return true;
        }

        /// <summary>主執行緒呼叫（DelayedActions.Update 每幀一次）：執行其他執行緒交付的階段觸發。</summary>
        internal static void DrainMainThreadRaises()
        {
            Interlocked.Exchange(ref lastDrainTimestamp, Stopwatch.GetTimestamp());
            while (pendingMainThreadRaises.TryDequeue(out var request))
            {
                if (!request.TryTake())
                {
                    continue;
                }
                try
                {
                    Raise(request.Phase);
                }
                finally
                {
                    request.Done.Set();
                }
            }
        }
    }
}
