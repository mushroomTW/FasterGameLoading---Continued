using System;
using System.Diagnostics;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// FasterGameLoading 專用的統一日誌與錯誤處理工具。
    /// 自動加上 [FasterGameLoading] 字首，並格式化 Exception 的呼叫堆疊。
    /// </summary>
    public static class FGLLog
    {
        private const string Prefix = "[FasterGameLoading] ";

        public static void Message(string message)
        {
            if (FasterGameLoadingSettings.VerboseLogging)
            {
                Log.Message(Prefix + message);
            }
        }

        public static void Warning(string message)
        {
            Log.Warning(Prefix + message);
        }

        public static void Warning(string message, Exception ex)
        {
            Log.Warning(Prefix + message + " - Exception: " + ex.Message + "\n" + ex.StackTrace);
        }

        public static void Error(string message)
        {
            Log.Error(Prefix + message);
        }

        public static void Error(string message, Exception ex)
        {
            Log.Error(Prefix + message + " - Exception: " + ex + "\n" + new StackTrace());
        }
    }
}
