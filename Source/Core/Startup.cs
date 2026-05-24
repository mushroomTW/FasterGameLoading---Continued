using HarmonyLib;
using System.Linq;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 在所有 StaticConstructorOnStartupUtility 完成後執行收尾工作：
    /// 儲存跨 session 快取資料、注入翻譯、排程延遲動作。
    /// </summary>
    [HarmonyPatch(typeof(StaticConstructorOnStartupUtility), nameof(StaticConstructorOnStartupUtility.CallAll))]
    public static class Startup
    {
        public static void Postfix()
        {
            // Save current session data for cross-session caching
            SessionCache.modsInLastSession = ModsConfig.ActiveModsInLoadOrder.Select(x => x.packageIdLowerCase).ToList();
            SessionCache.loadedTexturesSinceLastSession = new System.Collections.Generic.Dictionary<string, string>(ModContentLoaderTexture2D_LoadTexture_Patch.loadedTexturesThisSession);
            if (ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadHits > 0
                || ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadFailures > 0
                || TextureResize.CacheCount > 0)
            {
                Log.Message("[FasterGameLoading] Texture downscale cache hits: " + ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadHits
                    + ", failures: " + ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadFailures
                    + ", configured entries: " + TextureResize.CacheCount);
            }
            SessionCache.loadedTypesByFullNameSinceLastSession = GenTypes_GetTypeInAnyAssemblyInt_Patch.loadedTypesThisSession;
            SessionCache.xmlPathsSinceLastSession = new System.Collections.Generic.Dictionary<string, bool>(XmlNode_SelectSingleNode_Patch.xmlPathsThisSession);

            // Inject translations
            TranslationInjector.InjectTranslations();

            // Schedule setting write and delayed actions via LongEventHandler to avoid blocking startup
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
