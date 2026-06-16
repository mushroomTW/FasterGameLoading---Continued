using System;
using System.Collections.Generic;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 集中管理所有快取重置邏輯。
    /// 各目錄在自己的類別中註冊清理委派，語言切換時由 ResetAll() 統一觸發，
    /// 不再集中寫死在 LanguageDatabase_SelectLanguage_Patch 中。
    /// </summary>
    public static class CacheResetter
    {
        private static readonly List<Action> resetActions = new List<Action>();

        /// <summary>
        /// 註冊一組清理動作（通常在靜態建構子或 Mod 建構子中呼叫）。
        /// </summary>
        public static void Register(Action resetAction)
        {
            resetActions.Add(resetAction);
        }

        /// <summary>
        /// 依註冊順序執行所有清理動作，單一失敗不影響後續。
        /// </summary>
        public static void ResetAll()
        {
            foreach (var action in resetActions)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    FGLLog.Error("CacheResetter error during cleanup:", ex);
                }
            }
        }
    }
}
