using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

        internal sealed class XmlScanResult
        {
            public readonly Dictionary<string, long> MetadataHashes;
            public readonly int FileCount;
            public readonly long ElapsedMilliseconds;
            public readonly Exception Exception;
            public readonly bool Bypassed;

            public XmlScanResult(Dictionary<string, long> metadataHashes, int fileCount, long elapsedMilliseconds = 0, Exception exception = null, bool bypassed = false)
            {
                MetadataHashes = metadataHashes ?? new Dictionary<string, long>();
                FileCount = fileCount;
                ElapsedMilliseconds = elapsedMilliseconds;
                Exception = exception;
                Bypassed = bypassed;
            }
        }

        public static void ScanXmlFiles(List<string> modPaths, string configPath = null)
        {
            CommitXmlScanResult(ScanXmlMetadata(modPaths, configPath));
        }

        public static void StartScanAsync(List<string> modPaths, string configPath, Action<Action> enqueueMainThreadAction)
        {
            if (enqueueMainThreadAction == null) throw new ArgumentNullException(nameof(enqueueMainThreadAction));

            // 背景工作只保有不可變的路徑副本，絕不讀寫 Verse/SessionCache 狀態。
            var pathCopy = modPaths == null ? new List<string>() : new List<string>(modPaths);
            Task.Run(() => ScanXmlMetadata(pathCopy, configPath)).ContinueWith(task =>
            {
                var result = task.Status == TaskStatus.RanToCompletion
                    ? task.Result
                    : new XmlScanResult(null, 0, exception: task.Exception);
                enqueueMainThreadAction(() => CommitXmlScanResult(result));
            });
        }

        internal static XmlScanResult ScanXmlMetadata(List<string> modPaths, string configPath = null)
        {
            var stopwatch = Stopwatch.StartNew();
            if (Utils.IsMissileGirlActive || ((modPaths == null || modPaths.Count == 0) && string.IsNullOrEmpty(configPath)))
            {
                return new XmlScanResult(null, 0, stopwatch.ElapsedMilliseconds, bypassed: true);
            }

            try
            {
                var nextMetadataHashes = new Dictionary<string, long>();
                int totalXmlCount = 0;

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
                        totalXmlCount += xmlCount;
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
                    totalXmlCount += xmlCount;
                }

                return new XmlScanResult(nextMetadataHashes, totalXmlCount, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                return new XmlScanResult(null, 0, stopwatch.ElapsedMilliseconds, ex);
            }
        }

        internal static void CommitXmlScanResult(XmlScanResult result)
        {
            try
            {
                if (result == null || result.Exception != null)
                {
                    FGLLog.Warning("Error during background XML file scan:", result?.Exception);
                    return;
                }
                if (result.Bypassed) return;

                var previous = SessionCache.xmlMetadataHashByMod;
                bool metadataChanged = previous.Count != result.MetadataHashes.Count
                    || result.MetadataHashes.Any(pair => !previous.TryGetValue(pair.Key, out var oldHash) || oldHash != pair.Value);
                long combinedHash = CombineMetadataHashes(result.MetadataHashes);

                SessionCache.xmlMetadataHashByMod = result.MetadataHashes;
                SessionCache.xmlContentHashByMod = new Dictionary<string, long>();

                if (FasterGameLoadingSettings.VerboseLogging)
                {
                    FGLLog.Message($"XML scan complete. Files: {result.FileCount}, elapsed: {result.ElapsedMilliseconds}ms, combined metadata hash: {combinedHash}, last saved hash: {SessionCache.xmlCombinedHashSinceLastSession}");
                }

                if (SessionCache.xmlCombinedHashSinceLastSession != combinedHash || metadataChanged)
                {
                    SessionCache.xmlPathsSinceLastSession.Clear();
                    SessionCache.xmlCombinedHashSinceLastSession = combinedHash;
                    needWriteSettings = true;
                }
            }
            finally
            {
                XmlNode_SelectSingleNode_Patch.isXmlScanComplete = true;
            }
        }

        private static void ScanDirectoryMetadata(string dirPath, ref long combinedHash, ref int xmlCount)
        {
            try
            {
                var dirInfo = new System.IO.DirectoryInfo(dirPath);
                if (!dirInfo.Exists) return;

                var files = dirInfo.EnumerateFiles("*.xml", SearchOption.AllDirectories)
                                   .OrderBy(f => f.FullName, StringComparer.Ordinal);
                foreach (var info in files)
                {
                    try
                    {
                        var file = info.FullName;
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
