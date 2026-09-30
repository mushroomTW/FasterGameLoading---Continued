using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using RimTestRedux;
using UnityEngine;
using Verse;

namespace FasterGameLoading.InGameTests
{
    /// <summary>
    /// 靜態圖集的三個選項：裁切（TrimStaticAtlases）、略過重複紋理（DeduplicateStaticAtlases）、較快的壓縮（FastStaticAtlasCompression）。
    /// 選項關閉時，只檢查該選項的測試直接略過；像素對齊與壓縮兩項不論選項開關都必須成立。
    /// 三條烘焙路徑（原版啟動時、延遲後原版、延遲後自適應）都適用；自適應烘焙中只有一張紋理、直接沿用該紋理的圖集不算烘焙過的圖集。
    ///
    /// The three static atlas options: trimming (TrimStaticAtlases), skipping duplicate textures (DeduplicateStaticAtlases)
    /// and faster compression (FastStaticAtlasCompression). A test that checks only one option returns early when that
    /// option is off; the pixel-grid and compression tests must hold with the options on or off. All three bake paths
    /// apply (vanilla at startup, vanilla after delayed loading, adaptive after delayed loading); an adaptive atlas that
    /// holds a single texture reuses that texture as it is and does not count as baked.
    /// </summary>
    [TestSuite]
    internal static class StaticAtlasOptimizationTests
    {
        private static IEnumerable<StaticTextureAtlas> BakedAtlases
            => AtlasBakingTests.CurrentLoadAtlases.Where(static atlas =>
                atlas.ColorTexture != null && atlas.textures.Count > 0 && !atlas.textures.Contains(atlas.ColorTexture));

        /// <summary>
        /// 裁切開啟時，每張圖集在最上方紋理之上，只保留每層 mipmap 一列空白，加上不到一個 GPU 壓縮派送群組的取整餘量。
        /// 同時記下本次載入的圖集總量，方便比較不同設定。
        ///
        /// With trimming on, each atlas keeps one empty row per mip level above its top texture, plus less than one GPU
        /// compression group of rounding. Also logs this load's atlas totals, for comparing settings.
        /// </summary>
        [Test]
        public static void TrimmedAtlasesEndJustAboveTheirTopTexture()
        {
            var baked = BakedAtlases.ToList();
            LogAtlasTotals(baked);
            if (!FasterGameLoadingSettings.TrimStaticAtlases) return;

            var failures = new List<string>();
            foreach (var atlas in baked)
            {
                var color = atlas.ColorTexture;
                if (atlas.tiles.Count is 0) continue;

                int usedRows = atlas.tiles.Values.Max(tile => Mathf.RoundToInt(tile.uvRect.yMax * color.height));
                int lastMip = Math.Max(0, color.mipmapCount - 1);
                int extraEmptyRows = color.height - usedRows - (1 << lastMip);
                if (extraEmptyRows >= (8 << lastMip))
                {
                    failures.Add($"{atlas.groupKey} {color.width}x{color.height}: top texture ends at row {usedRows}, {extraEmptyRows} empty rows more than needed");
                }
            }
            FglState.AssertNone(failures, "static atlases taller than their textures need");
        }

        /// <summary>
        /// 每張紋理在圖集中的 UV 矩形都落在整數像素上，且不超出圖集。裁切會重新換算矩形，換算出錯時紋理會偏離像素格或被截斷；
        /// 原版的矩形本來就符合。
        ///
        /// Every texture's UV rect in its atlas sits on whole pixels and inside the atlas. Trimming recomputes the rects,
        /// and a wrong conversion moves textures off the pixel grid or cuts them off; vanilla's rects always pass.
        /// </summary>
        [Test]
        public static void AtlasTexturesSitOnWholePixels()
        {
            var failures = new List<string>();
            foreach (var atlas in BakedAtlases)
            {
                var color = atlas.ColorTexture;
                foreach (var pair in atlas.tiles)
                {
                    var uv = pair.Value.uvRect;
                    bool onGrid = OnPixel(uv.x, color.width) && OnPixel(uv.width, color.width)
                        && OnPixel(uv.y, color.height) && OnPixel(uv.height, color.height);
                    bool inside = uv.xMin >= 0f && uv.yMin >= 0f && uv.xMax <= 1.0001f && uv.yMax <= 1.0001f;
                    if (!onGrid || !inside)
                    {
                        failures.Add(string.Format(CultureInfo.InvariantCulture, "{0} in {1} {2}x{3}: at ({4:F2}, {5:F2}), {6:F2}x{7:F2} px",
                            pair.Key.name, atlas.groupKey, color.width, color.height,
                            uv.x * color.width, uv.y * color.height, uv.width * color.width, uv.height * color.height));
                    }
                }
            }
            FglState.AssertNone(failures, "atlas textures off the pixel grid or outside their atlas");
        }

