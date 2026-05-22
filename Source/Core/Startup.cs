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
            SessionCache.modsInLastSession = ModsConfig.ActiveModsInLoadOrder.Select(x => x.packageIdLowerCase).ToList();
            //SessionCache.loadedTexturesSinceLastSession = ModContentLoaderTexture2D_LoadTexture_Patch.loadedTexturesThisSession;
            SessionCache.loadedTexturesSinceLastSession = new System.Collections.Generic.Dictionary<string, string>(ModContentLoaderTexture2D_LoadTexture_Patch.loadedTexturesThisSession);
            Log.Message("[FasterGameLoading] Texture downscale cache hits: " + ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadHits
                + ", failures: " + ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadFailures
                + ", configured entries: " + TextureResize.CacheCount);
            SessionCache.loadedTypesByFullNameSinceLastSession = GenTypes_GetTypeInAnyAssemblyInt_Patch.loadedTypesThisSession;
            SessionCache.xmlPathsSinceLastSession = new System.Collections.Generic.Dictionary<string, bool>(XmlNode_SelectSingleNode_Patch.xmlPathsThisSession);
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
