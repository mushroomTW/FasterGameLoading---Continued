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
        /// </summary>
        public static void Prefix()
        {
            if (FasterGameLoadingMod.delayedActions != null)
            {
                FasterGameLoadingMod.delayedActions.BeginLanguageReload();
            }
            else
            {
                DelayedActions.LanguageReloadInProgress = true;
            }

            // 不清除 ModContentPack 和 AssetBundle 的快取，因為它們不會被 Unity 銷毀
            // 這樣可以避免切換語言時重複載入導致的 OOM 和 AssetBundle 衝突

            // 清除圖形快取（引用了已銷毀的 cachedGraphic）
            lock (GraphicData_Init_Patch.syncLock)
            {
                GraphicData_Init_Patch.savedGraphics.Clear();
            }

            // 清除 XML path 快取（不同語言可能有不同的有效 XPath）
            lock (XmlNode_SelectSingleNode_Patch.syncLock)
            {
                XmlNode_SelectSingleNode_Patch.failedXMLPathsThisSession.Clear();
                XmlNode_SelectSingleNode_Patch.successfulXMLPathsThisSession.Clear();
            }

            // 清除 mod 引用快取（ModContentPack 實例可能被重建）
            FasterGameLoadingSettings.modsByPackageIds.Clear();
        }

        public static void Postfix()
        {
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                if (FasterGameLoadingMod.delayedActions != null)
                {
                    FasterGameLoadingMod.delayedActions.EndLanguageReload();
                }
                else
                {
                    DelayedActions.LanguageReloadInProgress = false;
                }
            });
        }
    }
}
