using System;
using System.Buffers.Binary;
using System.IO;

namespace FasterGameLoading;

/// <summary>
/// PNG 二進位檔案解析工具，免載入整張圖即可讀取解析度。
/// </summary>
public static class PngUtils
{
    /// <summary>
    /// 嘗試從 PNG 檔案標頭讀取尺寸（不載入完整圖片）。
    /// </summary>
    public static bool TryGetImageDimensions(string path, ref int width, ref int height)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            return TryReadPngDimensions(stream, ref width, ref height);
        }
        catch (IOException) { return false; }
        catch (System.UnauthorizedAccessException) { return false; }
    }

    // 從 PNG 檔案串流讀取 IHDR chunk 中的寬高。
    private static bool TryReadPngDimensions(Stream stream, ref int width, ref int height)
    {
        stream.Position = 0;
        var header = new byte[24];
        if (stream.Read(header, 0, header.Length) != header.Length) return false;
        if (header[0] is not 0x89 || header[1] is not 0x50 || header[2] is not 0x4E || header[3] is not 0x47) return false;

        int parsedWidth = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4));
        int parsedHeight = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4));
        // 拒絕超出合理範圍的尺寸，防止損毀標頭傳播異常巨大的數值
        const int MaxSafeDimension = 16384;
        if (parsedWidth <= 0 || parsedHeight <= 0 || parsedWidth > MaxSafeDimension || parsedHeight > MaxSafeDimension)
        {
            return false;
        }

        width = parsedWidth;
        height = parsedHeight;
        return true;
    }
}
