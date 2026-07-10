using System.IO;

namespace FasterGameLoading
{
    /// <summary>
    /// PNG 二進位檔案解析工具，免載入整張圖即可讀取解析度。
    /// </summary>
    public static class PngUtils
    {
        /// <summary>嘗試從 PNG 檔案標頭讀取尺寸（不載入完整圖片）。</summary>
        public static bool TryGetImageDimensions(string path, ref int width, ref int height)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using (var stream = File.OpenRead(path))
                {
                    return TryReadPngDimensions(stream, ref width, ref height);
                }
            }
            catch (IOException) { return false; }
            catch (System.UnauthorizedAccessException) { return false; }
        }

        /// <summary>從 PNG 檔案串流讀取 IHDR chunk 中的寬高。</summary>
        private static bool TryReadPngDimensions(Stream stream, ref int width, ref int height)
        {
            stream.Position = 0;
            var header = new byte[24];
            if (stream.Read(header, 0, header.Length) != header.Length) return false;
            if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47) return false;

            width = ReadBigEndianInt32(header, 16);
            height = ReadBigEndianInt32(header, 20);
            // 拒絕超出合理範圍的尺寸，防止損毀標頭傳播異常巨大的數值
            const int MaxSafeDimension = 16384;
            return width > 0 && height > 0 && width <= MaxSafeDimension && height <= MaxSafeDimension;
        }

        private static int ReadBigEndianInt32(byte[] bytes, int offset)
        {
            return (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
        }
    }
}
