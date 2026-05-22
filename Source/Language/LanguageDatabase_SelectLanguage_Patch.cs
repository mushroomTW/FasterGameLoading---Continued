using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    [HarmonyPatch(typeof(LanguageDatabase), nameof(LanguageDatabase.SelectLanguage))]
    public static class LanguageDatabase_SelectLanguage_Patch
    {
        /// <summary>
        /// 語言切換會觸發 ClearAllPlayData + LoadAllPlayData，
        /// Unity 的 Texture2D 物件會被銷毀，但我們的快取仍持有 C# 引用（已成 null）。
        /// 必須清除所有快取，讓重載流程完整執行。
        ///
        /// 各目錄的快取清理邏輯已分散註冊至 CacheResetter，
        /// 新增快取時只需在該類別加一行 CacheResetter.Register(...) 即可，
        /// 不需要再修改這裡。
        /// </summary>
        public static void Prefix()
        {
            CacheResetter.ResetAll();
        }
    }
}
