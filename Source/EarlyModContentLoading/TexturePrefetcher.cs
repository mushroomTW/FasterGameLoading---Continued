using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimWorld.IO;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 在背景依 mod 載入順序把原始貼圖讀進記憶體，主執行緒載入貼圖時直接取用，不必等磁碟 I/O。
    /// 主執行緒的貼圖載入仍是逐張解碼與壓縮；這裡只把讀檔移出主執行緒，暫存總量受
    /// <see cref="FGLConsts.TexturePrefetchBudgetBytes"/> 限制。啟動完成（CallAll）時停止並釋放暫存。
    /// 只在開啟多執行緒、且由 FGL 負責貼圖載入（未啟用 Image Opt、Graphics Settings+）時執行。
    /// </summary>
    internal static class TexturePrefetcher
    {
        private static volatile TexturePrefetchBuffer buffer;

        static TexturePrefetcher()
        {
            SessionLifecycle.On(LifecyclePhase.StartupCompleted, Stop);
        }

        internal static bool ShouldRun => FasterGameLoadingSettings.EnableMultiThreading && TextureOwnership.FglOwnsTextureLoading;

        /// <summary>由 Mod 建構子呼叫：此時已知道所有執行中的 mod，貼圖內容尚未開始載入。</summary>
        internal static void Start()
        {
            if (!ShouldRun || buffer != null) return;

            var mods = ModsInContentLoadOrder();
            // 只取對照表的快照、不檢查快取檔：新鮮度檢查有磁碟 I/O，主執行緒載入時本來就會再做一次。
            var downscaledOriginals = new HashSet<string>(
                FasterGameLoadingMod.Instance?.CacheManager?.GetResizedTextureCacheCopy().Keys ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);
            var started = new TexturePrefetchBuffer(FGLConsts.TexturePrefetchBudgetBytes);
            buffer = started;
            Task.Factory.StartNew(() => Run(started, mods, downscaledOriginals), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>取出預讀好的檔案內容；沒有預讀或已停止時回傳 false。</summary>
        internal static bool TryTake(string path, out byte[] data)
        {
            var current = buffer;
            if (current == null)
            {
                data = null;
                return false;
            }
            return current.TryTake(path, out data);
        }

        internal static bool IsRunning => buffer != null;

        /// <summary>最近一次停止的預讀統計；尚未執行或仍在執行時為 null。只保留數字，暫存區本身停止後即可回收。</summary>
        internal static TexturePrefetchStats LastRun { get; private set; }

        /// <summary>
        /// 貼圖內容的載入順序：開啟提早載入時，FGL 先依序載入不在略過名單的 mod，略過名單的 mod 最後由原版載入；
        /// 否則由原版依 RunningMods 順序載入。
        /// </summary>
        internal static List<ModContentPack> ModsInContentLoadOrder()
        {
            var mods = LoadedModManager.RunningMods.ToList();
            if (!FasterGameLoadingSettings.earlyModContentLoading) return mods;
            return mods.Where(static m => !ProtectedMods.ShouldSkipEarlyLoad(m))
                .Concat(mods.Where(static m => ProtectedMods.ShouldSkipEarlyLoad(m)))
                .ToList();
        }

        /// <summary>
        /// 一個 mod 會經 <see cref="FilesystemFile.ReadAllBytes"/> 讀取的貼圖檔，順序同原版 ModContentLoader.LoadAllForMod：
        /// .dds 由 ModDdsLoader 以其他方式讀取，有同名 .dds 的圖檔則被原版略過，兩者都不預讀。
        /// </summary>
        internal static IEnumerable<FileInfo> TextureFilesInLoadOrder(ModContentPack mod)
        {
            var files = ModContentPack.GetAllFilesForMod(mod, GenFilePaths.ContentPath<Texture2D>(), ModContentLoader<Texture2D>.IsAcceptableExtension);
            var ddsFiles = new HashSet<string>(
                files.Keys.Select(static k => k.ToLowerInvariant()).Where(static k => k.EndsWith(".dds", StringComparison.Ordinal)),
                StringComparer.Ordinal);
            foreach (var pair in files)
            {
                var key = pair.Key.ToLowerInvariant();
                if (key.EndsWith(".dds", StringComparison.Ordinal)) continue;
                if (key.Length > 4 && ddsFiles.Contains(key.Substring(0, key.Length - 4) + ".dds")) continue;
                yield return pair.Value;
            }
        }

        private static void Run(TexturePrefetchBuffer prefetch, List<ModContentPack> mods, HashSet<string> downscaledOriginals)
        {
            try
            {
                for (int group = 0; group < mods.Count; group++)
                {
                    foreach (var file in TextureFilesInLoadOrder(mods[group]))
                    {
                        if (prefetch.IsStopped) return;
                        Prefetch(prefetch, group, file, downscaledOriginals);
                    }
                }
            }
            catch (Exception ex)
            {
                // 背景預讀失敗只會讓主執行緒改回自行讀檔。
                FGLLog.Warning("Texture prefetch stopped early:", ex);
            }
        }

        /// <param name="group">檔案所屬 mod 在載入順序中的位置；同一個 mod 的貼圖一定依序載入。</param>
        private static void Prefetch(TexturePrefetchBuffer prefetch, int group, FileInfo file, HashSet<string> downscaledOriginals)
        {
            var path = file.FullName;
            // 有降質快取的貼圖改讀快取檔（由 StartPreloadCachedTextures 預讀），原圖不會被讀取。
            // 快取檔若已失效，主執行緒會改讀原圖，只是這張沒有預讀。
            if (downscaledOriginals.Contains(path)) return;

            long length;
            try
            {
                length = file.Length;
            }
            catch (IOException)
            {
                return;
            }
            if (length > prefetch.BudgetBytes) return;

            if (!prefetch.TryRegister(path, group, out var sequence) || !prefetch.WaitForRoom(group, sequence, length)) return;

            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return;
            }
            prefetch.Store(group, sequence, path, data);
        }

        private static void Stop()
        {
            var stopped = buffer;
            if (stopped == null) return;
            buffer = null;
            stopped.Stop();
            LastRun = new TexturePrefetchStats(stopped.Hits, stopped.HitBytes, stopped.Misses, stopped.Evicted);
            FGLLog.Message(string.Format(CultureInfo.InvariantCulture,
                "Texture prefetch: {0} files ({1:F1} MiB) served from memory, {2} read on demand, {3} prefetched but skipped.",
                stopped.Hits, stopped.HitBytes / 1024d / 1024d, stopped.Misses, stopped.Evicted));
        }
    }

    /// <summary>一次背景預讀結束時的統計。</summary>
    internal sealed class TexturePrefetchStats
    {
        internal TexturePrefetchStats(int hits, long hitBytes, int misses, int evicted)
        {
            Hits = hits;
            HitBytes = hitBytes;
            Misses = misses;
            Evicted = evicted;
        }

        internal int Hits { get; }
        internal long HitBytes { get; }
        internal int Misses { get; }
        internal int Evicted { get; }
    }

    /// <summary>主執行緒載入貼圖時，原版 ModContentLoader 以這個方法讀檔；有預讀好的內容就直接回傳。</summary>
    [HarmonyPatch(typeof(FilesystemFile), nameof(FilesystemFile.ReadAllBytes))]
    public static class FilesystemFile_ReadAllBytes_Patch
    {
        public static bool Prepare() => TexturePrefetcher.ShouldRun;

        public static bool Prefix(FilesystemFile __instance, ref byte[] __result)
        {
            if (!TexturePrefetcher.IsRunning) return true;
            return !TexturePrefetcher.TryTake(__instance.FullPath, out __result);
        }
    }
}
