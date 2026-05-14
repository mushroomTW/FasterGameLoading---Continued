using HarmonyLib;
using System.Linq;
using Verse;

namespace FasterGameLoading
{
    [HarmonyPatch(typeof(StaticConstructorOnStartupUtility), nameof(StaticConstructorOnStartupUtility.CallAll))]
    public static class Startup
    {

        public static void Postfix()
        {
            FasterGameLoadingSettings.modsInLastSession = ModsConfig.ActiveModsInLoadOrder.Select(x => x.packageIdLowerCase).ToList();
            //FasterGameLoadingSettings.loadedTexturesSinceLastSession = ModContentLoaderTexture2D_LoadTexture_Patch.loadedTexturesThisSession;
            FasterGameLoadingSettings.loadedTexturesSinceLastSession = new System.Collections.Generic.Dictionary<string, string>(ModContentLoaderTexture2D_LoadTexture_Patch.loadedTexturesThisSession);
            Log.Message("[FasterGameLoading] Texture downscale cache hits: " + ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadHits
                + ", failures: " + ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadFailures
                + ", configured entries: " + TextureResize.CacheCount);
            FasterGameLoadingSettings.loadedTypesByFullNameSinceLastSession = GenTypes_GetTypeInAnyAssemblyInt_Patch.loadedTypesThisSession;
            FasterGameLoadingSettings.successfulXMLPathsSinceLastSession = XmlNode_SelectSingleNode_Patch.successfulXMLPathsThisSession;
            FasterGameLoadingSettings.failedXMLPathsSinceLastSession = XmlNode_SelectSingleNode_Patch.failedXMLPathsThisSession;
TranslationInjector.InjectTranslations();
            // 將設定寫入排到載入完成後，避免同步阻塞啟動過程
            LongEventHandler.toExecuteWhenFinished.Add(delegate
            {
                LoadedModManager.GetMod<FasterGameLoadingMod>().WriteSettings();
            });
            LongEventHandler.toExecuteWhenFinished.Add(delegate
            {
                FasterGameLoadingMod.delayedActions.StartCoroutine(FasterGameLoadingMod.delayedActions.PerformActions());
            });
        }
    }
}
