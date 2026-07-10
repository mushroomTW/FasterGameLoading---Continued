using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 LanguageDatabase.SelectLanguage，在切換語言時自動重置所有快取。
    /// </summary>
    [HarmonyPatch(typeof(LanguageDatabase), nameof(LanguageDatabase.SelectLanguage))]
    public static class LanguageDatabase_SelectLanguage_Patch
    {
        /// <summary>
        /// 於選擇語言的前置處理中，執行所有已註冊的 CacheResetter 清理動作。
        /// </summary>
        /// <remarks>
        /// 語言切換會觸發 ClearAllPlayData + LoadAllPlayData，
        /// Unity 的 Texture2D 物件會被銷毀，但我們的快取仍持有 C# 引用（已成 null）。
        /// 必須清除所有快取，讓重載流程完整執行。
        ///
        /// 各目錄的快取清理邏輯已分散註冊至 CacheResetter，
        /// 新增快取時只需在該類別加一行 CacheResetter.Register(...) 即可，
        /// 不需要再修改這裡。
        /// </remarks>
        public static void Prefix()
        {
            CacheResetter.ResetAll();
        }
    }
}
