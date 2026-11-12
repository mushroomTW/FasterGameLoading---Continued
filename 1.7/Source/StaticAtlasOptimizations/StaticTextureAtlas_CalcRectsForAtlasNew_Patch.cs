using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Verse;

namespace FasterGameLoading;

/// <summary>
/// 把靜態圖集裁到實際用到的高度。
/// 原版打包器從 uv y = 0 往上逐列排放，高度取 2 的次方，第一次放不下時改用兩倍高度重試，
/// 所以最後一列之上全是空白，大型圖集常有一半是空的；這些空白列照樣要讀回、產生 mipmap 與壓縮。
/// 打包完成後，以實際用到的高度重新建立顏色紋理，並依新高度換算回傳的 UV 矩形，每張紋理的像素位置不變；
/// Bake 接著依這些矩形繪製、建立遮罩與網格。
/// 新頁面沿用原版頁面的取樣設定（名稱、filterMode、wrapMode、anisoLevel、mipMapBias、minimumMipmapLevel）與 GPU 記憶體分類；
/// 這些設定由原版 CreatePage 依成員紋理決定，重建頁面時必須原樣帶過去，否則會退回 Unity 預設值（例如 wrapMode 變回 Repeat）。
/// 高度向上取整到 8 × 2^(mips - 1) 的倍數，讓每一層 mipmap 的高度都是 4 的倍數（區塊壓縮 BC／DXT 的最小單位）且保有餘裕；
/// 最上方的紋理之上，每一層 mipmap 至少保留一列空白（原尺寸 2^(mips - 1) 列）：
/// 雙線性／三線性濾波與 mipmap 縮小時，邊緣紋理會取樣到相鄰的列，保留空白列讓它讀到的是透明像素，與原版圖集上緣一定有空白列的情況一致。
/// 打包器會遞迴呼叫自己重試，內層的結果經由外層回傳，因此只在最外層處理一次。
/// </summary>
[HarmonyPatch(typeof(StaticTextureAtlas), nameof(StaticTextureAtlas.CalcRectsForAtlasNew))]
public static class StaticTextureAtlas_CalcRectsForAtlasNew_Patch
{
    // 打包器目前的遞迴深度。烘焙只在主執行緒進行。
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
    /// </summary>
    public static int TrimmedHeight(int usedRows, int mipCount)
    {
        int lastMip = Math.Max(0, mipCount - 1);
        // 每層 mipmap 的高度都對齊 8（至少是 DXT 區塊的 4 的倍數）；刻意比 4 像素寬鬆，維持與先前版本相同的裁切結果。
        int step = 8 << lastMip;
        int keptEmptyRows = 1 << lastMip;
        return (usedRows + keptEmptyRows + step - 1) / step * step;
    }

    /// <summary>
    /// 把以舊高度正規化的 UV 矩形換算成以新高度正規化，列的位置與高度（以像素計）不變。
    /// </summary>
    public static Rect RescaleRows(Rect rect, int oldHeight, int newHeight)
        => new Rect(
            rect.x,
            Mathf.RoundToInt(rect.y * oldHeight) / (float)newHeight,
            rect.width,
            Mathf.RoundToInt(rect.height * oldHeight) / (float)newHeight);

    // 把原版頁面的名稱、取樣設定與 GPU 記憶體分類複製到重建的頁面。
    internal static void CopyPageSettings(Texture2D from, Texture2D to)
    {
        to.name = from.name;
        to.filterMode = from.filterMode;
        to.wrapMode = from.wrapMode;
        to.anisoLevel = from.anisoLevel;
        to.mipMapBias = from.mipMapBias;
        to.minimumMipmapLevel = from.minimumMipmapLevel;
        if (from.CoreTexture != null && to.CoreTexture != null)
        {
            to.CoreTexture.MemoryCategory = from.CoreTexture.MemoryCategory;
        }
    }

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
        var trimmedPage = new Texture2D(color.width, trimmed, color.graphicsFormat, color.mipmapCount,
            TextureCreationFlags.MipChain | TextureCreationFlags.DontInitializePixels);
        CopyPageSettings(color, trimmedPage);
        atlas.colorTexture = trimmedPage;
        UnityEngine.Object.DestroyImmediate(color);
        for (int i = 0; i < rects.Length; i++)
        {
            rects[i] = RescaleRows(rects[i], height, trimmed);
        }
    }
}
