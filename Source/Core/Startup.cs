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
            FasterGameLoadingSettings.loadedTypesByFullNameSinceLastSession = GenTypes_GetTypeInAnyAssemblyInt_Patch.loadedTypesThisSession;
            FasterGameLoadingSettings.successfulXMLPathsSinceLastSession = XmlNode_SelectSingleNode_Patch.successfulXMLPathsThisSession;
            FasterGameLoadingSettings.failedXMLPathsSinceLastSession = XmlNode_SelectSingleNode_Patch.failedXMLPathsThisSession;
            LoadedModManager.GetMod<FasterGameLoadingMod>().WriteSettings();
            TranslationInjector.InjectTranslations();
            LongEventHandler.toExecuteWhenFinished.Add(delegate
            {
                FasterGameLoadingMod.delayedActions.StartCoroutine(FasterGameLoadingMod.delayedActions.PerformActions());
            });
        }
    }
}
