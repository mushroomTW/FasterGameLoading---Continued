using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    [HarmonyPatch(typeof(ModAssetBundlesHandler), "ReloadAll")]
    public static class ModAssetBundlesHandler_ReloadAll_Patch
    {
        public static bool Prefix(ModAssetBundlesHandler __instance)
        {
            // AssetBundle 已被 RimWorld 原生流程載入過，跳過重複載入
            if (__instance.loadedAssetBundles != null && __instance.loadedAssetBundles.Count > 0)
                return false;
            return true;
        }
    }
}
