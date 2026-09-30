using HarmonyLib;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 查詢被 <see cref="StaticAtlasDeduplicator"/> 移除的 Item 或 Misc 副本時，會落到原版 TryGetStaticTile 的第二輪
    /// （搜尋所有圖集）並在 Building 圖集找到；原版此時每次查詢都記一筆警告。
    /// 只對被移除的組合把 ignoreFoundInOtherAtlas 設為 true，其他查詢不受影響。
    /// 不受設定開關控制：只要本次載入移除過組合就需要它，切換語言後的重新烘焙也一樣。
    ///
    /// A lookup of an Item or Misc copy that <see cref="StaticAtlasDeduplicator"/> removed falls through to vanilla
    /// TryGetStaticTile's second pass, which searches every atlas and finds the Building copy; vanilla logs a warning
    /// on every such lookup. This sets ignoreFoundInOtherAtlas for the removed pairs only, so every other lookup is
    /// unaffected. It does not check the setting: it is needed whenever this load removed a pair, including the bake
    /// that follows a language change.
    /// </summary>
    [HarmonyPatch(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.TryGetStaticTile))]
    public static class GlobalTextureAtlasManager_TryGetStaticTile_Patch
    {
        public static void Prefix(TextureAtlasGroup group, Texture2D texture, ref bool ignoreFoundInOtherAtlas)
        {
            if (!ignoreFoundInOtherAtlas && StaticAtlasDeduplicator.IsDroppedCopy(texture, group))
            {
                ignoreFoundInOtherAtlas = true;
            }
        }
    }
}
