using System;
using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 延遲圖形載入的靜態圖集插入閘門：在指定的圖形動作期間讓
    /// <c>GlobalTextureAtlasManager.TryInsertStatic</c> 直接回傳 false（不排入圖集）。
    ///
    /// 用途只有一個：當某個 Def 的 <see cref="GraphicData"/> 在排入延遲佇列後被其他 Mod 改寫
    /// （見 <see cref="GraphicSnapshot"/>），原版插入圖集的其實是改寫前的貼圖，
    /// 改寫後的圖形從來沒進過靜態圖集。FGL 若照常執行原版回呼，改寫後的貼圖會被插進圖集，
    /// 造成同一張貼圖配多個遮罩的衝突與錯誤的遮罩套用。
    /// 此時仍要執行原版回呼以建立 <c>Def.graphic</c>，只把插入圖集這個副作用關掉。
    ///
    /// 這個補丁只在延遲視覺管線啟用時套用，與是否開啟自適應烘焙無關；
    /// 沒有進入抑制範圍時，前綴只做一次欄位讀取。
    /// </summary>
    [HarmonyPatch(typeof(GlobalTextureAtlasManager), "TryInsertStatic")]
    public static class DeferredGraphicAtlasSkip
    {
        public static bool Prepare() => DelayedActions.DeferredVisualsEnabled;

        // 抑制層數；只在跳過插入時才會大於 0。用 ThreadStatic 是因為插入一律發生在
        // 執行圖形動作的同一個執行緒上，其他執行緒（例如背景貼圖載入）不受影響。
        [ThreadStatic]
        private static int suppressionDepth;

        // 回傳 false 即跳過原方法：不寫入圖集，也不檢查遮罩衝突。
        public static bool Prefix() => suppressionDepth is 0;

        /// <summary>
        /// 在這個範圍內執行 <paramref name="action"/>：原版回呼照常建立圖形，但不會插入靜態圖集。
        /// 以 try/finally 確保動作拋出例外時也會還原。
        /// </summary>
        internal static void RunWithoutStaticAtlas(Action action)
        {
            suppressionDepth++;
            try
            {
                action();
            }
            finally
            {
                suppressionDepth--;
            }
        }
    }
}
