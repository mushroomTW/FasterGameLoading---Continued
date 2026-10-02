using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimTestRedux;
using UnityEngine;
using Verse;

namespace FasterGameLoading.InGameTests
{
    /// <summary>
    /// 相容性分支只在對應 mod 啟用時才會走到；這些測試在 mod 未啟用時直接略過，
    /// 需以 companion_mods 帶入 HugsLib、Humanoid Alien Races、Ancot Library、ChezhouLib 等另跑一輪（見 README）。
    /// </summary>
    [TestSuite]
    internal static class CompatibilityTests
    {
        /// <summary>
        /// 開啟延遲圖形時，FGL 把 HugsLib 的 OnDefsLoaded 改到主執行緒、等長事件結束後才執行；
        /// 它仍必須執行（HugsLib 子 mod 的 DefsLoaded 靠它），而且只能在主執行緒。
        /// </summary>
        [Test]
        public static void HugsLibDefsLoadedRunsOnMainThread()
        {
            if (!HugsLibDefsLoadedProbe.HugsLibActive) return;

            if (FasterGameLoadingSettings.DelayGraphicLoading)
            {
                Assert.That(FglState.HasFglPatch(RedirectHugslibToMainThread.targetMethod)).Is.True();
            }
            lock (HugsLibDefsLoadedProbe.OffMainThreadCalls)
            {
                Log.Message($"[FGL InGameTests] HugsLib OnDefsLoaded ran {HugsLibDefsLoadedProbe.Calls} time(s), {HugsLibDefsLoadedProbe.OffMainThreadCalls.Count} off the main thread.");
                Assert.That(HugsLibDefsLoadedProbe.Calls).Is.GreaterThan(0);
                if (FasterGameLoadingSettings.DelayGraphicLoading)
                {
                    Assert.That(HugsLibDefsLoadedProbe.OffMainThreadCalls.Count).Is.EqualTo(0);
                }
            }
        }

        /// <summary>
        /// HAR 的 LoadGraphicsHook 以 ContentFinder 計算種族部件的貼圖變體數量，必須等所有 mod 的內容都載入後才執行，
        /// 否則尚未載入的種族 mod 變體數會被算成 0。FGL 以閘門延後它，HAR 與其衍生因此不必排除在提早載入之外。
        /// </summary>
        [Test]
        public static void HarGraphicsHookRunsAfterAllContentLoaded()
        {
            if (!HarGraphicsHookProbe.HarActive) return;

            Assert.That(FglState.HasFglPatch(AlienRaceGraphicsHookGate.TargetMethod())).Is.True();
            var har = LoadedModManager.RunningMods.First(static m => ModDependencyReflection.IsAlienRaces(m.PackageIdPlayerFacing));
            Assert.That(ProtectedMods.ShouldSkipEarlyLoad(har)).Is.False();

            List<string> missing;
            lock (HarGraphicsHookProbe.ModsMissingAtRun)
            {
                int round = TestRunDriver.Round;
                HarGraphicsHookProbe.Runs.TryGetValue(round, out int runs);
                missing = HarGraphicsHookProbe.ModsMissingAtRun.Where(e => e.round == round).Select(static e => e.packageId).ToList();
                bool harLoadedEarly;
                lock (ContentLoadProbe.LoadedBeforePatches)
                {
                    harLoadedEarly = ContentLoadProbe.EarlyLoadedMods.Contains(har);
                }
                Log.Message($"[FGL InGameTests] HAR LoadGraphicsHook counted variants {runs} time(s) this round; HAR content loaded early: {harLoadedEarly}.");
                // 語言重載後 HAR 的 Mod.Content 仍是舊的容器，原版 HAR 不會再計算變體，只在初次載入要求一定執行過。
                if (round == 1) Assert.That(runs).Is.GreaterThan(0);
            }
            FglState.AssertNone(missing, "mods whose content was not loaded when HAR counted graphic variants");
        }

