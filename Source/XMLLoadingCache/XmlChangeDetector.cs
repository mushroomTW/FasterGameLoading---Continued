using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 在背景執行緒中異步掃描所有啟用中第三方 Mod 的 XML 檔案，
    /// 計算 XML 檔案路徑、大小與修改時間的 metadata 雜湊，並動態決定是否失效快取。
    /// 不讀取 XML 內容，避免大 modlist 啟動時產生大量磁碟 I/O。
    /// </summary>
    public static class XmlChangeDetector
    {
        /// <summary>
        /// 標記是否需要在主執行緒寫入設定檔（執行緒安全的跨執行緒排程旗標）。
        /// </summary>
        public static volatile bool needWriteSettings = false;

        public static void ScanXmlFiles(List<string> modPaths, string configPath = null)
        {
            if (Utils.IsMissileGirlActive)
            {
                XmlNode_SelectSingleNode_Patch.isXmlScanComplete = true;
                return;
            }
            if ((modPaths == null || modPaths.Count == 0) && string.IsNullOrEmpty(configPath))
            {
                XmlNode_SelectSingleNode_Patch.isXmlScanComplete = true;
                return;
            }

            try
            {
                var nextMetadataHashes = new Dictionary<string, long>();
                bool anyMetadataChanged = false;

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

                        if (!SessionCache.xmlMetadataHashByMod.TryGetValue(key, out var lastMetadataHash)
                            || metadataHash != lastMetadataHash)
                        {
                            anyMetadataChanged = true;
                        }
                        nextMetadataHashes[key] = metadataHash;
                    }
                }

                // 2. 掃描 Config 目錄
                if (!string.IsNullOrEmpty(configPath) && Directory.Exists(configPath))
                {
                    var key = configPath.ToLowerInvariant();
                    long metadataHash = 0;
                    int xmlCount = 0;
                    ScanDirectoryMetadata(configPath, ref metadataHash, ref xmlCount);
                    if (!SessionCache.xmlMetadataHashByMod.TryGetValue(key, out var lastMetadataHash)
                        || metadataHash != lastMetadataHash)
                    {
                        anyMetadataChanged = true;
                    }
                    nextMetadataHashes[key] = metadataHash;
                }

                // 3. 更新快取字典
                SessionCache.xmlMetadataHashByMod = nextMetadataHashes;
                SessionCache.xmlContentHashByMod = new Dictionary<string, long>();

                // 以確定性排序後進行順序敏感折疊（polynomial rolling hash），
                // 避免不同檔案的變更互相抵消導致雜湊碰撞。
                long newCombinedHash = CombineMetadataHashes(nextMetadataHashes);

                if (FasterGameLoadingSettings.VerboseLogging)
                {
                    FGLLog.Message($"XML scan complete. Combined metadata hash: {newCombinedHash}, last saved hash: {SessionCache.xmlCombinedHashSinceLastSession}");
                }

                if (SessionCache.xmlCombinedHashSinceLastSession != newCombinedHash || anyMetadataChanged)
                {
                    // XML metadata 改變，失效 XPath 查詢快取
                    SessionCache.xmlPathsSinceLastSession.Clear();
                    SessionCache.xmlCombinedHashSinceLastSession = newCombinedHash;
                    needWriteSettings = true;
                }
            }
            catch (Exception ex)
            {
                FGLLog.Warning("Error during background XML file scan:", ex);
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
                            var relativePath = Path.GetFileName(dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                                + "/"
                                + file.Substring(dirPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            long fileHash = 17;
                            fileHash = fileHash * 31 + StableStringHash(relativePath.Replace('\\', '/'));
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

        private static long StableStringHash(string value)
        {
            unchecked
            {
                long hash = 17;
                if (value == null) return hash;

                for (int i = 0; i < value.Length; i++)
                {
                    hash = hash * 31 + value[i];
                }
                return hash;
            }
        }

        /// <summary>
        /// 將每個 Mod 的 metadata 雜湊值以確定性順序折疊為單一 long。
        /// 採順序敏感的 polynomial rolling hash（乘 31）並以字串序排序鍵，
        /// 確保跨平台、跨次執行結果一致，且不同檔案的變更不會互相抵消。
        /// 純函式，不依賴 RimWorld 執行環境，可單元測試。
        /// </summary>
        internal static long CombineMetadataHashes(IReadOnlyDictionary<string, long> hashesByMod)
        {
            unchecked
            {
                long combined = 0;
                foreach (var key in hashesByMod.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    combined = combined * 31 + hashesByMod[key];
                }
                return combined;
            }
        }
    }
}
