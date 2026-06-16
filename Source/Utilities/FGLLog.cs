using System;
using System.Collections.Concurrent;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// FasterGameLoading 專用的統一日誌與錯誤處理工具。
    /// 自動加上 [FasterGameLoading] 字首，並格式化 Exception 的呼叫堆疊。
    ///
    /// 執行緒安全：Verse.Log.Message/Warning/Error 並非執行緒安全（內部對訊息清單做
    /// 非同步 Add，且錯誤會操作 log 視窗）。本模組有多個背景執行緒（XML 掃描、型別預載、
    /// 紋理預讀、啟動收尾 Task 等）會記錄日誌，若直接從背景執行緒呼叫 Verse.Log，可能與
    /// 主執行緒的渲染／記錄並行而損毀清單結構，導致硬卡死或極度卡頓。
    /// 因此：字串組裝（含 Exception 讀取）可在任意執行緒進行，但實際呼叫 Verse.Log 一律
    /// 收斂到主執行緒——背景執行緒只把組好的訊息放進佇列，由主執行緒 flush。
    /// </summary>
    public static class FGLLog
    {
        private const string Prefix = "[FasterGameLoading] ";

        private enum LogLevel { Message, Warning, Error }

        /// <summary>背景執行緒待寫入的日誌佇列，由主執行緒消耗。</summary>
        private static readonly ConcurrentQueue<(LogLevel level, string text)> pending =
            new ConcurrentQueue<(LogLevel, string)>();

        public static void Message(string message)
        {
            if (!FasterGameLoadingSettings.VerboseLogging)
                return;
            Emit(LogLevel.Message, Prefix + message);
        }

        public static void Warning(string message)
        {
            Emit(LogLevel.Warning, Prefix + message);
        }

        public static void Warning(string message, Exception ex)
        {
            Emit(LogLevel.Warning, Prefix + message + " - Exception: " + ex);
        }

        public static void Error(string message)
        {
            Emit(LogLevel.Error, Prefix + message);
        }

        public static void Error(string message, Exception ex)
        {
            // ex.ToString() 已包含完整的例外訊息與呼叫堆疊，不需再附加 new StackTrace()
            Emit(LogLevel.Error, Prefix + message + " - Exception: " + ex);
        }

        /// <summary>
        /// 主執行緒：先排空背景緒累積的訊息（維持大致時序）再直接寫入。
        /// 背景執行緒：僅入列，待主執行緒 flush。
        /// </summary>
        private static void Emit(LogLevel level, string text)
        {
            if (UnityData.IsInMainThread)
            {
                FlushPending();
                Write(level, text);
            }
            else
            {
                pending.Enqueue((level, text));
            }
        }

        private static void Write(LogLevel level, string text)
        {
            switch (level)
            {
                case LogLevel.Message:
                    Log.Message(text);
                    break;
                case LogLevel.Warning:
                    Log.Warning(text);
                    break;
                case LogLevel.Error:
                    Log.Error(text);
                    break;
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
                Write(entry.level, entry.text);
            }
        }
    }
}
