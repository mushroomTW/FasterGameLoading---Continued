using System;
using System.Collections.Concurrent;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// FasterGameLoading 專用的統一日誌與錯誤處理工具。
    /// 自動加上 [FasterGameLoading] 字首，並格式化 Exception 的呼叫堆疊。
    ///
    /// 執行緒安全：Verse.Log 與遊戲日誌 UI 會共用狀態；背景執行緒直接寫入
    /// 可能與主執行緒的渲染及日誌處理衝突。
    /// 因此：字串組裝（含 Exception 讀取）可在任意執行緒進行，但實際呼叫 Verse.Log 一律
    /// 收斂到主執行緒；背景執行緒只把組好的訊息放進佇列，由主執行緒排空。
    /// </summary>
    public static class FGLLog
    {
        private const string Prefix = "[FasterGameLoading] ";

        /// <summary>背景執行緒待寫入的日誌佇列，由主執行緒消耗。</summary>
        private static readonly ConcurrentQueue<(Action<string> writeAction, string text)> pending =
            new ConcurrentQueue<(Action<string>, string)>();

        public static void Message(string message)
        {
            if (!FasterGameLoadingSettings.VerboseLogging)
                return;
            Emit(Log.Message, Prefix + message);
        }

        public static void Warning(string message, Exception ex = null)
        {
            Emit(Log.Warning, ex == null ? Prefix + message : Prefix + message + "\n" + ex);
        }

        public static void Error(string message, Exception ex = null)
        {
            // ex.ToString() 已包含完整的例外訊息與呼叫堆疊，不需再附加 new StackTrace()。
            // 以換行分隔，使「訊息以冒號結尾」時輸出自然（message:\n<例外>），避免「: - Exception:」的彆扭排版。
            Emit(Log.Error, ex == null ? Prefix + message : Prefix + message + "\n" + ex);
        }

        /// <summary>
        /// 主執行緒：先排空背景緒累積的訊息（維持大致時序）再直接寫入。
        /// 背景執行緒：僅入列，待主執行緒 flush。
        /// </summary>
        private static void Emit(Action<string> writeAction, string text)
        {
            if (UnityData.IsInMainThread)
            {
                FlushPending();
                writeAction(text);
            }
            else
            {
                pending.Enqueue((writeAction, text));
            }
        }

        /// <summary>
        /// 排空背景執行緒累積的日誌佇列。<b>必須在主執行緒呼叫</b>
        /// （由 <see cref="DelayedActions.Update"/> 每幀觸發，以及任何主執行緒日誌呼叫時順帶觸發）。
        /// </summary>
        public static void FlushPending()
        {
            while (pending.TryDequeue(out var entry))
            {
                entry.writeAction(entry.text);
            }
        }
    }
}
