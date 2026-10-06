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
        /// 於選擇語言的前置處理中，觸發 SessionLifecycle 的 LanguageReloading 階段。
        /// </summary>
        /// <remarks>
        /// 語言切換會觸發 ClearAllPlayData + LoadAllPlayData，
        /// Unity 的 Texture2D 物件會被銷毀，但我們的快取仍持有 C# 引用（已成 null）。
        /// 必須清除所有快取，讓重載流程完整執行。
        ///
        /// 各目錄的快取清理邏輯分散在各自的類別登記，
        /// 新增快取時只需在該類別加一行 SessionLifecycle.On(LifecyclePhase.LanguageReloading, ...) 即可，
        /// 不需要再修改這裡。
        /// </remarks>
        public static void Prefix()
        {
            SessionLifecycle.Raise(LifecyclePhase.LanguageReloading);
            PlayDataLoader_ClearAllPlayData_Patch.MarkResetBySelectLanguage();
        }

        /// <summary>
        /// 其他 mod 的前置處理取消原版（例如 HugsLib 改為重新啟動）時，不會排入 ClearAllPlayData，
        /// 撤銷標記，避免之後無關的清除（例如載入失敗的自動恢復）被誤判為這次語言切換而略過重置。
        /// </summary>
        public static void Postfix(bool __runOriginal)
        {
            if (!__runOriginal)
            {
                PlayDataLoader_ClearAllPlayData_Patch.ClearResetBySelectLanguage();
            }
        }
    }
}
