using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 並行解析 XML，但保留 RimWorld 原版的檔案收集與覆蓋順序。
    /// </summary>
    [HarmonyPatch(typeof(DirectXmlLoader), "XmlAssetsInModFolder")]
    public static class DirectXmlLoader_XmlAssetsInModFolder_Patch
    {
        // MA0016: foldersToLoadDebug 是 Harmony 由原方法注入的參數，型別必須與
        // DirectXmlLoader.XmlAssetsInModFolder 的簽章逐字相符；改成唯讀介面會使
        // Harmony 無法比對而讓整個補丁失效。
#pragma warning disable MA0016
        public static bool Prefix(ref LoadableXmlAsset[] __result, ModContentPack mod, string folderPath, List<string> foldersToLoadDebug)
#pragma warning restore MA0016
        {
            if (mod == null || !FasterGameLoadingSettings.EnableMultiThreading || ProtectedMods.ShouldSkipEarlyLoad(mod))
            {
                return true;
            }

            try
            {
                var files = XmlFilesInVanillaOrder(mod, folderPath, foldersToLoadDebug);
                if (files.Count is 0)
                {
                    __result = Array.Empty<LoadableXmlAsset>();
                    return false;
                }

                var assets = new LoadableXmlAsset[files.Count];
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8),
                };

                Parallel.For(0, files.Count, options, i =>
                {
                    try
                    {
                        assets[i] = new LoadableXmlAsset(files[i], mod);
                    }
                    catch (Exception ex)
                    {
                        FGLLog.Error($"Failed to load XML asset in parallel for Mod {mod.Name} (File: {files[i]?.FullName}):", ex);
                    }
                });

                __result = assets.Where(static a => a != null).ToArray();
                return false;
            }
            catch (Exception ex)
            {
                FGLLog.Warning($"Parallel XML loading failed for Mod {mod?.Name ?? "Unknown"}, falling back to vanilla loader: {ex.Message}");
                return true;
            }
        }

        private static List<FileInfo> XmlFilesInVanillaOrder(ModContentPack mod, string folderPath, List<string> foldersToLoadDebug)
        {
            var folders = foldersToLoadDebug ?? mod.foldersToLoadDescendingOrder;
            var filesByRelativePath = new Dictionary<string, FileInfo>(StringComparer.Ordinal);

            foreach (var root in folders)
            {
                var directory = new DirectoryInfo(Path.Combine(root, folderPath));
                if (!directory.Exists)
                {
                    continue;
                }

                foreach (var fileInfo in directory.EnumerateFiles("*.xml", SearchOption.AllDirectories))
                {
                    // 以 '.' 開頭即涵蓋 macOS 的 "._" 資源分叉檔；net472 無 StartsWith(char) 多載，
                    // 直接比對首字元既正確又省去一次字串比對。
                    if (fileInfo.Name.Length > 0 && fileInfo.Name[0] == '.')
                    {
                        continue;
                    }

                    var relativePath = fileInfo.FullName.Substring(root.Length + 1);
                    if (!filesByRelativePath.ContainsKey(relativePath))
                    {
                        filesByRelativePath.Add(relativePath, fileInfo);
                    }
                }
            }

            return new List<FileInfo>(filesByRelativePath.Values);
        }
    }
}
