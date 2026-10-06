using System.Collections.Generic;
using HarmonyLib;
using RimTestRedux;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;

namespace FasterGameLoading.InGameTests
{
    /// <summary>
    /// 在真實執行環境確認 Harmony patch 依設定正確套用／解除。
    /// 單元測試只能驗證轉譯器產生的 IL，無法確認 PatchAll 與 Prepare() 在實際啟動時的結果。
    /// </summary>
    [TestSuite]
    internal static class PatchApplicationTests
    {
        [Test]
        public static void AlwaysOnPatchesAreApplied()
        {
            var failures = new List<string>();
            Check(failures, AccessTools.Method(typeof(StaticConstructorOnStartupUtility), nameof(StaticConstructorOnStartupUtility.CallAll)), expected: true);
            Check(failures, AccessTools.Method(typeof(ModContentPack), nameof(ModContentPack.ReloadContentInt)), expected: true);
            Check(failures, AccessTools.Method(typeof(ModAssetBundlesHandler), nameof(ModAssetBundlesHandler.ReloadAll)), expected: true);
            Check(failures, AccessTools.Method(typeof(World), nameof(World.FinalizeInit)), expected: true);
            Check(failures, AccessTools.Method(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.BakeStaticAtlases)), expected: true);
            Check(failures, AccessTools.Method(typeof(SubSoundDef), nameof(SubSoundDef.ResolveReferences)), expected: true);
            FglState.AssertNone(failures, "patch state mismatches");
        }

        [Test]
        public static void TypeLookupCachePatchFollowsSetting()
        {
            var failures = new List<string>();
            Check(failures, AccessTools.Method(typeof(GenTypes), nameof(GenTypes.GetTypeInAnyAssemblyInt)), FasterGameLoadingSettings.TypeLookupCache);
            FglState.AssertNone(failures, "patch state mismatches");
        }

        [Test]
        public static void DelayGraphicPatchesFollowSetting()
        {
            bool expected = FasterGameLoadingSettings.DelayGraphicLoading;
            var failures = new List<string>();
            Check(failures, AccessTools.Method(typeof(ThingDef), nameof(ThingDef.PostLoad)), expected);
            Check(failures, AccessTools.Method(typeof(BuildableDef), nameof(BuildableDef.PostLoad)), expected);
            Check(failures, AccessTools.Method(typeof(GraphicData), nameof(GraphicData.Init)), expected);
            FglState.AssertNone(failures, "patch state mismatches");
        }

        /// <summary>音效解析完成後，SoundStarter 類別的攔截必須全部解除，否則整個 session 沒有聲音。</summary>
        [Test]
        public static void SoundStarterInterceptionIsRemoved()
        {
            var failures = new List<string>();
            Check(failures, AccessTools.Method(typeof(SoundStarter), nameof(SoundStarter.PlayOneShotOnCamera)), expected: false);
            Check(failures, AccessTools.Method(typeof(SoundStarter), nameof(SoundStarter.PlayOneShot)), expected: false);
            Check(failures, AccessTools.Method(typeof(SoundStarter), nameof(SoundStarter.TrySpawnSustainer)), expected: false);
            Check(failures, AccessTools.Method(typeof(SubSoundDef), nameof(SubSoundDef.TryPlay)), expected: false);
            FglState.AssertNone(failures, "patch state mismatches");
        }

        private static void Check(List<string> failures, System.Reflection.MethodBase method, bool expected)
        {
            if (method == null)
            {
                failures.Add("target method not found (game API changed?)");
                return;
            }
            if (FglState.HasFglPatch(method) != expected)
            {
                failures.Add($"{method.DeclaringType?.Name}.{method.Name} expected {(expected ? "patched" : "unpatched")}");
            }
        }
    }

    /// <summary>延遲的 SubSoundDef 解析是否真的全部完成。</summary>
    [TestSuite]
    internal static class DeferredSoundTests
    {
        [Test]
        public static void SubSoundQueueIsDrained()
        {
            Assert.That(FasterGameLoadingMod.delayedActions.SubSoundDefToResolveCount).Is.EqualTo(0);
        }

        /// <summary>
        /// 延遲解析尚未跑完時，播放請求要當場解析該 SubSoundDef 再交回原版播放（主選單 UI 音效靠這條路徑）。
        /// 重建情境：重新套上 SoundStarter 攔截、讓主選單點擊音效經 FGL 的轉譯器重新排入延遲佇列，再對它發出播放請求。
        /// </summary>
        [Test]
        public static void PlayRequestResolvesQueuedSubSoundOnDemand()
        {
            var delayedActions = FasterGameLoadingMod.delayedActions;
            var tryPlay = AccessTools.Method(typeof(SubSoundDef), nameof(SubSoundDef.TryPlay));
            var sub = SoundDefOf.Click.subSounds[0];
            int expectedGrains = sub.resolvedGrains.Count;
            Assert.That(expectedGrains).Is.GreaterThan(0);
            Assert.That(delayedActions.SubSoundDefToResolveCount).Is.EqualTo(0);

            try
            {
                sub.resolvedGrains.Clear();
                SoundStarter_Patch.ResetUnpatchedStatus();
                // PatchCategory(string) 取的是呼叫端組件（本測試 mod），必須明確指定 FGL 的組件。
                FasterGameLoadingMod.harmony.PatchCategory(typeof(FasterGameLoadingMod).Assembly, "SoundStarter");
                sub.ResolveReferences();
                Assert.That(delayedActions.SubSoundDefToResolveCount).Is.EqualTo(1);
                Assert.That(sub.resolvedGrains.Count).Is.EqualTo(0);

                sub.TryPlay(SoundInfo.OnCamera());

                Assert.That(sub.resolvedGrains.Count).Is.EqualTo(expectedGrains);
                Assert.That(delayedActions.SubSoundDefToResolveCount).Is.EqualTo(0);
                // 其餘 SubSoundDef 尚未全部解析前，攔截要保留。
                Assert.That(FglState.HasFglPatch(tryPlay)).Is.True();
            }
            finally
            {
                // 失敗時也不能讓整個 session 留著攔截，或留下未解析的 SubSoundDef。
                delayedActions.ResolvePendingSubSounds();
            }
        }

        /// <summary>
        /// 原版在回呼中把 grains 展開成 resolvedGrains；延遲後漏跑的 SubSoundDef 會保持空清單而無聲。
        /// 只檢查 grains 能解析出實體音檔的項目，缺檔屬於內容問題，原版同樣會報錯。
        /// </summary>
        [Test]
        public static void EverySubSoundWithGrainsIsResolved()
        {
            var failures = new List<string>();
            foreach (var soundDef in DefDatabase<SoundDef>.AllDefsListForReading)
            {
                if (soundDef.subSounds == null) continue;
                foreach (var sub in soundDef.subSounds)
                {
                    if (sub.resolvedGrains.Count > 0 || sub.grains.NullOrEmpty()) continue;
                    if (!AnyGrainResolvable(sub)) continue;
                    failures.Add($"{soundDef.defName}/{sub}");
                }
            }
            FglState.AssertNone(failures, "SubSoundDefs left unresolved");
        }

        private static bool AnyGrainResolvable(SubSoundDef sub)
        {
            foreach (var grain in sub.grains)
            {
                foreach (var _ in grain.GetResolvedGrains())
                {
                    return true;
                }
            }
            return false;
        }
    }
}
