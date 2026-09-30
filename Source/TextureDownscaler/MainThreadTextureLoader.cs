using System;
using System.Collections.Concurrent;
using System.Threading;
using RimWorld.IO;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 讓背景執行緒（提早載入、平行 XML）安全取得貼圖：請求交給主執行緒載入，背景端阻塞等待結果。
    /// 泵送契約：主執行緒必須持續呼叫 <see cref="Drain"/>。DelayedActions.Update 每幀呼叫一次，
    /// ModContentPack.ReloadContentInt 的補丁在每個 mod 載入前也呼叫一次（長事件期間 Update 可能暫停）。
    /// </summary>
    public static class MainThreadTextureLoader
    {
        private static readonly ConcurrentQueue<LoadRequest> pending = new ConcurrentQueue<LoadRequest>();
        private static bool draining;

        /// <summary>背景執行緒等待主執行緒代為載入貼圖的上限。可於測試中調低。</summary>
        internal static int RedirectTimeoutMs { get; set; } = 10_000;

        /// <summary>主執行緒實際執行的載入；測試以替身取代，因為 headless 環境沒有 Unity 圖形裝置。</summary>
        internal static Func<VirtualFile, Texture2D> LoadOnMainThread { get; set; } = static file => ModContentLoader<Texture2D>.LoadTexture(file);

        /// <summary>尚未被主執行緒取走的請求數。</summary>
        internal static int PendingCount => pending.Count;

        private sealed class LoadRequest
        {
            private const int Pending = 0;
            private const int Taken = 1;
            private const int Cancelled = 2;

            public VirtualFile File;
            public Texture2D Result;
            public Exception Exception;
            public readonly ManualResetEventSlim CompletedEvent = new ManualResetEventSlim(initialState: false);
            private int state;

            /// <summary>主執行緒取得處理權；請求已被等待端放棄時回傳 false。</summary>
            public bool TryTake()
            {
                return Interlocked.CompareExchange(ref state, Taken, Pending) is Pending;
            }

            /// <summary>
            /// 等待端放棄請求；主執行緒已開始處理時回傳 false，
            /// 呼叫端必須等它完成並接手結果，否則載入出的貼圖會無人持有而洩漏。
            /// </summary>
            public bool Cancel()
            {
                return Interlocked.CompareExchange(ref state, Cancelled, Pending) is Pending;
            }
        }

        /// <summary>
        /// 背景執行緒呼叫：把 <paramref name="file"/> 交給主執行緒載入並阻塞等待。
        /// 逾時、失敗一律回傳 null；主執行緒已開始處理時會等它完成，不因逾時放棄。
        /// </summary>
        public static Texture2D Load(VirtualFile file)
        {
            var request = new LoadRequest { File = file };
            pending.Enqueue(request);

            if (!request.CompletedEvent.Wait(RedirectTimeoutMs))
            {
                if (request.Cancel())
                {
                    FGLLog.Warning($"Timeout waiting for texture loading on main thread: {file.FullPath}");
                    return null;
                }

                // 逾時的同時主執行緒已取得處理權：等它完成並接手結果，避免貼圖洩漏。
                request.CompletedEvent.Wait();
            }

            if (request.Exception != null)
            {
                FGLLog.Warning($"Error loading texture on main thread redirect: {request.Exception.Message}");
                return null;
            }
            return request.Result;
        }

        /// <summary>
        /// 主執行緒呼叫：處理所有待處理請求。載入過程中重入（例如載入觸發另一次泵送）時直接返回，不遞迴消費佇列。
        /// </summary>
        public static void Drain()
        {
            if (draining) return;
            draining = true;
            try
            {
                while (pending.TryDequeue(out var request))
                {
                    if (!request.TryTake())
                    {
                        continue;
                    }

                    try
                    {
                        request.Result = LoadOnMainThread(request.File);
                    }
                    catch (Exception ex)
                    {
                        request.Exception = ex;
                    }
                    finally
                    {
                        request.CompletedEvent.Set();
                    }
                }
            }
            finally
            {
                draining = false;
            }
        }
    }
}
