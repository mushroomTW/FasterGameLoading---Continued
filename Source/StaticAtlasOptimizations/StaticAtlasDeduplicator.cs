using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 略過靜態圖集中的重複紋理。
    /// ThingDef.PostLoad 會把可打包建築的圖形同時排進 Building 與 Item 兩組圖集，
    /// 它的藍圖 Def 又把同一張紋理排進 Misc，於是同一張紋理被烘焙三次。
    /// 打包後的建築以內層建築的分類（Building）查詢圖集，從不讀取 Item 那一份；
    /// 藍圖以 Misc 查詢，原版 TryGetStaticTile 在 Misc 找不到時會改搜尋所有圖集，於是找到 Building 那一份。
    /// 因此在烘焙前，從 Item 與 Misc 移除同樣排在 Building、且各群組遮罩狀態一致的紋理，並記下被移除的（紋理, 群組）組合，
    /// 讓 <see cref="GlobalTextureAtlasManager_TryGetStaticTile_Patch"/> 對這些組合不發出「在其他圖集群組找到」的警告。
    ///
    /// Skips duplicate textures in the static atlases.
    /// ThingDef.PostLoad queues a minifiable building's graphic in both the Building and the Item group, and its
    /// blueprint def queues the same texture in Misc, so the texture is baked three times. A minified building looks
    /// its graphic up under the inner building's category, Building, and never reads the Item copy; a blueprint looks
    /// it up under Misc, and when vanilla TryGetStaticTile finds nothing there it searches every atlas and finds the
    /// Building copy. Item and Misc entries also queued for Building are removed only when mask states are consistent,
    /// and the removed (texture, group) pairs are recorded so that
    /// <see cref="GlobalTextureAtlasManager_TryGetStaticTile_Patch"/> keeps vanilla's "found in another atlas group"
    /// warning quiet for them.
    /// </summary>
    public static class StaticAtlasDeduplicator
    {
        private static readonly TextureAtlasGroup[] CopyGroups = { TextureAtlasGroup.Item, TextureAtlasGroup.Misc };
        private static readonly bool[] MaskStates = { false, true };

        // 以反射讀取私有的 buildQueue：單元測試在 .NET 上執行，Publicizer 的存取略過在那裡無效，直接存取會拋 FieldAccessException。
        // Reads the private buildQueue by reflection: the unit tests run on .NET, where the Publicizer's access bypass does not
        // apply and direct access throws FieldAccessException.
        private static readonly FieldInfo BuildQueueField = AccessTools.Field(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.buildQueue));

        private static readonly HashSet<(Texture2D texture, TextureAtlasGroup group)> droppedCopies = new();

        static StaticAtlasDeduplicator()
        {
            // 切換語言會重新載入紋理並重新烘焙，舊的組合已不適用。
            // A language change reloads the textures and bakes again, so the old pairs no longer apply.
            SessionLifecycle.On(LifecyclePhase.LanguageReloading, static () => droppedCopies.Clear());
        }

        /// <summary>
        /// 本次載入從建置佇列移除的（紋理, 群組）組合。
        /// The (texture, group) pairs removed from the build queue in this load.
        /// </summary>
        public static IReadOnlyCollection<(Texture2D texture, TextureAtlasGroup group)> DroppedCopies => droppedCopies;

        /// <summary>
        /// 這個組合是否在烘焙前被移除。
        /// Whether this pair was removed before the bake.
        /// </summary>
        public static bool IsDroppedCopy(Texture2D texture, TextureAtlasGroup group)
            => droppedCopies.Count > 0 && droppedCopies.Contains((texture, group));

        /// <summary>
        /// 在確定會執行的烘焙之前呼叫：原版烘焙由 <see cref="GlobalTextureAtlasManager_BakeStaticAtlases_DeduplicatePatch"/> 呼叫，
        /// 自適應烘焙由 <see cref="AdaptiveAtlasBaker"/> 呼叫。重複呼叫是安全的，第二次不會再找到重複項目。
        ///
        /// Called just before a bake that will run: by <see cref="GlobalTextureAtlasManager_BakeStaticAtlases_DeduplicatePatch"/>
        /// for vanilla's bake, and by <see cref="AdaptiveAtlasBaker"/> for the adaptive bake. Calling it again is safe;
        /// the second call finds nothing left to remove.
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
        ///
        /// Removes from the Item and Misc groups the entries that are also queued in the Building group with the same
        /// mask state and no conflicting mask states in other groups, and returns the removed pairs.
        /// The remaining entries keep their order within each group; a group
        /// left empty is removed (vanilla never has an empty group, and would try to bake an empty atlas from one).
        /// Generic so that unit tests need no Texture2D.
        /// </summary>
        internal static List<(T item, TextureAtlasGroup group)> RemoveCopiesOfBuildingTextures<T>(
            Dictionary<TextureAtlasGroupKey, (List<T>, HashSet<T>)> queue)
            where T : class
        {
            var removed = new List<(T item, TextureAtlasGroup group)>();
            foreach (bool masked in MaskStates)
            {
                if (!queue.TryGetValue(new TextureAtlasGroupKey { group = TextureAtlasGroup.Building, hasMask = masked }, out var building))
                {
                    continue;
                }
                var buildingSet = new HashSet<T>(building.Item2, building.Item2.Comparer);
                // fallback 會搜尋所有圖集，不只 Building；任何相反遮罩狀態都可能先被命中。
                foreach (var queuedGroup in queue)
                {
                    if (queuedGroup.Key.hasMask != masked)
                    {
                        buildingSet.ExceptWith(queuedGroup.Value.Item2);
                    }
                }
                foreach (var group in CopyGroups)
                {
                    var key = new TextureAtlasGroupKey { group = group, hasMask = masked };
                    if (!queue.TryGetValue(key, out var entry))
                    {
                        continue;
                    }
                    var (items, itemSet) = entry;
                    foreach (T item in items)
                    {
                        if (item != null && buildingSet.Contains(item))
                        {
                            removed.Add((item, group));
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
            return removed;
        }
    }
}
