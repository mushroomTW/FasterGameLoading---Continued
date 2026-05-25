using HarmonyLib;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 針對 Bionic Icons 的靜態圖集（Static Atlas）烘焙相容性補丁。
    /// 攔截 GlobalTextureAtlasManager.TryInsertStatic，阻止 Bionic Icons 的紋理進入靜態圖集，避免因多遮罩（multi-mask）造成圖案衝突與載入不全。
    /// </summary>
    [HarmonyPatch(typeof(GlobalTextureAtlasManager), "TryInsertStatic")]
    public static class BionicIconsBakePatch
    {
        public static bool Prepare() => EarlyLoadSkipList.IsBionicIconsActive;

        /// <summary>
        /// 在將主紋理與遮罩紋理寫入靜態圖集前進行攔截。
        /// 如果該紋理在 ModContentLoader 載入快取中的硬碟路徑包含 "bionicicons"，則回傳 false 跳過原方法。
        /// </summary>
        public static bool Prefix(TextureAtlasGroup group, Texture2D texture, Texture2D mask)
        {
            if (IsBionicIconTexture(texture) || IsBionicIconTexture(mask))
            {
                // 拒絕將 Bionic Icons 紋理塞入靜態圖集
                return false;
            }
            return true;
        }

        /// <summary>
        /// 輔助方法：透過 ModContentLoader 載入的 WeakReference 快取路徑來精準辨識 Texture2D 實體是否來自 Bionic Icons。
        /// </summary>
        private static bool IsBionicIconTexture(Texture2D texture)
        {
            if (texture == null) return false;

            // 遍歷本 session 中所有載入的紋理 WeakReference 快取
            foreach (var kvp in ModContentLoaderTexture2D_LoadTexture_Patch.savedTextures)
            {
                // 檢查硬碟完整路徑中是否含有 "bionicicons" 關鍵字
                if (kvp.Key.IndexOf("bionicicons", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (kvp.Value.TryGetTarget(out var savedTex) && savedTex == texture)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
