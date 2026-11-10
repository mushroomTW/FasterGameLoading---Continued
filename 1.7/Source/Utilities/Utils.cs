namespace FasterGameLoading;

// 通用擴充方法與輔助工具。
public static class Utils
{
    // 將路徑中的反斜線統一替換為正斜線，確保跨平台相容性。
    public static string NormalizePath(this string path)
    {
        return path?.Replace('\\', '/');
    }
}
