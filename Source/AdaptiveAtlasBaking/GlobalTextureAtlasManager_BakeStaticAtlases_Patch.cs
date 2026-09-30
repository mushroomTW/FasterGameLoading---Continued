using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 GlobalTextureAtlasManager.BakeStaticAtlases，由延遲視覺管線決定是否放行原版烘焙：
    /// - 沒有啟用延遲載入：放行原版。烘焙在啟動流程中同步完成，
    ///   自適應分批只為了分幀讓出，在同一幀內跑完沒有任何好處，反而會把圖集切得比原版更碎。
    /// - 啟用延遲載入：啟動流程那一次跳過，改由 DelayedActions 在延遲圖形載入後烘焙；
    ///   管線以原版烘焙（自適應烘焙關閉或失敗）時才放行。
    /// </summary>
    [HarmonyPatch(typeof(GlobalTextureAtlasManager), "BakeStaticAtlases")]
    public static class GlobalTextureAtlasManager_BakeStaticAtlases_Patch
    {
        public static bool Prefix() => DelayedActions.AllowsVanillaStaticBake;
    }
}
