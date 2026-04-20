using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    [HarmonyPatch(typeof(GlobalTextureAtlasManager), "TryInsertStatic")]
    public static class GlobalTextureAtlasManager_TryInsertStatic_Patch
    {
        // 只在 StaticAtlasesBaking 關閉時 patch（由 Prepare 控制）
        // 阻止圖形載入完成前的 TryInsertStatic 呼叫，完成後放行以保留 atlas 批次渲染效能
        public static bool Prepare() => !FasterGameLoadingSettings.StaticAtlasesBaking;
        public static bool Prefix()
        {
            return DelayedActions.AllGraphicLoaded;
        }
    }
}

