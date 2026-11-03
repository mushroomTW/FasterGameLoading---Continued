using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace FasterGameLoading;

/// <summary>
/// 攔截 GlobalTextureAtlasManager.BakeStaticAtlases，由延遲視覺管線決定是否放行原版烘焙：
/// - 未啟用延遲載入：放行原版在啟動期間同步烘焙圖集。
/// - 啟用延遲載入：跳過啟動期間的一次烘焙；延遲圖形管線處理完成後，再放行原版烘焙。
///   跳過時改在這裡結束 1.7 的延遲紋理載入（見 <see cref="EndDeferredTextureLoading"/>）。
/// </summary>
[HarmonyPatch(typeof(GlobalTextureAtlasManager), "BakeStaticAtlases")]
public static class GlobalTextureAtlasManager_BakeStaticAtlases_Patch
{
    // Publicizer 只讓編譯通過；在 .NET 10 直接存取私有欄位會拋 FieldAccessException。
    private static readonly FieldInfo BuildQueueField = AccessTools.Field(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.buildQueue));
    private static readonly FieldInfo BuildQueueMasksField = AccessTools.Field(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.buildQueueMasks));

    // 原版的來源貼圖釋放開關（啟動參數 -noEvictAtlasSources 會關閉）；為 private，經由委派呼叫。
    private static readonly Func<bool> AtlasSourceEvictionEnabled = AccessTools.MethodDelegate<Func<bool>>(
        AccessTools.PropertyGetter(typeof(GlobalTextureAtlasManager), "AtlasSourceEvictionEnabled"));

    public static bool Prefix()
    {
        if (DelayedActions.AllowsVanillaStaticBake) return true;
        var delayedActions = FasterGameLoadingMod.delayedActions;
        if (delayedActions != null) delayedActions.RegisterQueuedTextureImportSettings();
        EndDeferredTextureLoading();
        return false;
    }

    /// <summary>
    /// 1.7 的 PlayDataLoader 在載入 mod 前開啟延遲紋理載入，原版烘焙結束時才由 EndDeferredLoading 平行上傳。
    /// 跳過啟動那次烘焙後，改在此處先結束延遲載入，以原版烘焙的條件（已排入烘焙佇列的圖集來源紋理與遮罩維持不上傳；
    /// 原版關閉來源貼圖釋放時則全部上傳）平行上傳其餘紋理，延遲管線的圖形載入因此不再被一次同步烘焙擋住，主選單可以先出現。
    /// 之後原版烘焙內再呼叫 EndDeferredLoading 時延遲清單已空，直接返回。
    /// </summary>
    private static void EndDeferredTextureLoading()
    {
        HashSet<Waylith.Rendering.Texture2D> atlasSources = [];
        if (AtlasSourceEvictionEnabled())
        {
            var buildQueue = (Dictionary<TextureAtlasGroupKey, (List<Texture2D>, HashSet<Texture2D>)>)BuildQueueField.GetValue(null);
            var buildQueueMasks = (Dictionary<Texture2D, Texture2D>)BuildQueueMasksField.GetValue(null);
            AddAtlasSources(buildQueue, buildQueueMasks, atlasSources);
        }
        Waylith.Rendering.Texture2D.EndDeferredLoading(atlasSources.Contains);
    }

    /// <summary>
    /// 收集維持不上傳的圖集來源：與原版 StaticTextureAtlas.Members 相同，含主紋理與有遮罩群組的遮罩
    /// （原版烘焙只對有遮罩的群組取用 buildQueueMasks）。
    /// </summary>
    internal static void AddAtlasSources(
        Dictionary<TextureAtlasGroupKey, (List<Texture2D>, HashSet<Texture2D>)> buildQueue,
        Dictionary<Texture2D, Texture2D> buildQueueMasks,
        HashSet<Waylith.Rendering.Texture2D> atlasSources)
    {
        foreach (var (key, (textures, _)) in buildQueue)
        {
            foreach (var texture in textures)
            {
                if (texture == null) continue;
                if (texture.CoreTexture != null) atlasSources.Add(texture.CoreTexture);
                if (key.hasMask && buildQueueMasks.TryGetValue(texture, out var mask) && mask?.CoreTexture != null)
                {
                    atlasSources.Add(mask.CoreTexture);
                }
            }
        }
    }
}
