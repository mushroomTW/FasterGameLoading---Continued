using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 把靜態圖集裁到實際用到的高度。
    /// 原版打包器從 uv y = 0 往上逐列排放，高度取 2 的次方，第一次放不下時改用兩倍高度重試，
    /// 所以最後一列之上全是空白，大型圖集常有一半是空的；這些空白列照樣要讀回、產生 mipmap 與壓縮。
    /// 打包完成後，以實際用到的高度重新建立顏色紋理，並依新高度換算回傳的 UV 矩形，每張紋理的像素位置不變；
    /// Bake 接著依這些矩形繪製、建立遮罩與網格。
    /// 高度向上取整到 8 × 2^(mips - 1) 的倍數，讓每一層 mipmap 都符合 GPU 壓縮的 8 像素派送邊界；
    /// 最上方的紋理之上，每一層 mipmap 至少保留一列空白（原尺寸 2^(mips - 1) 列）：
    /// 圖集取樣會環繞，上下邊緣的濾波會讀到另一側，原版圖集在那裡一定有空白列，裁切後也保留。
    /// 打包器會遞迴呼叫自己重試，內層的結果經由外層回傳，因此只在最外層處理一次。
    ///
    /// Trims each static atlas to the height it uses.
    /// Vanilla's packer fills rows upward from uv y = 0 into a power-of-two height and retries at double the
    /// height when the first attempt does not fit, so every row above the last packed one is empty, and large
    /// atlases are often half empty; those rows are still read back, mipmapped and compressed.
    /// After packing, this recreates the colour texture at the height actually used and rescales the returned UV
    /// rects to it, so no texture moves by a pixel; Bake then draws, masks and builds meshes from those rects.
    /// The height is rounded up to a multiple of 8 x 2^(mips - 1), so GPU compression covers every mip level,
    /// and at least one empty row per mip level (2^(mips - 1) rows at full size) is kept above the top
    /// texture: atlas sampling wraps, so filtering at the top and bottom edges reads the opposite edge, where a
    /// vanilla atlas always has empty rows, and a trimmed one keeps them.
    /// The packer calls itself to retry and the inner result returns through the outer call, so only the outermost
    /// call acts.
    /// </summary>
    [HarmonyPatch(typeof(StaticTextureAtlas), nameof(StaticTextureAtlas.CalcRectsForAtlasNew))]
    public static class StaticTextureAtlas_CalcRectsForAtlasNew_Patch
    {
        /// <summary>
        /// 打包器目前的遞迴深度。烘焙只在主執行緒進行。
        /// The packer's current recursion depth. Baking runs on the main thread only.
        /// </summary>
        private static int depth;

        public static void Prefix() => depth++;

        public static void Postfix(StaticTextureAtlas __instance, Rect[] __result)
        {
            if (depth is 1 && FasterGameLoadingSettings.TrimStaticAtlases)
            {
                TrimToUsedRows(__instance, __result);
            }
        }

        public static void Finalizer() => depth--;

        /// <summary>
        /// 最上方紋理止於第 <paramref name="usedRows"/> 列時，裁切後的圖集高度。
        /// The trimmed atlas height when the top texture ends at row <paramref name="usedRows"/>.
        /// </summary>
        public static int TrimmedHeight(int usedRows, int mipCount)
        {
            int lastMip = Math.Max(0, mipCount - 1);
            // FastCompressDXT 使用高度 / 8 的整數派送；只對齊 DXT 的 4 像素會漏寫末端區塊。
            int step = 8 << lastMip;
            int keptEmptyRows = 1 << lastMip;
            return (usedRows + keptEmptyRows + step - 1) / step * step;
        }

        /// <summary>
        /// 把以舊高度正規化的 UV 矩形換算成以新高度正規化，列的位置與高度（以像素計）不變。
        /// Converts a UV rect normalised to the old height into one normalised to the new height, keeping its rows
        /// (in pixels) where they were.
        /// </summary>
        public static Rect RescaleRows(Rect rect, int oldHeight, int newHeight)
            => new Rect(
                rect.x,
                Mathf.RoundToInt(rect.y * oldHeight) / (float)newHeight,
                rect.width,
                Mathf.RoundToInt(rect.height * oldHeight) / (float)newHeight);

        private static void TrimToUsedRows(StaticTextureAtlas atlas, Rect[] rects)
        {
            Texture2D color = atlas.colorTexture;
            if (rects == null || rects.Length is 0 || color == null)
            {
                return;
            }
            int height = color.height;
            int usedRows = 0;
            foreach (Rect rect in rects)
            {
                usedRows = Math.Max(usedRows, Mathf.RoundToInt(rect.yMax * height));
            }
            int trimmed = TrimmedHeight(usedRows, color.mipmapCount);
            if (trimmed >= height)
            {
                return;
            }
            atlas.colorTexture = new Texture2D(color.width, trimmed, color.graphicsFormat, color.mipmapCount,
                TextureCreationFlags.MipChain | TextureCreationFlags.DontInitializePixels);
            UnityEngine.Object.DestroyImmediate(color);
            for (int i = 0; i < rects.Length; i++)
            {
                rects[i] = RescaleRows(rects[i], height, trimmed);
            }
        }
    }
}
