using HarmonyLib;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 GlobalTextureAtlasManager.TryInsertStatic，阻止受貼圖保護 Mod 的紋理進入靜態圖集，
    /// 避免多遮罩（multi-mask）造成圖案衝突與載入不全。哪些貼圖要排除由 <see cref="ProtectedMods"/> 決定，
    /// 載入時由 <see cref="LoadedTextureRegistry"/> 記下實體。
    /// </summary>
    [HarmonyPatch(typeof(GlobalTextureAtlasManager), "TryInsertStatic")]
    public static class AdaptiveBakingSkipList
    {
        public static bool Prepare() => DelayedActions.AdaptiveBakingEnabled;

        /// <summary>
        /// 主紋理或遮罩任一已登記為排除烘焙，就回傳 false 跳過原方法（不寫入靜態圖集）。
        /// 只比對實體（實體來自以完整路徑判定的載入流程）；不以檔名比對，
        /// 否則其他 Mod 的同名貼圖（例如 Body_north）也會被誤排除在圖集之外。
        /// </summary>
        public static bool Prefix(TextureAtlasGroup group, Texture2D texture, Texture2D mask)
        {
            return !LoadedTextureRegistry.IsSkippedForBaking(texture) && !LoadedTextureRegistry.IsSkippedForBaking(mask);
        }
    }
}
