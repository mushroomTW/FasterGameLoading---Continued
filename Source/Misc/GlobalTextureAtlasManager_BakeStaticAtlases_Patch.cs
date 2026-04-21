using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    [HarmonyPatch(typeof(GlobalTextureAtlasManager), "BakeStaticAtlases")]
    public static class GlobalTextureAtlasManager_BakeStaticAtlases_Patch
    {
        public static bool Prefix()
        {
            if (DelayedActions.LanguageReloadInProgress)
            {
                return true;
            }

            if (!DelayedActions.AllDeferredVisualsLoaded)
            {
                return false;
            }

            if (!FasterGameLoadingSettings.StaticAtlasesBaking)
            {
                return true;
            }

            return DelayedActions.AdaptiveStaticAtlasBakeFailed;
        }
    }
}
