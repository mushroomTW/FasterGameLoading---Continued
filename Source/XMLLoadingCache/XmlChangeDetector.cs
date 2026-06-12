using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 在背景執行緒中異步掃描所有啟用中第三方 Mod 的 XML 檔案，
    /// 計算累積雜湊值以偵測檔案內容異動，並動態決定是否失效快取。
    /// 採用雙層雜湊檢測，避免因 Steam 同步更新導致修改時間變更，但內容未變時，錯誤重置 XPath 快取。
    /// </summary>
    public static class XmlChangeDetector
    {
        /// <summary>
        /// 標記是否需要在主執行緒寫入設定檔（執行緒安全的跨執行緒排程旗標）。
        /// </summary>
        public static volatile bool needWriteSettings = false;

        public static void ScanXmlFiles(List<string> modPaths, string configPath = null)
        {
            if ((modPaths == null || modPaths.Count == 0) && string.IsNullOrEmpty(configPath))
            {
                XmlNode_SelectSingleNode_Patch.isXmlScanComplete = true;
                return;
            }

            try
            {
                bool anyContentChanged = false;
                
                var nextMetadataHashes = new Dictionary<string, long>();
                var nextContentHashes = new Dictionary<string, long>();

                // 1. 掃描所有 Mod
                if (modPaths != null)
                {
                    foreach (var modPath in modPaths)
                    {
                        if (string.IsNullOrEmpty(modPath) || !Directory.Exists(modPath))
                            continue;

                        var key = modPath.ToLowerInvariant();
                        long metadataHash = 0;
                        int xmlCount = 0;

                        // 掃描 Defs 目錄
                        var defsPath = Path.Combine(modPath, FGLConsts.DefsDirName);
                        if (Directory.Exists(defsPath))
                        {
                            ScanDirectoryMetadata(defsPath, ref metadataHash, ref xmlCount);
                        }

                        // 掃描 Patches 目錄
                        var patchesPath = Path.Combine(modPath, FGLConsts.PatchesDirName);
                        if (Directory.Exists(patchesPath))
                        {
                            ScanDirectoryMetadata(patchesPath, ref metadataHash, ref xmlCount);
                        }

                        nextMetadataHashes[key] = metadataHash;

                        // 檢查 metadata 雜湊
                        long lastMetadataHash = 0;
                        SessionCache.xmlMetadataHashByMod.TryGetValue(key, out lastMetadataHash);

                        long contentHash = 0;
                        if (metadataHash == lastMetadataHash && SessionCache.xmlContentHashByMod.TryGetValue(key, out contentHash))
                        {
                            // Metadata 完全沒變，代表檔案沒變，直接沿用上次的實質內容雜湊
                            nextContentHashes[key] = contentHash;
                        }
                        else
                        {
                            // Metadata 改變了（例如 Steam 下載更新、玩家手動修改等）
                            // 深入讀取 XML 檔案內容並計算實質的 MD5 contentHash
                            contentHash = 0;
                            if (Directory.Exists(defsPath))
                            {
                                ScanDirectoryContent(defsPath, ref contentHash);
                            }
                            if (Directory.Exists(patchesPath))
                            {
                                ScanDirectoryContent(patchesPath, ref contentHash);
                            }
                            nextContentHashes[key] = contentHash;

                            long lastContentHash = 0;
                            SessionCache.xmlContentHashByMod.TryGetValue(key, out lastContentHash);
                            if (contentHash != lastContentHash)
                            {
                                anyContentChanged = true;
                            }
                        }
                    }
                }

                // 2. 掃描 Config 目錄
                if (!string.IsNullOrEmpty(configPath) && Directory.Exists(configPath))
                {
                    var key = configPath.ToLowerInvariant();
                    long metadataHash = 0;
                    int xmlCount = 0;
                    ScanDirectoryMetadata(configPath, ref metadataHash, ref xmlCount);
                    nextMetadataHashes[key] = metadataHash;

                    long lastMetadataHash = 0;
                    SessionCache.xmlMetadataHashByMod.TryGetValue(key, out lastMetadataHash);

                    long contentHash = 0;
                    if (metadataHash == lastMetadataHash && SessionCache.xmlContentHashByMod.TryGetValue(key, out contentHash))
                    {
                        nextContentHashes[key] = contentHash;
                    }
                    else
                    {
                        contentHash = 0;
                        ScanDirectoryContent(configPath, ref contentHash);
                        nextContentHashes[key] = contentHash;

                        long lastContentHash = 0;
                        SessionCache.xmlContentHashByMod.TryGetValue(key, out lastContentHash);
                        if (contentHash != lastContentHash)
                        {
                            anyContentChanged = true;
                        }
                    }
                }

                // 3. 更新快取字典
                SessionCache.xmlMetadataHashByMod = nextMetadataHashes;
                SessionCache.xmlContentHashByMod = nextContentHashes;

                // 以確定性排序後進行順序敏感折疊（polynomial rolling hash），
                // 避免不同檔案的變更互相抵消導致雜湊碰撞。
                // 注意：此變更會使現有快取在下次啟動時失效一次，屬預期行為。
                long newCombinedHash = 0;
                foreach (var key in nextContentHashes.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    newCombinedHash = unchecked(newCombinedHash * 31 + nextContentHashes[key]);
                }
                
                if (FasterGameLoadingSettings.VerboseLogging)
                {
                    FGLLog.Message($"XML scan complete. Combined content hash: {newCombinedHash}, last saved hash: {SessionCache.xmlCombinedHashSinceLastSession}");
                }

                if (SessionCache.xmlCombinedHashSinceLastSession != newCombinedHash || anyContentChanged)
                {
                    // 內容實質改變，失效 XPath 查詢快取
                    SessionCache.xmlPathsSinceLastSession.Clear();
                    SessionCache.xmlCombinedHashSinceLastSession = newCombinedHash;
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

        private static void ScanDirectoryMetadata(string dirPath, ref long combinedHash, ref int xmlCount)
        {
            try
            {
                // 排序後再折疊，確保跨平台與跨次執行的確定性
                var files = Directory.EnumerateFiles(dirPath, "*.xml", SearchOption.AllDirectories)
                                     .OrderBy(f => f, StringComparer.Ordinal);
                foreach (var file in files)
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Exists)
                        {
                            long fileHash = 17;
                            fileHash = fileHash * 31 + info.LastWriteTimeUtc.Ticks;
                            fileHash = fileHash * 31 + info.Length;
                            combinedHash = unchecked(combinedHash * 31 + fileHash);
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

        private static void ScanDirectoryContent(string dirPath, ref long combinedHash)
        {
            try
            {
                // 排序後再折疊，確保跨平台與跨次執行的確定性
                var files = Directory.EnumerateFiles(dirPath, "*.xml", SearchOption.AllDirectories)
                                     .OrderBy(f => f, StringComparer.Ordinal);
                foreach (var file in files)
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Exists)
                        {
                            long fileHash = 17;
                            fileHash = fileHash * 31 + info.Length;
                            fileHash = fileHash * 31 + GetFileContentHash(file);
                            combinedHash = unchecked(combinedHash * 31 + fileHash);
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

        private static long GetFileContentHash(string filePath)
        {
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using (var md5 = MD5.Create())
                    {
                        var hashBytes = md5.ComputeHash(fs);
                        return BitConverter.ToInt64(hashBytes, 0);
                    }
                }
            }
            catch
            {
                return 0;
            }
        }
    }
}
