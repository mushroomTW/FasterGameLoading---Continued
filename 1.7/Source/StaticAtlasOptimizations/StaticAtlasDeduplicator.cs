using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace FasterGameLoading;

/// <summary>
/// 略過靜態圖集中的重複紋理。
/// ThingDef.PostLoad 會把可打包建築的圖形同時排進 Building 與 Item 兩組圖集，
/// 它的藍圖 Def 又把同一張紋理排進 Misc，於是同一張紋理被烘焙三次。
/// 打包後的建築以內層建築的分類（Building）查詢圖集，從不讀取 Item 那一份；
/// 藍圖以 Misc 查詢，原版 TryGetStaticTile 在 Misc 找不到時會改搜尋所有圖集，於是找到 Building 那一份。
/// 因此在烘焙前，從 Item 與 Misc 移除同樣排在 Building、且各群組遮罩狀態一致的紋理，並記下被移除的（紋理, 群組）組合，
/// 讓 <see cref="GlobalTextureAtlasManager_TryGetStaticTile_Patch"/> 對這些組合不發出「在其他圖集群組找到」的警告。
/// </summary>
public static class StaticAtlasDeduplicator
{
    private static readonly TextureAtlasGroup[] CopyGroups = [TextureAtlasGroup.Item, TextureAtlasGroup.Misc];
    private static readonly bool[] MaskStates = [false, true];

    // 以反射讀取私有的 buildQueue：Publicizer 只讓編譯通過，直接存取在 .NET 10（CoreCLR）上會拋 FieldAccessException（單元測試已實測），
    // 因此不依賴遊戲執行環境是否放寬存取檢查。
    private static readonly FieldInfo BuildQueueField = AccessTools.Field(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.buildQueue));

    private static readonly HashSet<(Texture2D texture, TextureAtlasGroup group)> droppedCopies = new();

    static StaticAtlasDeduplicator()
    {
        // 切換語言會重新載入紋理並重新烘焙，舊的組合已不適用。
        SessionLifecycle.On(LifecyclePhase.LanguageReloading, static () => droppedCopies.Clear());
    }

    /// <summary>
    /// 本次載入從建置佇列移除的（紋理, 群組）組合。
    /// </summary>
    public static IReadOnlyCollection<(Texture2D texture, TextureAtlasGroup group)> DroppedCopies => droppedCopies;

    /// <summary>
    /// 這個組合是否在烘焙前被移除。
    /// </summary>
    public static bool IsDroppedCopy(Texture2D texture, TextureAtlasGroup group)
        => droppedCopies.Count > 0 && droppedCopies.Contains((texture, group));

    /// <summary>
    /// 在確定會執行的烘焙之前呼叫：原版烘焙由 <see cref="GlobalTextureAtlasManager_BakeStaticAtlases_DeduplicatePatch"/> 呼叫。重複呼叫是安全的，第二次不會再找到重複項目。
    /// </summary>
    public static void RemoveDuplicateCopies()
    {
        if (!FasterGameLoadingSettings.DeduplicateStaticAtlases)
        {
            return;
        }
        if (BuildQueueField?.GetValue(null) is not Dictionary<TextureAtlasGroupKey, (List<Texture2D>, HashSet<Texture2D>)> queue)
        {
            return;
        }
        var removed = RemoveCopiesOfBuildingTextures(queue);
        long pixels = 0;
        foreach (var (texture, group) in removed)
        {
            droppedCopies.Add((texture, group));
            pixels += (long)texture.width * texture.height;
        }
        if (removed.Count > 0)
        {
            FGLLog.Message($"Static atlas dedup: skipped {removed.Count.ToString(CultureInfo.InvariantCulture)} Item and Misc copies of Building textures ({(pixels / 1e6).ToString("F1", CultureInfo.InvariantCulture)} Mpx)");
        }
    }

    /// <summary>
    /// 從 Item 與 Misc 群組移除同樣以相同遮罩狀態排在 Building 群組的項目，回傳被移除的組合。
    /// 若任一群組有同主紋理但不同遮罩狀態，保留副本；原版跨群組查詢不比對遮罩狀態。
    /// 其餘項目在各群組中的順序不變；移除後變空的群組整個刪除（原版不會有空群組，會試著用它烘焙一張空圖集）。
    /// 以泛型實作，單元測試因此不必建立 Texture2D。
    /// </summary>
    internal static List<(T item, TextureAtlasGroup group)> RemoveCopiesOfBuildingTextures<T>(
        Dictionary<TextureAtlasGroupKey, (List<T>, HashSet<T>)> queue)
        where T : class
    {
        List<(T item, TextureAtlasGroup group)> removed = [];
        foreach (bool masked in MaskStates)
        {
            if (!TryBuildEligibleBuildingSet(queue, masked, out var buildingSet))
            {
                continue;
            }

            foreach (var group in CopyGroups)
            {
                DeduplicateGroup(queue, new TextureAtlasGroupKey { group = group, hasMask = masked }, buildingSet, removed);
            }
        }
        return removed;
    }

    private static bool TryBuildEligibleBuildingSet<T>(
        Dictionary<TextureAtlasGroupKey, (List<T>, HashSet<T>)> queue,
        bool masked,
        out HashSet<T> buildingSet)
        where T : class
    {
        if (!queue.TryGetValue(new TextureAtlasGroupKey { group = TextureAtlasGroup.Building, hasMask = masked }, out var building))
        {
            buildingSet = null;
            return false;
        }
        buildingSet = new HashSet<T>(building.Item2, building.Item2.Comparer);
        // fallback 會搜尋所有圖集，不只 Building；任何相反遮罩狀態都可能先被命中。
        foreach (var queuedGroup in queue)
        {
            if (queuedGroup.Key.hasMask != masked)
            {
                buildingSet.ExceptWith(queuedGroup.Value.Item2);
            }
        }
        return true;
    }

    private static void DeduplicateGroup<T>(
        Dictionary<TextureAtlasGroupKey, (List<T>, HashSet<T>)> queue,
        TextureAtlasGroupKey key,
        HashSet<T> buildingSet,
        List<(T item, TextureAtlasGroup group)> removed)
        where T : class
    {
        if (!queue.TryGetValue(key, out var entry))
        {
            return;
        }
        var (items, itemSet) = entry;
        foreach (T item in items)
        {
            if (item != null && buildingSet.Contains(item))
            {
                removed.Add((item, key.group));
                itemSet.Remove(item);
            }
        }
        items.RemoveAll(item => item != null && buildingSet.Contains(item));
        if (items.Count is 0)
        {
            queue.Remove(key);
        }
    }
}
