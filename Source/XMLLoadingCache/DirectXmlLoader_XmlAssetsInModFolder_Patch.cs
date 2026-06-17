using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 DirectXmlLoader.XmlAssetsInModFolder 以實現並行 XML 載入與解析。
    /// 使用 Parallel.For 多核心同時讀取磁碟檔案並解析為 XmlDocument。
    /// </summary>
    [HarmonyPatch(typeof(DirectXmlLoader), "XmlAssetsInModFolder")]
    public static class DirectXmlLoader_XmlAssetsInModFolder_Patch
    {
        public static bool Prefix(ref LoadableXmlAsset[] __result, ModContentPack mod, string folderPath, List<string> foldersToLoadDebug)
        {
            if (mod == null)
            {
                return true; // 參數無效時直接放行，讓 vanilla 處理（或拋出對應異常）
            }

            // 若設定中停用了多執行緒預載入，則直接回歸原生單執行緒載入
            if (!FasterGameLoadingSettings.EnableMultiThreading)
            {
                return true;
            }

            // 如果該 Mod 屬於跳過清單，則放行回原生的單執行緒 XML 加載方法，避免多執行緒加載造成的 Patch 覆蓋與執行緒安全衝突
            if (EarlyLoadSkipList.ShouldSkip(mod.PackageIdPlayerFacing))
            {
                return true;
            }

            try
            {
                // 1. 獲取該 Mod 該目錄下的所有 XML 虛擬檔案（List<Tuple<string, FileInfo>>）
                // 呼叫 RimWorld 官方最底層的虛擬目錄檔案列舉方法，保證 100% 的相容性與檔案完整性
                var files = ModContentPack.GetAllFilesForModPreserveOrder(
                    mod,
                    folderPath,
                    f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase),
                    foldersToLoadDebug
                );

                if (files != null)
                {
                    for (int i = files.Count - 1; i >= 0; i--)
                    {
                        var fileInfo = files[i]?.Item2;
                        if (fileInfo == null || !IsVanillaVisibleXmlFile(fileInfo))
                        {
                            files.RemoveAt(i);
                        }
                    }
                }

                if (files == null || files.Count == 0)
                {
                    __result = Array.Empty<LoadableXmlAsset>();
                    return false; // 跳過原方法
                }

                var assets = new LoadableXmlAsset[files.Count];

                // 2. 使用 Parallel.For 進行多執行緒並行讀取與 XML DOM 解析（限制最大並行度防止 I/O 飽和）
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8)
                };
                Parallel.For(0, files.Count, options, i =>
                {
                    try
                    {
                        var tuple = files[i];
                        if (tuple != null)
                        {
                            var fileInfo = tuple.Item2;
                            if (fileInfo != null && fileInfo.Exists)
                            {
                                // 調用官方構造函數，這會讀取磁碟內容並實例化 XmlDocument 進行 Load。
                                // 因為每個 LoadableXmlAsset 都是全新且獨立的，此過程在 C# 中是 100% 執行緒安全的。
                                assets[i] = new LoadableXmlAsset(fileInfo, mod);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // 一律輸出錯誤日誌，以利排查多執行緒載入問題
                        FGLLog.Error($"Failed to load XML asset in parallel for Mod {mod.Name} (File: {files[i]?.Item2?.FullName}):", ex);
                    }
                });

                // 3. 收集並過濾成功的 XML assets
                var resultList = new List<LoadableXmlAsset>(assets.Length);
                for (int i = 0; i < assets.Length; i++)
                {
                    if (assets[i] != null)
                    {
                        resultList.Add(assets[i]);
                    }
                }

                __result = resultList.ToArray();
                return false; // 跳過 RimWorld 原生單線程的加載方法
            }
            catch (Exception ex)
            {
                FGLLog.Warning($"Parallel XML loading failed for Mod {mod?.Name ?? "Unknown"}, falling back to vanilla loader: {ex.Message}");
                return true; // 萬一出錯，安全 fallback 回原生的單執行緒加載
            }
        }

        private static bool IsVanillaVisibleXmlFile(FileInfo fileInfo)
        {
            var name = fileInfo.Name;
            return !name.StartsWith("._", StringComparison.Ordinal)
                && !name.StartsWith(".", StringComparison.Ordinal);
        }

    }
}
