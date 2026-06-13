using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                            // 深入讀取 XML 檔案內容並計算實質的 xxHash64 contentHash
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
                    var hasher = new XxHash64();
                    // 64 KB 緩衝區串流讀取，避免將大檔一次性載入記憶體
                    var buffer = new byte[64 * 1024];
                    int read;
                    while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        hasher.Update(buffer, read);
                    }
                    // 以位元層級將 ulong 結果轉為 long（不改變位元樣式，僅作為快取鍵使用）
                    return unchecked((long)hasher.Digest());
                }
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 串流式 xxHash64（非加密快速雜湊，seed = 0）的最小自包含實作，零外部相依。
        /// 僅供快取變更偵測使用，不涉及任何安全性需求；相較 MD5 在大檔案上有數倍吞吐量優勢。
        /// 依官方規格實作，可逐塊餵入任意大小的 buffer，內部自動處理 32-byte stripe 對齊與殘餘位元組。
        /// </summary>
        private sealed class XxHash64
        {
            private const ulong Prime1 = 11400714785074694791UL;
            private const ulong Prime2 = 14029467366897019727UL;
            private const ulong Prime3 = 1609587929392839161UL;
            private const ulong Prime4 = 9650029242287828579UL;
            private const ulong Prime5 = 2870177450012600261UL;

            // seed = 0：各 accumulator 初始值依官方規格推導
            private ulong v1 = unchecked(Prime1 + Prime2);
            private ulong v2 = Prime2;
            private ulong v3 = 0;
            private ulong v4 = unchecked(0UL - Prime1);
            private ulong totalLen = 0;
            private readonly byte[] mem = new byte[32];
            private int memSize = 0;

            public void Update(byte[] input, int len)
            {
                unchecked
                {
                    totalLen += (ulong)len;
                    int offset = 0;

                    // 累積不足一個 32-byte stripe，先暫存於 mem 等待後續資料
                    if (memSize + len < 32)
                    {
                        Array.Copy(input, 0, mem, memSize, len);
                        memSize += len;
                        return;
                    }

                    // 先用本次資料把上次殘留的 mem 補滿到 32 bytes 並消化掉
                    if (memSize > 0)
                    {
                        int fill = 32 - memSize;
                        Array.Copy(input, 0, mem, memSize, fill);
                        v1 = Round(v1, ReadU64(mem, 0));
                        v2 = Round(v2, ReadU64(mem, 8));
                        v3 = Round(v3, ReadU64(mem, 16));
                        v4 = Round(v4, ReadU64(mem, 24));
                        offset += fill;
                        memSize = 0;
                    }

                    int limit = len - 32;
                    while (offset <= limit)
                    {
                        v1 = Round(v1, ReadU64(input, offset)); offset += 8;
                        v2 = Round(v2, ReadU64(input, offset)); offset += 8;
                        v3 = Round(v3, ReadU64(input, offset)); offset += 8;
                        v4 = Round(v4, ReadU64(input, offset)); offset += 8;
                    }

                    int remaining = len - offset;
                    if (remaining > 0)
                    {
                        Array.Copy(input, offset, mem, 0, remaining);
                        memSize = remaining;
                    }
                }
            }

            public ulong Digest()
            {
                unchecked
                {
                    ulong h64;
                    if (totalLen >= 32)
                    {
                        h64 = Rotl(v1, 1) + Rotl(v2, 7) + Rotl(v3, 12) + Rotl(v4, 18);
                        h64 = MergeRound(h64, v1);
                        h64 = MergeRound(h64, v2);
                        h64 = MergeRound(h64, v3);
                        h64 = MergeRound(h64, v4);
                    }
                    else
                    {
                        h64 = v3 + Prime5; // v3 == seed == 0
                    }

                    h64 += totalLen;

                    int offset = 0;
                    int remaining = memSize;
                    while (remaining >= 8)
                    {
                        h64 ^= Round(0, ReadU64(mem, offset));
                        h64 = Rotl(h64, 27) * Prime1 + Prime4;
                        offset += 8; remaining -= 8;
                    }
                    if (remaining >= 4)
                    {
                        h64 ^= (ulong)ReadU32(mem, offset) * Prime1;
                        h64 = Rotl(h64, 23) * Prime2 + Prime3;
                        offset += 4; remaining -= 4;
                    }
                    while (remaining >= 1)
                    {
                        h64 ^= mem[offset] * Prime5;
                        h64 = Rotl(h64, 11) * Prime1;
                        offset += 1; remaining -= 1;
                    }

                    h64 ^= h64 >> 33;
                    h64 *= Prime2;
                    h64 ^= h64 >> 29;
                    h64 *= Prime3;
                    h64 ^= h64 >> 32;
                    return h64;
                }
            }

            private static ulong Round(ulong acc, ulong input)
            {
                unchecked
                {
                    acc += input * Prime2;
                    acc = Rotl(acc, 31);
                    acc *= Prime1;
                    return acc;
                }
            }

            private static ulong MergeRound(ulong acc, ulong val)
            {
                unchecked
                {
                    val = Round(0, val);
                    acc ^= val;
                    acc = acc * Prime1 + Prime4;
                    return acc;
                }
            }

            private static ulong Rotl(ulong x, int r) => (x << r) | (x >> (64 - r));

            private static ulong ReadU64(byte[] data, int offset) =>
                (ulong)data[offset]
                | ((ulong)data[offset + 1] << 8)
                | ((ulong)data[offset + 2] << 16)
                | ((ulong)data[offset + 3] << 24)
                | ((ulong)data[offset + 4] << 32)
                | ((ulong)data[offset + 5] << 40)
                | ((ulong)data[offset + 6] << 48)
                | ((ulong)data[offset + 7] << 56);

            private static uint ReadU32(byte[] data, int offset) =>
                (uint)data[offset]
                | ((uint)data[offset + 1] << 8)
                | ((uint)data[offset + 2] << 16)
                | ((uint)data[offset + 3] << 24);
        }
    }
}
