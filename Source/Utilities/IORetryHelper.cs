using System;
using System.IO;
using System.Threading;

namespace FasterGameLoading
{
    /// <summary>
    /// 提供帶有重試機制的檔案 I/O 操作輔助類別，
    /// 用於因應 SSD 繁忙或防毒軟體掃描鎖檔時的寫入失敗。
    /// </summary>
    public static class IORetryHelper
    {
        /// <summary>
        /// 帶重試機制的 File.WriteAllBytes 寫入。
        /// </summary>
        public static void WriteAllBytesWithRetry(string path, byte[] bytes, int maxRetries = 3, int delayMs = 100)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    File.WriteAllBytes(path, bytes);
                    return;
                }
                catch (IOException)
                {
                    if (i == maxRetries - 1) throw;
                    Thread.Sleep(delayMs);
                }
                catch (UnauthorizedAccessException)
                {
                    if (i == maxRetries - 1) throw;
                    Thread.Sleep(delayMs);
                }
            }
        }

        /// <summary>
        /// 帶重試機制的 File.WriteAllText 寫入。
        /// </summary>
        public static void WriteAllTextWithRetry(string path, string text, int maxRetries = 3, int delayMs = 100)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    File.WriteAllText(path, text);
                    return;
                }
                catch (IOException)
                {
                    if (i == maxRetries - 1) throw;
                    Thread.Sleep(delayMs);
                }
                catch (UnauthorizedAccessException)
                {
                    if (i == maxRetries - 1) throw;
                    Thread.Sleep(delayMs);
                }
            }
        }
    }
}
