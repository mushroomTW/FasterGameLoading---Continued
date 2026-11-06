using HarmonyLib;
using Verse;

namespace FasterGameLoading;

/// <summary>
/// 原版烘焙前移除重複紋理（見 <see cref="StaticAtlasDeduplicator"/>）。
/// 以最低優先順序排在其他所有 prefix 之後，因此只在原版烘焙確定會執行時才動手，
/// 其他 mod 在自己的 prefix 裡才排進佇列的紋理也一併涵蓋。
/// </summary>
[HarmonyPatch(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.BakeStaticAtlases))]
public static class GlobalTextureAtlasManager_BakeStaticAtlases_DeduplicatePatch
{
    [HarmonyPriority(Priority.Last)]
    public static void Prefix(bool __runOriginal)
    {
        if (__runOriginal)
        {
            StaticAtlasDeduplicator.RemoveDuplicateCopies();
        }
    }
}
