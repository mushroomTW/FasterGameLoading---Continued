using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimTestRedux;
using RimWorld;
using UnityEngine;
using Verse;

namespace FasterGameLoading.InGameTests
{
    /// <summary>
    /// 延遲圖形／圖示管線跑完後，每個 Def 的視覺狀態要與原版一致。
    /// 預設設定下這些測試驗證原版基準；開啟延遲圖形載入時驗證 FGL 沒有漏載或配錯圖形。
    /// </summary>
    [TestSuite]
    internal static class DeferredGraphicsTests
    {
        [Test]
        public static void DeferredQueuesAreDrained()
        {
            var delayedActions = FasterGameLoadingMod.delayedActions;
            Assert.That(delayedActions.Phase == DeferredPhase.Completed).Is.True();
            Assert.That(delayedActions.GraphicsToLoadCount).Is.EqualTo(0);
            Assert.That(delayedActions.IconsToLoadCount).Is.EqualTo(0);
        }

        /// <summary>原版 PostLoad 回呼把 graphicData.Graphic 指給 ThingDef.graphic；漏跑的 Def 會停在 BadGraphic。</summary>
        [Test]
        public static void EveryThingDefWithGraphicDataHasGraphic()
        {
            var failures = new List<string>();
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def.graphicData != null && (def.graphic == null || def.graphic == BaseContent.BadGraphic))
                {
                    failures.Add(def.defName);
                }
            }
            FglState.AssertNone(failures, "ThingDefs with graphicData but no graphic");
        }

        /// <summary>
        /// GraphicData_Init_Patch 會讓參數相同的 GraphicData 共用同一個 Graphic；原版的 GraphicRequest 鍵含 graphicData 參考，
        /// 本來不會跨 Def 共用，因此不能要求同一個實例。改以相同參數向 GraphicDatabase 取得原版會產生的 Graphic，
        /// 要求兩者在繪製上等價；不等價就代表快取把別的 Def 的圖形配給了這個 Def。
        /// 以原版結果為準而不直接比 GraphicData：GraphicDatabase 會量化顏色，Graphic_PawnBodySilhouette 等類別也會自行換 shader。
        /// 查詢會在 GraphicDatabase 留下對照用的 Graphic，只影響測試 session。
        /// 只檢查 cachedGraphic 仍是 GraphicData.Init 產生的那一個的 Def：有些 mod（例如 Bionic Icons）繞過 Init，
        /// 以 graphicData 表達不出的參數（mask）直接寫入 cachedGraphic，這些不經過 FGL 的共用快取，也無法從 graphicData 重建。
        /// </summary>
        [Test]
        public static void CachedGraphicsMatchWhatVanillaWouldProduce()
        {
            var failures = new List<string>();
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                var data = def.graphicData;
                if (data?.cachedGraphic == null || !GraphicDataInitProbe.CameFromInit(data)) continue;
                var graphic = Unwrap(data.cachedGraphic);
                if (graphic == BaseContent.BadGraphic || data.graphicClass == null) continue;

                var shader = (data.shaderType ?? ShaderTypeDefOf.Cutout).Shader;
                var expected = GraphicDatabase.Get(data.graphicClass, data.texPath, shader, data.drawSize, data.color, data.colorTwo, data, data.shaderParameters, data.maskPath);
                if (!IsEquivalent(graphic, expected))
                {
                    failures.Add($"{def.defName}: got {Describe(graphic)}, expected {Describe(expected)}");
                }
            }
            FglState.AssertNone(failures, "cached graphics differing from vanilla GraphicDatabase results");
        }

        /// <summary>
        /// 依原版 ThingDef.ResolveIcon 的條件，應該拿到圖示的 Def 不能停在 BadTex。
        /// 延遲圖示佇列若漏跑或在圖形載入前就跑，這裡會抓到。
        /// </summary>
        [Test]
        public static void ThingDefsThatShouldHaveIconsHaveThem()
        {
            var failures = new List<string>();
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def.uiIcon == BaseContent.BadTex && ShouldHaveIcon(def))
                {
                    failures.Add(def.defName);
                }
            }
            FglState.AssertNone(failures, "ThingDefs left with BadTex icon");
        }

        /// <summary>非 ThingDef 的 BuildableDef（地形等）只看明確指定的 uiIconPath。</summary>
        [Test]
        public static void TerrainIconsFromExplicitPathsAreLoaded()
        {
            var failures = new List<string>();
            foreach (var def in DefDatabase<TerrainDef>.AllDefsListForReading)
            {
                if (def.uiIcon == BaseContent.BadTex && !def.uiIconPath.NullOrEmpty()
                    && ContentFinder<Texture2D>.Get(def.uiIconPath, reportFailure: false) != null)
                {
                    failures.Add(def.defName);
                }
            }
            FglState.AssertNone(failures, "TerrainDefs left with BadTex icon");
        }

        private static bool IsEquivalent(Graphic a, Graphic b)
        {
            if (ReferenceEquals(a, b)) return true;
            return b != null
                && a.GetType() == b.GetType()
                && string.Equals(a.path, b.path, System.StringComparison.Ordinal)
                && string.Equals(a.maskPath, b.maskPath, System.StringComparison.Ordinal)
                && a.Shader == b.Shader
                && a.drawSize == b.drawSize
                && ((Color32)a.color).Equals((Color32)b.color)
                && ((Color32)a.colorTwo).Equals((Color32)b.colorTwo);
        }

        private static string Describe(Graphic g)
            => g == null ? "null" : $"{g.GetType().Name} '{g.path}' mask '{g.maskPath}' {g.Shader?.name} {g.drawSize} {g.color}/{g.colorTwo}";

        /// <summary>GraphicData.Init 會在 GraphicDatabase 的結果外包 RandomRotated／Linked；鍵值要比對最內層。</summary>
        private static Graphic Unwrap(Graphic graphic)
        {
            while (true)
            {
                switch (graphic)
                {
                    case Graphic_Linked linked:
                        graphic = linked.subGraphic;
                        break;
                    case Graphic_RandomRotated rotated:
                        graphic = rotated.subGraphic;
                        break;
                    default:
                        return graphic;
                }
            }
        }

        private static bool ShouldHaveIcon(ThingDef def)
        {
            if (!def.uiIconPath.NullOrEmpty())
            {
                return ContentFinder<Texture2D>.Get(def.uiIconPath, reportFailure: false) != null;
            }
            if (def.category == ThingCategory.Pawn)
            {
                return def.race != null && !def.race.Humanlike && def.race.AnyPawnKind != null;
            }
            // 圖形本身就是 BadTexture（例如 AOBA Framework 的除錯工具刻意以它為 texPath），圖示也只會是 BadTex。
            // 比對 texPath 而不讀 MatSingle：部分 mod 的 Graphic 子類別只支援 MatAt，讀 MatSingle 會拋例外。
            if (string.Equals(def.graphicData?.texPath, BaseContent.BadTexPath, StringComparison.Ordinal)) return false;
            // 原版 BuildableDef.ResolveIcon 的前置條件：有可用圖形、且不是 mote。
            return def.graphic != null && def.graphic != BaseContent.BadGraphic && def.mote == null;
        }
    }

    /// <summary>
    /// 記下每個 GraphicData 最近一次 Init 後的 cachedGraphic。FGL 的 prefix 共用快取時會略過原方法，postfix 仍會執行，兩種路徑都記得到。
    /// 本測試 mod 在 FGL 之前建構，這個 patch 早於任何 Def 的 Init。
    /// </summary>
    [HarmonyPatch(typeof(GraphicData), nameof(GraphicData.Init))]
    internal static class GraphicDataInitProbe
    {
        private static readonly ConditionalWeakTable<GraphicData, Graphic> InitResults = new ConditionalWeakTable<GraphicData, Graphic>();

        public static bool CameFromInit(GraphicData data)
            => InitResults.TryGetValue(data, out var graphic) && ReferenceEquals(graphic, data.cachedGraphic);

        [HarmonyPriority(Priority.Last)]
        public static void Postfix(GraphicData __instance)
        {
            // ConditionalWeakTable 在 .NET Framework 沒有 AddOrUpdate；重複 Init 時先移除舊值。
            lock (InitResults)
            {
                InitResults.Remove(__instance);
                InitResults.Add(__instance, __instance.cachedGraphic);
            }
        }
    }
}
