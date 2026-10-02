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
                    MoveOrReplace(tmp, path);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    TryDeleteFile(tmp);
                    if (i == maxRetries - 1) throw;
                    Thread.Sleep(delayMs);
                }
            }
        }

        private static void MoveOrReplace(string tmp, string path)
        {
            if (File.Exists(path))
            {
                // .NET 4.7.2 的 File.Move 不支援覆寫，目標存在時以 File.Replace 原子性替換。
                File.Replace(tmp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // 忽略暫存檔刪除失敗的例外，避免遮蔽主要 I/O 例外
            }
        }
    }
}
