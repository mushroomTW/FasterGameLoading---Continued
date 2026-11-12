namespace FasterGameLoading;

/// <summary>
/// 通用擴充方法與輔助工具。
/// </summary>
public static class Utils
{
    /// <summary>
    /// 將路徑中的反斜線統一替換為正斜線，確保跨平台相容性。
    /// </summary>
    public static string NormalizePath(this string path)
    {
        return path?.Replace('\\', '/');
    }
}
