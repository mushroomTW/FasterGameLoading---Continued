using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 並行解析 XML，但保留 RimWorld 原版的檔案收集與覆蓋順序。
    /// </summary>
    [HarmonyPatch(typeof(DirectXmlLoader), "XmlAssetsInModFolder")]
    public static class DirectXmlLoader_XmlAssetsInModFolder_Patch
    {
        public static bool Prefix(ref LoadableXmlAsset[] __result, ModContentPack mod, string folderPath, List<string> foldersToLoadDebug)
        {
            if (mod == null || !FasterGameLoadingSettings.EnableMultiThreading || EarlyLoadSkipList.ShouldSkip(mod))
            {
                return true;
            }

            try
            {
                var files = XmlFilesInVanillaOrder(mod, folderPath, foldersToLoadDebug);
                if (files.Count == 0)
                {
                    __result = Array.Empty<LoadableXmlAsset>();
                    return false;
                }

                var assets = new LoadableXmlAsset[files.Count];
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8)
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

                var result = new List<LoadableXmlAsset>(assets.Length);
                for (int i = 0; i < assets.Length; i++)
                {
                    if (assets[i] != null)
                    {
                        result.Add(assets[i]);
                    }
                }

                __result = result.ToArray();
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
            var filesByRelativePath = new Dictionary<string, FileInfo>();

            for (int i = 0; i < folders.Count; i++)
            {
                var root = folders[i];
                var directory = new DirectoryInfo(Path.Combine(root, folderPath));
                if (!directory.Exists)
                {
                    continue;
                }

                foreach (var fileInfo in directory.EnumerateFiles("*.xml", SearchOption.AllDirectories))
                {
                    if (fileInfo.Name.StartsWith("._", StringComparison.Ordinal) || fileInfo.Name.StartsWith(".", StringComparison.Ordinal))
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
