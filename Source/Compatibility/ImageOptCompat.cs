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

                foreach (var textureDir in TextureDirs(root))
                {
                    IEnumerable<string> paths;
                    try
                    {
                        paths = Directory.EnumerateFiles(textureDir, "*.dds.zstd", SearchOption.AllDirectories).ToArray();
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var path in paths)
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
            }
            return deleted;
        }

        private static IEnumerable<string> TextureDirs(string root)
        {
            var seen = new HashSet<string>();
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
                var textureDir = Path.Combine(candidate, FGLConsts.TexturesDirName);
                if (Directory.Exists(textureDir) && seen.Add(textureDir))
                {
                    yield return textureDir;
                }
            }
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
