using System;
using System.IO;
using System.Threading;

namespace FasterGameLoading
{
    /// <summary>
    /// 提供帶有重試機制的檔案 I/O 操作輔助類別，
    /// 用於因應 SSD 繁忙或防毒軟體掃描鎖檔時的寫入失敗。
    /// 所有寫入均採用先寫暫存檔再移入目標的原子化策略，
    /// 防止快取清單或圖集資料因寫入中斷而損毀。
    /// </summary>
    public static class IORetryHelper
    {
        /// <summary>
        /// 帶重試機制的原子化 File.WriteAllBytes 寫入。
        /// 先寫入 path + ".tmp"，成功後再移入目標路徑。
        /// </summary>
        public static void WriteAllBytesWithRetry(string path, byte[] bytes, int maxRetries = 3, int delayMs = 100)
        {
            string tmp = path + ".tmp";
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    File.WriteAllBytes(tmp, bytes);
                    MoveFileIntoPlace(tmp, path);
                    return;
                }
                catch (IOException)
                {
                    CleanupTempFile(tmp);
                    if (i == maxRetries - 1) throw;
                    Thread.Sleep(delayMs);
                }
                catch (UnauthorizedAccessException)
                {
                    CleanupTempFile(tmp);
                    if (i == maxRetries - 1) throw;
                    Thread.Sleep(delayMs);
                }
            }
        }

        /// <summary>
        /// 帶重試機制的原子化 File.WriteAllText 寫入。
        /// 先寫入 path + ".tmp"，成功後再移入目標路徑。
        /// </summary>
        public static void WriteAllTextWithRetry(string path, string text, int maxRetries = 3, int delayMs = 100)
        {
            string tmp = path + ".tmp";
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    File.WriteAllText(tmp, text);
                    MoveFileIntoPlace(tmp, path);
                    return;
                }
                catch (IOException)
                {
                    CleanupTempFile(tmp);
                    if (i == maxRetries - 1) throw;
                    Thread.Sleep(delayMs);
                }
                catch (UnauthorizedAccessException)
                {
                    CleanupTempFile(tmp);
                    if (i == maxRetries - 1) throw;
                    Thread.Sleep(delayMs);
                }
            }
        }

        /// <summary>
        /// 將暫存檔移入目標路徑。
        /// .NET 4.7.2 的 File.Move 不支援覆蓋，因此目標存在時先以 File.Replace 替換。
        /// </summary>
        private static void MoveFileIntoPlace(string tmp, string dest)
        {
            if (File.Exists(dest))
            {
                // File.Replace(source, dest, backup=null) 為原子性替換，不需備份檔
                File.Replace(tmp, dest, null);
            }
            else
            {
                File.Move(tmp, dest);
            }
        }

        /// <summary>
        /// 忽略錯誤地清理暫存檔，避免失敗時殘留 .tmp 檔案。
        /// </summary>
        private static void CleanupTempFile(string tmp)
        {
            try
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch
            {
                // 清理失敗不影響主流程，靜默忽略
            }
        }
    }
}
