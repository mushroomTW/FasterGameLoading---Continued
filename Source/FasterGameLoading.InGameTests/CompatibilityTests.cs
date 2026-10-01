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
    /// 需以 companion_mods 帶入 HugsLib、Humanoid Alien Races、Ancot Library 等另跑一輪（見 README）。
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
        /// Hyperdrive 在自己的建構子檢查 LoadModXML 是否已有其他 mod 的 prefix，有就放棄跨 mod 平行解析。
        /// FGL 先建構時必須等 Hyperdrive 建構完才掛上閘門：兩者的 prefix 都要在 LoadModXML 上，且 FGL 的閘門排最前；
        /// Defs/ 交給 Hyperdrive 平行處理，FGL 不再於單一 mod 內平行解析，Patches/ 照常平行。
        /// </summary>
        [Test]
        public static void HyperdriveKeepsParallelModXmlLoading()
        {
            var hyperdrive = HyperdriveCompat.FindModType();
            if (hyperdrive == null) return;

            var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.LoadModXML)));
            var owners = info?.Prefixes
                .OrderByDescending(static p => p.priority)
                .Select(static p => p.owner)
                .ToList() ?? new List<string>();
            var deferred = FglState.HasFglPatch(AccessTools.Constructor(hyperdrive, new[] { typeof(ModContentPack) }));
            Log.Message($"[FGL InGameTests] Hyperdrive: LoadModXML prefixes = {string.Join(", ", owners)}; FGL gate {(deferred ? "deferred until after Hyperdrive's constructor" : "applied immediately")}.");

            Assert.That(owners.Contains("vopaga.hyperdrive")).Is.True();
            Assert.That(owners.FirstOrDefault()).Is.EqualTo(FglState.HarmonyId);
            Assert.That(HyperdriveCompat.ParallelizesModDefs).Is.True();

            if (!FasterGameLoadingSettings.EnableMultiThreading) return;
            var mod = LoadedModManager.RunningMods.First(static m => !ProtectedMods.ShouldSkipEarlyLoad(m));
            LoadableXmlAsset[] result = null;
            Assert.That(DirectXmlLoader_XmlAssetsInModFolder_Patch.Prefix(ref result, mod, "Defs/", null)).Is.True();
            Assert.That(DirectXmlLoader_XmlAssetsInModFolder_Patch.Prefix(ref result, mod, "Patches/", null)).Is.False();
        }

        /// <summary>HAR、Ayameduki、WRK 及依賴 HAR 的 mod 在排除名單內，內容只能由原版流程載入。</summary>
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
}
