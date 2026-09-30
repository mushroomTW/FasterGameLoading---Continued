using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// Image Opt 相容性檢查器。
    /// </summary>
    public static class ImageOptCompat
    {
        private static readonly byte[] ddsMagic = { (byte)'D', (byte)'D', (byte)'S', (byte)' ' };
        private static readonly byte[] zstdMagic = { 0x28, 0xB5, 0x2F, 0xFD };

        public static int CleanupInvalidDdsZstdCaches(IEnumerable<string> roots)
        {
            if (roots == null) return 0;

            var deleted = 0;
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

                foreach (var textureDir in TextureDirs(root))
                {
                    deleted += CleanupTextureDir(textureDir);
                }
            }
            return deleted;
        }

        /// <summary>
        /// 清理單一 Textures 目錄下所有「有原始圖檔存在、但快取魔術字不合法」的
        /// .dds／.dds.zstd 檔，回傳實際刪除數量。
        /// 目錄無法列舉或個別檔案刪不掉都只是略過 —— 清理屬盡力而為，不得影響啟動流程。
        /// </summary>
        private static int CleanupTextureDir(string textureDir)
        {
            string[] paths;
            try
            {
                // 精確模式各走一遍：*.dds* 會順帶掃到 .dds 開頭的無關檔，
                // 以兩個精確模式列舉再合併，副檔名判定交給後續的 HasValidCacheMagic。
                paths = Directory.EnumerateFiles(textureDir, "*.dds.zstd", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(textureDir, "*.dds", SearchOption.AllDirectories))
                    .ToArray();
            }
            catch
            {
                return 0;
            }

            var deleted = 0;
            foreach (var path in paths)
            {
                if (!HasSourceImage(path) || HasValidCacheMagic(path)) continue;

                try
                {
                    File.Delete(path);
                    deleted++;
                }
                catch
                {
                    // 檔案被佔用或權限不足時略過該檔即可。
                }
            }
            return deleted;
        }

        private static IEnumerable<string> TextureDirs(string root)
        {
            IEnumerable<string> childDirs;
            try
            {
                childDirs = Directory.EnumerateDirectories(root).ToArray();
            }
            catch
            {
                childDirs = Enumerable.Empty<string>();
            }

            foreach (var candidate in new[] { root }.Concat(childDirs))
            {
                // 父目錄恆不等於子目錄下的 Textures 全路徑，不需額外去重
                var textureDir = Path.Combine(candidate, FGLConsts.TexturesDirName);
                if (Directory.Exists(textureDir))
                {
                    yield return textureDir;
                }
            }
        }

        private static bool HasSourceImage(string cachePath)
        {
            var sourcePath = StripCacheExtension(cachePath);
            if (sourcePath == null) return false;

            return new[] { ".png", ".jpg", ".jpeg" }.Any(ext => File.Exists(sourcePath + ext));
        }

        /// <summary>
        /// 快取種類單次分派：長後綴必須優先判斷（.dds.zstd 結尾亦符合 .dds 結尾）。
        /// </summary>
        private enum DdsCacheKind
        {
            None,
            Dds,
            DdsZstd,
        }

        private static DdsCacheKind GetCacheKind(string path)
        {
            if (path.EndsWith(".dds.zstd", StringComparison.OrdinalIgnoreCase)) return DdsCacheKind.DdsZstd;
            if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) return DdsCacheKind.Dds;
            return DdsCacheKind.None;
        }

        private static string StripCacheExtension(string cachePath)
        {
            return GetCacheKind(cachePath) switch
            {
                DdsCacheKind.DdsZstd => cachePath.Substring(0, cachePath.Length - ".dds.zstd".Length),
                DdsCacheKind.Dds => cachePath.Substring(0, cachePath.Length - ".dds".Length),
                _ => null,
            };
        }

        private static bool HasValidCacheMagic(string path)
        {
            return GetCacheKind(path) switch
            {
                DdsCacheKind.DdsZstd => HasMagic(path, zstdMagic),
                DdsCacheKind.Dds => HasMagic(path, ddsMagic),
                _ => true,
            };
        }

        private static bool HasMagic(string path, byte[] magic)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var buffer = new byte[magic.Length];
                    int read = 0;
                    while (read < magic.Length)
                    {
                        int n = stream.Read(buffer, read, magic.Length - read);
                        if (n is 0) return false;
                        read += n;
                    }
                    return buffer.SequenceEqual(magic);
                }
            }
            catch
            {
                return true;
            }
        }
    }
}
