using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 PlayDataLoader.ClearAllPlayData，讓不經語言選擇的清除流程也重置 FGL 狀態。
    /// 原版只有兩個呼叫端：切換語言，以及 LoadAllPlayData 載入失敗後的自動恢復（清除後以重設的 mod 清單重載）。
    /// 後者原本不會觸發 <see cref="LifecyclePhase.LanguageReloading"/>，舊的延遲動作與內容載入紀錄會殘留到重載後。
    /// </summary>
    [HarmonyPatch(typeof(PlayDataLoader), nameof(PlayDataLoader.ClearAllPlayData))]
    public static class PlayDataLoader_ClearAllPlayData_Patch
    {
        private static volatile bool resetBySelectLanguage;

        /// <summary>LanguageDatabase.SelectLanguage 已在主執行緒重置過；接下來的這次清除不再重複。</summary>
        internal static void MarkResetBySelectLanguage() => resetBySelectLanguage = true;

        /// <summary>這次語言切換沒有執行原版（不會清除），撤銷 <see cref="MarkResetBySelectLanguage"/>。</summary>
        internal static void ClearResetBySelectLanguage() => resetBySelectLanguage = false;

        /// <summary>
        /// 錯誤恢復發生在載入事件緒，重置交由主執行緒執行並等待完成（見 <see cref="SessionLifecycle.RaiseOnMainThread"/>）。
        /// </summary>
        public static void Prefix()
        {
            if (resetBySelectLanguage)
            {
                resetBySelectLanguage = false;
                return;
            }
            if (!SessionLifecycle.RaiseOnMainThread(LifecyclePhase.LanguageReloading))
            {
                FGLLog.Warning("Timed out waiting for the main thread to reset FGL state before clearing play data; stale deferred work may remain.");
            }
        }
    }
}