        /// <summary>
        /// 略過重複紋理開啟時，遮罩狀態一致的 Building 紋理不會重複烘進 Item 或 Misc。
        /// 跨圖集存在不同遮罩狀態的主紋理必須保留副本，避免 fallback 取得錯誤遮罩。
        /// </summary>
        [Test]
        public static void BuildingTexturesAreNotAlsoBakedIntoItemOrMisc()
        {
            if (!FasterGameLoadingSettings.DeduplicateStaticAtlases) return;

            var atlases = AtlasBakingTests.CurrentLoadAtlases.ToList();
            var maskedTextures = new HashSet<Texture2D>(atlases
                .Where(static atlas => atlas.groupKey.hasMask)
                .SelectMany(static atlas => atlas.textures));
            var unmaskedTextures = new HashSet<Texture2D>(atlases
                .Where(static atlas => !atlas.groupKey.hasMask)
                .SelectMany(static atlas => atlas.textures));
            var building = new HashSet<(Texture2D, bool)>(atlases
                .Where(static atlas => atlas.groupKey.group is TextureAtlasGroup.Building)
                .SelectMany(static atlas => atlas.textures.Select(texture => (texture, atlas.groupKey.hasMask))));
            var failures = atlases
                .Where(static atlas => atlas.groupKey.group is TextureAtlasGroup.Item or TextureAtlasGroup.Misc)
                .SelectMany(atlas => atlas.textures
                    .Where(texture => building.Contains((texture, atlas.groupKey.hasMask))
                        && !(atlas.groupKey.hasMask ? unmaskedTextures : maskedTextures).Contains(texture))
                    .Select(texture => $"{texture.name} in {atlas.groupKey}"))
                .ToList();
            FglState.AssertNone(failures, "Building textures also baked into an Item or Misc atlas");
        }

        /// <summary>
        /// 每個被略過的副本仍能以原本的群組查到，查到的是 Building 圖集，而且不記警告。
        /// 警告記錄涵蓋整個載入過程（quicktest 時包含地圖繪製），不只這裡的查詢。
        ///
        /// Every skipped copy is still found under its own group, in a Building atlas, without a warning. The record of
        /// warned lookups covers the whole load (map drawing included under quicktest), not only the lookups made here.
        /// </summary>
        [Test]
        public static void SkippedCopiesAreFoundInTheBuildingAtlasWithoutWarnings()
        {
            var skipped = StaticAtlasDeduplicator.DroppedCopies.ToList();
            Log.Message($"[FGL InGameTests] Looking up {skipped.Count} skipped static atlas copies.");

            var failures = new List<string>();
            foreach (var (texture, group) in skipped)
            {
                if (!GlobalTextureAtlasManager.TryGetStaticTile(group, texture, out var tile))
                {
                    failures.Add($"{texture.name} ({group}): not found");
                }
                else if (tile.atlas.groupKey.group is not TextureAtlasGroup.Building)
                {
                    failures.Add($"{texture.name} ({group}): found in {tile.atlas.groupKey}");
                }
            }
            var skippedSet = new HashSet<(Texture2D, TextureAtlasGroup)>(skipped);
            lock (CrossGroupLookupProbe.Warned)
            {
                failures.AddRange(CrossGroupLookupProbe.Warned
                    .Where(skippedSet.Contains)
                    .Select(static pair => $"{pair.texture.name} ({pair.group}): the lookup logged a warning"));
            }
            FglState.AssertNone(failures, "skipped atlas copies that did not resolve quietly to a Building atlas");
        }