        /// <summary>
        /// ChezhouLib 的 ReloadAll prefix 取代原方法並回傳 false，Harmony 會略過排在它之後、回傳 bool 的 prefix；
        /// FGL 防止同一個 handler 重複載入的 prefix 必須排在它前面才會生效。
        /// </summary>
        [Test]
        public static void AssetBundleReloadGuardRunsBeforeChezhouLib()
        {
            const string ChezhouLibHarmonyId = "ChezhouLib.lib";
            var target = AccessTools.Method(typeof(ModAssetBundlesHandler), "ReloadAll");
            var info = Harmony.GetPatchInfo(target);
            if (info == null || !info.Prefixes.Any(static p => p.owner == ChezhouLibHarmonyId)) return;

            var prefixes = info.Prefixes.ToArray();
            var owners = PatchProcessor.GetSortedPatchMethods(target, prefixes)
                .Select(m => prefixes.First(p => p.PatchMethod == m).owner)
                .ToList();
            Log.Message($"[FGL InGameTests] ReloadAll prefixes in run order: {string.Join(", ", owners)}.");

            var fglIndex = owners.IndexOf(FglState.HarmonyId);
            Assert.That(fglIndex >= 0 && fglIndex < owners.IndexOf(ChezhouLibHarmonyId)).Is.True();
        }

        /// <summary>
        /// Hyperdrive 在自己的建構子檢查 LoadModXML 是否已有其他 mod 的 prefix，有就放棄跨 mod 平行解析。
        /// FGL 先建構時必須等 Hyperdrive 建構完才掛上閘門：兩者的 prefix 都要在 LoadModXML 上，且 FGL 的閘門排最前；
        /// Defs/ 交給 Hyperdrive 平行處理，FGL 不再於單一 mod 內平行解析，Patches/ 照常平行。
        /// </summary>
        [Test]
        public static void HyperdriveKeepsParallelModXmlLoading()
        {
            var hyperdrive = HyperdriveCompat.FindModType();
            if (hyperdrive == null) return;

            var owners = FglState.LoadModXMLPrefixOwnersInRunOrder();
            var deferred = FglState.HasFglPatch(AccessTools.Constructor(hyperdrive, new[] { typeof(ModContentPack) }));
            Log.Message($"[FGL InGameTests] Hyperdrive: LoadModXML prefixes = {string.Join(", ", owners)}; FGL gate {(deferred ? "deferred until after Hyperdrive's constructor" : "applied immediately")}.");

            Assert.That(owners.Contains(HyperdriveCompat.PackageId)).Is.True();
            Assert.That(owners.FirstOrDefault()).Is.EqualTo(FglState.HarmonyId);

            // 讓出 Defs/ 只在 LoadModXML 期間生效；這裡以實際的 patch 狀態重跑一次偵測，確認 Hyperdrive 會被認出來。
            HyperdriveCompat.OnLoadModXMLStarting();
            try
            {
                Assert.That(HyperdriveCompat.ParallelizesModDefs).Is.True();

                if (!FasterGameLoadingSettings.EnableMultiThreading) return;
                var mod = LoadedModManager.RunningMods.First(static m => !ProtectedMods.ShouldSkipEarlyLoad(m));
                LoadableXmlAsset[] result = null;
                Assert.That(DirectXmlLoader_XmlAssetsInModFolder_Patch.Prefix(ref result, mod, "Defs/", null)).Is.True();
                Assert.That(DirectXmlLoader_XmlAssetsInModFolder_Patch.Prefix(ref result, mod, "Patches/", null)).Is.False();
            }
            finally
            {
                HyperdriveCompat.ParallelizesModDefs = false;
            }
        }

        /// <summary>
        /// Ayameduki、WRK（以及無法延後 HAR 變體掃描時的 HAR 與其衍生）在排除名單內，內容只能由原版流程載入。
        /// </summary>
        [Test]
        public static void SkipListedModsAreNotEarlyLoaded()
        {
            List<string> failures;
            lock (ContentLoadProbe.LoadedBeforePatches)
            {
                failures = ContentLoadProbe.EarlyLoadedMods
                    .Where(static m => ProtectedMods.ShouldSkipEarlyLoad(m))
                    .Select(static m => m.PackageIdPlayerFacing)
                    .ToList();
            }
            FglState.AssertNone(failures, "skip-listed mods loaded early");
        }

        /// <summary>
        /// HAR、Ancot 及其衍生 mod 的貼圖（多遮罩的身體部件）不能進靜態圖集；
        /// FGL 在載入時把它們登記到 LoadedTextureRegistry 的烘焙排除表，由 TryInsertStatic 的 prefix 擋下。
        /// </summary>
        [Test]
        public static void ProtectedModTexturesStayOutOfStaticAtlases()
        {
            if (!FasterGameLoadingSettings.StaticAtlasesBaking) return;

            var atlased = new HashSet<Texture2D>(GlobalTextureAtlasManager.staticTextureAtlases.SelectMany(static a => a.textures));
            var failures = new List<string>();
            int protectedMods = 0, checkedTextures = 0;
            foreach (var mod in LoadedModManager.RunningMods)
            {
                // 受保護貼圖刻意不進 FGL 的路徑表，改以 mod 根目錄判斷整個 mod 是否受保護。
                if (!ProtectedMods.IsProtectedTexturePath(mod.RootDir.Replace('\\', '/').TrimEnd('/') + "/Textures/probe.png")) continue;
                protectedMods++;

                foreach (var entry in mod.GetContentHolder<Texture2D>().contentList)
                {
                    var texture = entry.Value;
                    if (texture == null) continue;
                    checkedTextures++;

                    if (!LoadedTextureRegistry.IsSkippedForBaking(texture))
                    {
                        failures.Add($"{mod.PackageIdPlayerFacing}/{entry.Key}: not registered as skipped");
                    }
                    else if (atlased.Contains(texture))
                    {
                        failures.Add($"{mod.PackageIdPlayerFacing}/{entry.Key}: baked into a static atlas");
                    }
                }
            }
            // 未帶入 HAR／Ancot 等 mod 時這裡是 0，測試等同略過；log 讓相容性回合能確認真的檢查到貼圖。
            Log.Message($"[FGL InGameTests] Protected-texture check covered {checkedTextures} textures from {protectedMods} protected mods.");
            FglState.AssertNone(failures, "protected mod texture problems");
        }
    }

