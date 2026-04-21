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
            // 允許 ReloadContentInt 重新執行
            ModContentPack_ReloadContentInt_Patch.loadedMods.Clear();

            // 允許 AssetBundle ReloadAll 重新執行
            ModAssetBundlesHandler_ReloadAll_Patch.reloadedHandlers.Clear();

            // 清除紋理快取（Texture2D 物件會被 Unity 銷毀）
            ModContentLoaderTexture2D_LoadTexture_Patch.savedTextures.Clear();
            ModContentLoaderTexture2D_LoadTexture_Patch.loadedTexturesThisSession.Clear();

            // 清除圖形快取（引用了已銷毀的 cachedGraphic）
            GraphicData_Init_Patch.savedGraphics.Clear();

            // 清除 XML path 快取（不同語言可能有不同的有效 XPath）
            XmlNode_SelectSingleNode_Patch.failedXMLPathsThisSession.Clear();
            XmlNode_SelectSingleNode_Patch.successfulXMLPathsThisSession.Clear();

            // 清除 mod 引用快取（ModContentPack 實例可能被重建）
            FasterGameLoadingSettings.modsByPackageIds.Clear();

            // 重置延遲載入狀態
            DelayedActions.AllGraphicLoaded = false;
            if (FasterGameLoadingMod.delayedActions != null)
            {
                FasterGameLoadingMod.delayedActions.graphicsToLoad.Clear();
                FasterGameLoadingMod.delayedActions.iconsToLoad.Clear();
                FasterGameLoadingMod.delayedActions.subSoundDefToResolve.Clear();
                FasterGameLoadingMod.delayedActions.ResetEarlyLoading();
            }

            // 重新啟用 SoundStarter patch（會在 DelayedActions.PerformActions 結束時 unpatch）
            try
            {
                FasterGameLoadingMod.harmony.PatchCategory("SoundStarter");
            }
            catch { /* 如果已 patched 就忽略 */ }
        }
    }
}
