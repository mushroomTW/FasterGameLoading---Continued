using System.Collections.Generic;
using System.IO;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// Image Opt 相容性檢查器。
    /// </summary>
    public static class ImageOptCompat
    {
        private static readonly byte[] zstdMagic = { 0x28, 0xB5, 0x2F, 0xFD };
        private static bool? isActive;

        static ImageOptCompat()
        {
            CacheResetter.Register(() => isActive = null);
        }

        public static bool IsActive
        {
            get
            {
                if (!isActive.HasValue)
                {
                    isActive = IsModActive("dev.soeur.imageopt");
                }
                return isActive.Value;
            }
        }
        private static bool IsModActive(string packageId)
        {
            try
            {
                return ModsConfig.IsActive(packageId);
            }
            catch
            {
                return false;
            }
        }

        public static int CleanupInvalidDdsZstdCaches(IEnumerable<string> roots)
        {
            if (roots == null) return 0;

            var deleted = 0;
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

                foreach (var path in Directory.EnumerateFiles(root, "*.dds.zstd", SearchOption.AllDirectories))
                {
                    if (!HasSourceImage(path) || HasZstdMagic(path)) continue;

                    try
                    {
                        File.Delete(path);
                        deleted++;
                    }
                    catch
                    {
                    }
                }
            }
            return deleted;
        }

        private static bool HasSourceImage(string ddsZstdPath)
        {
            var withoutDdsZstd = ddsZstdPath.Substring(0, ddsZstdPath.Length - ".dds.zstd".Length);
            return File.Exists(withoutDdsZstd + ".png")
                || File.Exists(withoutDdsZstd + ".jpg")
                || File.Exists(withoutDdsZstd + ".jpeg");
        }

        private static bool HasZstdMagic(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    if (stream.Length < zstdMagic.Length) return false;

                    for (int i = 0; i < zstdMagic.Length; i++)
                    {
                        if (stream.ReadByte() != zstdMagic[i]) return false;
                    }
                    return true;
                }
            }
            catch
            {
                return true;
            }
        }
    }
}