    /// <summary>
    /// 記錄 HugsLib 的 OnDefsLoaded 實際執行的次數與執行緒。掛在它第一個呼叫的 UtilityWorldObjectManager.OnDefsLoaded：
    /// 不掛 OnDefsLoaded 本身，FGL 以 prefix 攔下後改由 reverse patch 副本執行，原方法的 patch 不會觸發。
    /// </summary>
    [HarmonyPatch]
    internal static class HugsLibDefsLoadedProbe
    {
        public static int Calls;
        public static readonly List<string> OffMainThreadCalls = new List<string>();

        private static readonly MethodBase Target = AccessTools.Method("HugsLib.Utils.UtilityWorldObjectManager:OnDefsLoaded");

        public static bool HugsLibActive => Target != null && LoadedModManager.RunningMods.Any(static m => m.PackageId == "unlimitedhugs.hugslib");

        public static bool Prepare() => Target != null;

        public static MethodBase TargetMethod() => Target;

        public static void Prefix()
        {
            lock (OffMainThreadCalls)
            {
                Calls++;
                if (!UnityData.IsInMainThread) OffMainThreadCalls.Add(Thread.CurrentThread.Name ?? "unnamed thread");
            }
        }
    }

    /// <summary>
    /// 依輪次記錄 HAR 的 LoadGraphicsHook 真正計算變體（graphicsQueue 由非空變為空）的次數，以及當時還沒載入內容的 mod。
    /// 被 prefix 略過、或 HAR 自己的貼圖尚未載入而提早返回的呼叫都不算。
    /// </summary>
    [HarmonyPatch]
    internal static class HarGraphicsHookProbe
    {
        public static readonly Dictionary<int, int> Runs = new Dictionary<int, int>();
        public static readonly List<(int round, string packageId)> ModsMissingAtRun = new List<(int, string)>();

        private static readonly MethodBase Target = AccessTools.Method("AlienRace.AlienPartGenerator:LoadGraphicsHook");
        private static readonly FieldInfo GraphicsQueue = AccessTools.Field("AlienRace.AlienPartGenerator:graphicsQueue");

        public static bool HarActive => Target != null;

        public static bool Prepare() => Target != null && GraphicsQueue != null;

        public static MethodBase TargetMethod() => Target;

        public static void Prefix(out int __state) => __state = QueuedCount();

        public static void Postfix(bool __runOriginal, int __state)
        {
            if (!__runOriginal || __state == 0 || QueuedCount() != 0) return;
            lock (ModsMissingAtRun)
            {
                int round = TestRunDriver.Round;
                Runs.TryGetValue(round, out int runs);
                Runs[round] = runs + 1;
                foreach (var mod in LoadedModManager.RunningMods)
                {
                    if (!ModContentPack_ReloadContentInt_Patch.loadedMods.Contains(mod)) ModsMissingAtRun.Add((round, mod.PackageIdPlayerFacing));
                }
            }
        }

        private static int QueuedCount() => (GraphicsQueue.GetValue(null) as System.Collections.IEnumerable)?.Cast<object>().Count() ?? 0;
    }
}