        /// <summary>
        /// 開啟紋理壓縮時（原版預設），每張烘焙過的圖集與遮罩圖集都已是區塊壓縮格式，不論用哪種壓縮品質。
        /// 替換壓縮步驟的補丁若沒有真的壓縮，這裡會失敗。
        ///
        /// With texture compression on (vanilla's default), every baked atlas and mask atlas is block-compressed, whichever
        /// compression quality is used. A patch that replaces the compression step without compressing fails here.
        /// </summary>
        [Test]
        public static void BakedAtlasesAreCompressed()
        {
            if (!Prefs.TextureCompression) return;

            var failures = new List<string>();
            foreach (var atlas in BakedAtlases)
            {
                CheckCompressed(atlas.ColorTexture, atlas, "colour", failures);
                CheckCompressed(atlas.MaskTexture, atlas, "mask", failures);
            }
            FglState.AssertNone(failures, "baked static atlases left uncompressed");
        }

        private static void CheckCompressed(Texture2D texture, StaticTextureAtlas atlas, string which, List<string> failures)
        {
            // DXT 以 4×4 區塊編碼；邊長不是 4 的倍數的極小圖集不列入檢查。
            // DXT encodes 4x4 blocks; tiny atlases whose sides are not multiples of 4 are left out of this check.
            if (texture == null || texture.width % 4 != 0 || texture.height % 4 != 0) return;

            if (texture.format is not (TextureFormat.DXT1 or TextureFormat.DXT5 or TextureFormat.BC7))
            {
                failures.Add($"{atlas.groupKey} {which} {texture.width}x{texture.height}: {texture.format}");
            }
        }

        private static bool OnPixel(float uv, int size) => Math.Abs(uv * size - Mathf.Round(uv * size)) < 0.01f;

        private static void LogAtlasTotals(List<StaticTextureAtlas> baked)
        {
            long pixels = baked.Sum(static atlas => (long)atlas.ColorTexture.width * atlas.ColorTexture.height);
            long bytes = baked.Sum(static atlas => TextureBytes(atlas.ColorTexture) + TextureBytes(atlas.MaskTexture));
            Log.Message(string.Format(CultureInfo.InvariantCulture,
                "[FGL InGameTests] Static atlases this load: {0} baked, {1:F1} Mpx, {2:F0} MB. Trim {3}, skip duplicates {4} ({5} copies skipped), faster compression {6}.",
                baked.Count, pixels / 1e6, bytes / 1048576.0,
                FasterGameLoadingSettings.TrimStaticAtlases, FasterGameLoadingSettings.DeduplicateStaticAtlases,
                StaticAtlasDeduplicator.DroppedCopies.Count, FasterGameLoadingSettings.FastStaticAtlasCompression));
        }

        /// <summary>
        /// 紋理在顯示記憶體中的大小，包含所有 mipmap 層。
        /// A texture's size in video memory, every mip level included.
        /// </summary>
        private static long TextureBytes(Texture2D texture)
        {
            if (texture == null) return 0;

            long total = 0;
            for (int mip = 0; mip < Math.Max(1, texture.mipmapCount); mip++)
            {
                long width = Math.Max(1, texture.width >> mip);
                long height = Math.Max(1, texture.height >> mip);
                total += texture.format switch
                {
                    TextureFormat.DXT1 => (width + 3) / 4 * ((height + 3) / 4) * 8,
                    TextureFormat.DXT5 or TextureFormat.BC7 => (width + 3) / 4 * ((height + 3) / 4) * 16,
                    _ => width * height * 4,
                };
            }
            return total;
        }
    }

    /// <summary>
    /// 記錄原版 TryGetStaticTile 會記警告的每一次查詢：在要求的群組找不到、改在其他群組的圖集找到，且 ignoreFoundInOtherAtlas 未設定。
    /// 以（紋理, 群組）記錄，不比對警告文字：紋理名稱在不同 mod 之間經常重複。
    ///
    /// Records every lookup on which vanilla TryGetStaticTile logs its warning: nothing in the requested group, found in
    /// another group's atlas, and ignoreFoundInOtherAtlas not set. Records (texture, group) pairs instead of matching the
    /// warning's text, because texture names often repeat across mods.
    /// </summary>
    [HarmonyPatch(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.TryGetStaticTile))]
    internal static class CrossGroupLookupProbe
    {
        public static readonly HashSet<(Texture2D texture, TextureAtlasGroup group)> Warned = new();

        public static void Postfix(TextureAtlasGroup group, Texture2D texture, ref StaticTextureAtlasTile tile, bool ignoreFoundInOtherAtlas, bool __result)
        {
            if (__result && !ignoreFoundInOtherAtlas && tile?.atlas != null && tile.atlas.groupKey.group != group)
            {
                lock (Warned) Warned.Add((texture, group));
            }
        }
    }
}
