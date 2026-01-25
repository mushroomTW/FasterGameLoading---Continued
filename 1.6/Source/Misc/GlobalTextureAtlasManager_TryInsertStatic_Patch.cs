using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    [HarmonyPatch(typeof(GlobalTextureAtlasManager), "TryInsertStatic")]
    public static class GlobalTextureAtlasManager_TryInsertStatic_Patch
    {
        public static bool Prepare() => FasterGameLoadingSettings.disableStaticAtlasesBaking;
        public static bool Prefix()
        {
            // 只在延遲圖形載入且尚未完成載入時阻止
            // 這樣載入完成後仍可獲得 atlas 批次渲染的效能優勢
            return !FasterGameLoadingSettings.disableStaticAtlasesBaking
                || DelayedActions.AllGraphicLoaded;
        }
    }
}

