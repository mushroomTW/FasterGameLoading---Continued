using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using DevTools.Testing;
using RimWorld.Planet;
using Verse;
using Verse.Sound;

namespace FasterGameLoading.InGameTests
{
    /// <summary>
    /// 進入地圖相關的路徑：World.FinalizeInit 會把尚未解析的 SubSoundDef 全部解析完。
    /// 需要地圖的測試只在 quicktest 回合執行，主選單回合直接略過。
    /// </summary>
    [TestFixture(TestType.Playing)]
    internal class MapLoadTests
    {
        public MapLoadTests() { }

        private static bool Playing => Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null;

        [Test]
        public static void WorldFinalizeInitRan()
        {
            if (!FglState.Quicktest) return;
            Expect.GreaterThan(WorldFinalizeInitProbe.Calls, 0, "World.FinalizeInit was not observed in quicktest");
            Log.Message($"[FGL InGameTests] World.FinalizeInit ran with {WorldFinalizeInitProbe.QueuedSubSoundsAtInit} SubSoundDefs still queued; deferred pipeline finished at that point: {WorldFinalizeInitProbe.PipelineFinishedAtInit}");
        }

        /// <summary>
        /// 延遲管線通常在 World.FinalizeInit 之前就跑完（quicktest 實測佇列已空），補解析的路徑不會自然走到。
        /// 這裡重建那個情境：重新套上 SoundStarter 攔截，讓一個真實 SubSoundDef 經 FGL 的轉譯器重新排入延遲佇列，
        /// 再呼叫 FGL 的 postfix，確認它解析完畢並解除攔截。沒有長事件時 ExecuteWhenFinished 會立即執行。
        /// </summary>
        [Test]
        public static void FinalizeInitResolvesQueuedSubSoundsAndReleasesSound()
        {
            var delayedActions = FasterGameLoadingMod.delayedActions;
            var tryPlay = AccessTools.Method(typeof(SubSoundDef), nameof(SubSoundDef.TryPlay));
            var sub = DefDatabase<SoundDef>.AllDefsListForReading
                .Where(static s => s.subSounds != null)
                .SelectMany(static s => s.subSounds)
                .FirstOrDefault(static s => s.resolvedGrains.Count > 0);
            // 沒有任何已解析的 SubSoundDef 代表延遲音效解析本身壞了（EverySubSoundWithGrainsIsResolved 會列出），這裡明確指出而非拋 LINQ 例外。
            Expect.IsNotNull(sub, "no resolved SubSoundDef was available for the World.FinalizeInit path test");
            int expectedGrains = sub.resolvedGrains.Count;
            Expect.AreEqual(delayedActions.SubSoundDefToResolveCount, 0, "sub-sound queue was not empty before the path test");

            try
            {
                sub.resolvedGrains.Clear();
                SoundStarter_Patch.ResetUnpatchedStatus();
                // PatchCategory(string) 取的是呼叫端組件（本測試 mod），必須明確指定 FGL 的組件。
                FasterGameLoadingMod.harmony.PatchCategory(typeof(FasterGameLoadingMod).Assembly, "SoundStarter");
                sub.ResolveReferences();

                Expect.AreEqual(delayedActions.SubSoundDefToResolveCount, 1, "resolving the SubSoundDef did not queue exactly one deferred action");
                Expect.AreEqual(sub.resolvedGrains.Count, 0, "SubSoundDef resolved before World.FinalizeInit");
                Expect.IsTrue(FglState.HasFglPatch(tryPlay), "SoundStarter interception was not applied while sounds were queued");

                World_FinalizeInit_Patch.Postfix();

                Expect.AreEqual(delayedActions.SubSoundDefToResolveCount, 0, "World.FinalizeInit did not drain the sub-sound queue");
                Expect.AreEqual(sub.resolvedGrains.Count, expectedGrains, "World.FinalizeInit did not restore the resolved sound grains");
                Expect.IsFalse(FglState.HasFglPatch(tryPlay), "SoundStarter interception remained after the sub-sound queue drained");
            }
            finally
            {
                // 失敗時也不能讓整個 session 保持無聲，或留下未解析的 SubSoundDef。
                delayedActions.ResolvePendingSubSounds();
            }
        }

        // 地圖上每個有 graphicData 的物件都要拿得到真正的圖形與貼圖，不能是原版的錯誤圖形。
        [Test]
        public static void EverySpawnedThingHasItsGraphic()
        {
            if (!Playing) return;

            var map = Find.CurrentMap;
            var failures = new List<string>();
            Thing fixtureThing = null;
            try
            {
                if (!map.listerThings.AllThings.Any(static thing => thing.def.graphicData != null && thing is not Pawn))
                {
                    var steelDef = DefDatabase<ThingDef>.GetNamed("Steel");
                    var spawnCell = IntVec3.Invalid;
                    foreach (var cell in map.AllCells)
                    {
                        if (map.thingGrid.ThingsListAtFast(cell).Count == 0 &&
                            GenSpawn.CanSpawnAt(steelDef, cell, map, Rot4.North, canWipeEdifices: false))
                        {
                            spawnCell = cell;
                            break;
                        }
                    }

                    if (!spawnCell.IsValid)
                    {
                        Expect.IsTrue(false, "map has no graphic-bearing non-pawn Things and no empty cell for the Steel fixture");
                        return;
                    }

                    fixtureThing = ThingMaker.MakeThing(steelDef);
                    if (GenSpawn.Spawn(fixtureThing, spawnCell, map, Rot4.North, WipeMode.Vanish) == null)
                    {
                        Expect.IsTrue(false, "failed to spawn the Steel fixture for map graphic validation");
                        return;
                    }
                }

                int inspectedThingCount = 0;
                foreach (var thing in map.listerThings.AllThings)
                {
                    // Pawn 由 PawnRenderer 繪製，Graphic 不代表實際外觀。
                    if (thing.def.graphicData == null || thing is Pawn) continue;
                    inspectedThingCount++;
                    try
                    {
                        var graphic = thing.Graphic;
                        if (graphic == null || graphic == BaseContent.BadGraphic)
                        {
                            failures.Add($"{thing.def.defName}: bad graphic");
                            continue;
                        }
                        var texture = graphic.MatSingle?.mainTexture;
                        if (texture == null || texture == BaseContent.BadTex)
                        {
                            failures.Add($"{thing.def.defName}: missing texture ({graphic.GetType().Name} {graphic.path})");
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{thing.def.defName}: {ex.GetType().Name} {ex.Message}");
                    }
                }

                Expect.GreaterThan(inspectedThingCount, 0, "map contains no spawned non-pawn Things with graphic data to inspect");
                FglState.AssertNone(failures, "spawned things without a usable graphic");
            }
            finally
            {
                fixtureThing?.Destroy(DestroyMode.Vanish);
            }
        }
    }

    // 記錄 World.FinalizeInit 執行當下 FGL 延遲管線的狀態，用來說明本次 quicktest 是否真的走到音效補解析。
    [HarmonyPatch(typeof(World), nameof(World.FinalizeInit))]
    internal static class WorldFinalizeInitProbe
    {
        public static int Calls;
        public static int QueuedSubSoundsAtInit;
        public static bool PipelineFinishedAtInit;

        [HarmonyPriority(HarmonyLib.Priority.First)]
        public static void Prefix()
        {
            Calls++;
            QueuedSubSoundsAtInit = FasterGameLoadingMod.delayedActions.SubSoundDefToResolveCount;
            PipelineFinishedAtInit = FglState.DeferredPipelineFinished(TestRunDriver.Round);
        }
    }
}
