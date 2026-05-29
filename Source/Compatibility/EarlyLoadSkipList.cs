using System;
using System.Collections.Generic;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 提早載入排除名單管理器 — 管理不支援提早載入（Early Loading）或重複載入會產生衝突的 Mod。
    /// </summary>
    public static class EarlyLoadSkipList
    {
        // ── 排除提早載入的 Mod 名單 ──
        private static readonly HashSet<string> skippedMods = new(StringComparer.OrdinalIgnoreCase)
        {
            // 目前沒有
        };

        /// <summary>
        /// 判斷指定的 Mod 是否應該跳過提早載入流程，改走原生同步載入。
        /// </summary>
        /// <param name="packageId">Mod 的 Package ID</param>
        /// <returns>若為 true 則應跳過</returns>
        public static bool ShouldSkip(string packageId)
        {
            if (string.IsNullOrEmpty(packageId)) return false;
            return skippedMods.Contains(packageId);
        }
    }
}
