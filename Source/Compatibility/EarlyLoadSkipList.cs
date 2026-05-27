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
            "automatic.bionicicons", // Bionic Icons — 避免因提早載入而產生重複載入衝突
            "zorba.prepatcher",      // Prepatcher — 避免 ASM 重寫與提早載入順序衝突
            "krafs.xmlextensions",   // XML Extensions — 避免其動態 XML 解析受干擾
            "unlimitedhugs.hugslib"  // HugsLib — 底層庫，改由 vanilla 主執行緒安全初始化
        };

        private static bool? isBionicIconsActive;

        /// <summary>
        /// 取得 Bionic Icons Mod 是否為啟用狀態（快取判定結果以維護啟動時期的效能）。
        /// </summary>
        public static bool IsBionicIconsActive
        {
            get
            {
                if (!isBionicIconsActive.HasValue)
                {
                    try
                    {
                        isBionicIconsActive = ModsConfig.IsActive("automatic.bionicicons");
                    }
                    catch
                    {
                        isBionicIconsActive = false;
                    }
                }
                return isBionicIconsActive.Value;
            }
        }

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
