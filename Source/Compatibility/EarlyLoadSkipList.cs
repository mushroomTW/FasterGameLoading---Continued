using System;

namespace FasterGameLoading
{
    /// <summary>
    /// 提早載入排除名單管理器 — 管理不支援提早載入（Early Loading）或重複載入會產生衝突的 Mod。
    /// </summary>
    public static class EarlyLoadSkipList
    {
        /// <summary>
        /// 判斷指定的 Mod 是否應該跳過提早載入流程，改走原生同步載入。
        /// </summary>
        /// <param name="packageId">Mod 的 Package ID</param>
        /// <returns>若為 true 則應跳過</returns>
        public static bool ShouldSkip(string packageId)
        {
            if (string.IsNullOrEmpty(packageId)) return false;
            if (packageId.StartsWith("Ayameduki.", StringComparison.OrdinalIgnoreCase)) return true;
            if (packageId.StartsWith("WRK.", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
