using System;
using System.Collections.Generic;
using System.IO;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 在背景執行緒中異步掃描所有啟用中第三方 Mod 的 XML 檔案，
    /// 計算累積雜湊值以偵測檔案內容異動，並動態決定是否失效快取。
    /// </summary>
    public static class XmlChangeDetector
    {
        /// <summary>
        /// 標記是否需要在主執行緒寫入設定檔（執行緒安全的跨執行緒排程旗標）。
        /// </summary>
        public static volatile bool needWriteSettings = false;

        public static void ScanXmlFilesAsync(List<string> modPaths)
        {
            if (modPaths == null || modPaths.Count == 0)
            {
                XmlNode_SelectSingleNode_Patch.isXmlScanComplete = true;
                return;
            }

            try
            {
                long combinedHash = 0;
                int xmlCount = 0;

                foreach (var modPath in modPaths)
                {
                    if (string.IsNullOrEmpty(modPath) || !Directory.Exists(modPath))
                        continue;

                    // 掃描 Defs 目錄
                    var defsPath = Path.Combine(modPath, "Defs");
                    if (Directory.Exists(defsPath))
                    {
                        ScanDirectory(defsPath, ref combinedHash, ref xmlCount);
                    }

                    // 掃描 Patches 目錄
                    var patchesPath = Path.Combine(modPath, "Patches");
                    if (Directory.Exists(patchesPath))
                    {
                        ScanDirectory(patchesPath, ref combinedHash, ref xmlCount);
                    }
                }

                if (FasterGameLoadingSettings.VerboseLogging)
                {
                    FGLLog.Message($"XML scan complete. Found {xmlCount} XML files. Calculated hash: {combinedHash}, last saved hash: {SessionCache.xmlCombinedHashSinceLastSession}");
                }

                // 比對雜湊值
                if (SessionCache.xmlCombinedHashSinceLastSession != combinedHash)
                {
                    // 雜湊不一致，說明玩家修改了 XML，失效 XPath 查詢快取
                    SessionCache.xmlPathsSinceLastSession.Clear();
                    SessionCache.xmlCombinedHashSinceLastSession = combinedHash;

                    // 設置旗標，通知主執行緒在 LateUpdate 中儲存更新後的雜湊值
                    needWriteSettings = true;
                }
            }
            catch (Exception ex)
            {
                FGLLog.Warning("Error during background XML file scan: " + ex.Message);
            }
            finally
            {
                // 無論如何，將掃描標記設為完成，確保主執行緒加載順利啟用攔截
                XmlNode_SelectSingleNode_Patch.isXmlScanComplete = true;
            }
        }

        private static void ScanDirectory(string dirPath, ref long combinedHash, ref int xmlCount)
        {
            try
            {
                // EnumerateFiles 效能比 GetFiles 佳，在背景異步執行能避免記憶體瞬間大量配發
                foreach (var file in Directory.EnumerateFiles(dirPath, "*.xml", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Exists)
                        {
                            // 使用位元 XOR 運算，保證檔案順序不同（例如作業系統遍歷順序不一致）時算出的 Hash 依然相同
                            combinedHash ^= info.LastWriteTimeUtc.Ticks ^ info.Length;
                            xmlCount++;
                        }
                    }
                    catch
                    {
                        // 忽略個別檔案讀取權限異常
                    }
                }
            }
            catch
            {
                // 忽略整個目錄權限異常
            }
        }
    }
}
