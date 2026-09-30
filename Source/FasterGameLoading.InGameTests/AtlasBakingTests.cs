using System.Collections.Generic;
using System.Linq;
using RimTestRedux;
using UnityEngine;
using Verse;

namespace FasterGameLoading.InGameTests
{
    /// <summary>
    /// 靜態圖集烘焙的最終狀態：不重複烘焙（重複會多吃一份 VRAM）、佇列中的貼圖都有進圖集、
    /// 原版 1.6 的 pre-insert 貼圖沒有遺失。三條烘焙路徑（原版啟動時、延遲後原版、延遲後自適應）都適用。
    /// </summary>
    [TestSuite]
    internal static class AtlasBakingTests
    {
        /// <summary>本次載入烘焙的圖集；語言重載後排除原版留下的舊圖集（見 <see cref="LanguageReload.AtlasesBeforeReload"/>）。</summary>
        internal static IEnumerable<StaticTextureAtlas> CurrentLoadAtlases
            => GlobalTextureAtlasManager.staticTextureAtlases.Where(static a => TestRunDriver.Round != 2 || !LanguageReload.AtlasesBeforeReload.Contains(a));

        [Test]
        public static void StaticAtlasesWereBaked()
        {
            Assert.ThatCollection(CurrentLoadAtlases.ToList()).Is.Not.Empty();
        }

        [Test]
        public static void AdaptiveBakeDidNotFallBack()
        {
            // 自適應烘焙沒啟用時不會執行，此值恆為 false。
            Assert.That(AdaptiveAtlasBaker.LastBakeFailed).Is.False();
        }

        /// <summary>同一張貼圖在同一個 group 出現在兩張圖集，代表烘焙跑了兩次。</summary>
        [Test]
        public static void NoTextureIsBakedTwiceInTheSameGroup()
        {
            var seen = new HashSet<(TextureAtlasGroupKey, Texture2D)>();
            var failures = new List<string>();
            foreach (var atlas in CurrentLoadAtlases)
            {
                foreach (var texture in atlas.textures)
                {
                    if (!seen.Add((atlas.groupKey, texture)))
                    {
                        failures.Add($"{texture?.name} in {atlas.groupKey}");
                    }
                }
            }
            FglState.AssertNone(failures, "textures baked more than once");
        }

        [Test]
        public static void EveryAtlasTextureHasATile()
        {
            var failures = new List<string>();
            foreach (var atlas in CurrentLoadAtlases)
            {
                if (atlas.ColorTexture == null)
                {
                    failures.Add($"atlas {atlas.groupKey} has no color texture");
                    continue;
                }
                foreach (var texture in atlas.textures)
                {
                    if (!atlas.TryGetTile(texture, out _))
                    {
                        failures.Add($"{texture?.name} in {atlas.groupKey}");
                    }
                }
            }
            FglState.AssertNone(failures, "atlas textures without tiles");
        }

        /// <summary>
        /// 原版烘焙不清空 buildQueue，自適應烘焙提交後會清空；兩種情況下仍留在佇列裡的貼圖都必須已在同 group 的圖集中。
        /// 原版打包失敗時會清空該圖集的貼圖（留下空圖集），這屬於原版行為，該 group 不列入檢查。
        /// </summary>
        [Test]
        public static void QueuedTexturesAreInAnAtlasOfTheirGroup()
        {
            var packingFailedGroups = new HashSet<TextureAtlasGroupKey>();
            foreach (var atlas in CurrentLoadAtlases)
            {
                if (atlas.textures.Count == 0) packingFailedGroups.Add(atlas.groupKey);
            }
            var failures = new List<string>();
            foreach (var entry in GlobalTextureAtlasManager.buildQueue)
            {
                if (packingFailedGroups.Contains(entry.Key)) continue;
                foreach (var texture in entry.Value.Item1)
                {
                    if (texture != null && !IsInAtlas(entry.Key, texture))
                    {
                        failures.Add($"{texture.name} in {entry.Key}");
                    }
                }
            }
            FglState.AssertNone(failures, "queued textures missing from atlases");
        }

        /// <summary>
        /// RimWorld 1.6 的 BakeStaticAtlases 會先插入建築損傷刮痕與搬運箱貼圖；
        /// 自適應烘焙繞過原版方法，必須自行補插，否則這些貼圖會各自成為獨立的 draw call。
        /// </summary>
        [Test]
        public static void VanillaPreInsertedTexturesAreAtlased()
        {
            var failures = new List<string>();
            foreach (var path in new[] { "Things/Item/Minified/CrateFront", "Things/Item/Minified/BurlapBag" })
            {
                var texture = ContentFinder<Texture2D>.Get(path, reportFailure: false);
                if (texture != null && !GlobalTextureAtlasManager.TryGetStaticTile(TextureAtlasGroup.Item, texture, out _, ignoreFoundInOtherAtlas: true))
                {
                    failures.Add(path);
                }
            }
            foreach (var mat in BuildingsDamageSectionLayerUtility.DefaultScratchMats)
            {
                var texture = (Texture2D)mat.mainTexture;
                if (texture.width < 512 && texture.height < 512
                    && !GlobalTextureAtlasManager.TryGetStaticTile(TextureAtlasGroup.Building, texture, out _, ignoreFoundInOtherAtlas: true))
                {
                    failures.Add(texture.name);
                }
            }
            FglState.AssertNone(failures, "vanilla pre-inserted textures not atlased");
        }

        private static bool IsInAtlas(TextureAtlasGroupKey key, Texture2D texture)
        {
            foreach (var atlas in CurrentLoadAtlases)
            {
                if (atlas.groupKey.Equals(key) && atlas.TryGetTile(texture, out _))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
