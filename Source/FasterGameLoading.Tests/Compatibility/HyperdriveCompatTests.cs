using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;
using Verse;

namespace RimWorldHyperdrive
{
    /// <summary>
    /// 模擬 Hyperdrive 的 Mod 類別（命名空間與類別名稱與正版相同）：
    /// 建構子裡 LoadModXML 已有其他 owner 的 prefix 時放棄跨 mod 平行解析。
    /// </summary>
    public class HyperdriveMod : Mod
    {
        internal static bool StoodDown { get; set; }
        internal static bool ThrowInConstructor { get; set; }

        public HyperdriveMod(ModContentPack content) : base(content)
        {
            var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.LoadModXML)));
            StoodDown = info != null && info.Prefixes.Any(static p => !string.Equals(p.owner, "vopaga.hyperdrive", StringComparison.Ordinal));
            if (ThrowInConstructor) throw new InvalidOperationException("simulated Hyperdrive constructor failure");
        }

        /// <summary>模擬 Hyperdrive 的 LoadModXML prefix：記下當下 FGL 是否讓出 Defs/，並取代原方法。</summary>
        internal static bool? ParallelizesModDefsDuringLoad { get; set; }

        public static bool LoadModXMLPrefix()
        {
            ParallelizesModDefsDuringLoad = FasterGameLoading.HyperdriveCompat.ParallelizesModDefs;
            return false;
        }
    }
}

namespace FasterGameLoading.Tests.Compatibility
{
    [TestFixture]
    public class HyperdriveCompatTests
    {
        private const string HarmonyId = "FasterGameLoading.Tests.HyperdriveCompatTests";
        private Harmony harmony;
        private Harmony hyperdriveHarmony;
        private object originalRunningMods;
        private bool originalParallelizesModDefs;
        private bool originalModClassesCreated;

        private static FieldInfo RunningModsField => AccessTools.Field(typeof(LoadedModManager), "runningMods");
        private static Dictionary<Type, Mod> RunningModClasses =>
            (Dictionary<Type, Mod>)AccessTools.Field(typeof(LoadedModManager), "runningModClasses").GetValue(null);
        private static MethodInfo LoadModXML => AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.LoadModXML));

        [SetUp]
        public void SetUp()
        {
            harmony = new Harmony(HarmonyId);
            hyperdriveHarmony = new Harmony(HyperdriveCompat.PackageId);
            originalRunningMods = RunningModsField.GetValue(null);
            originalParallelizesModDefs = HyperdriveCompat.ParallelizesModDefs;
            originalModClassesCreated = EarlyModContentLoader.ModClassesCreated;
            RunningModClasses.Remove(typeof(RimWorldHyperdrive.HyperdriveMod));
            RimWorldHyperdrive.HyperdriveMod.StoodDown = false;
            RimWorldHyperdrive.HyperdriveMod.ThrowInConstructor = false;
            RimWorldHyperdrive.HyperdriveMod.ParallelizesModDefsDuringLoad = null;
        }

        [TearDown]
        public void TearDown()
        {
            harmony.UnpatchAll(HarmonyId);
            hyperdriveHarmony.UnpatchAll(HyperdriveCompat.PackageId);
            RunningModsField.SetValue(null, originalRunningMods);
            HyperdriveCompat.ParallelizesModDefs = originalParallelizesModDefs;
            EarlyModContentLoader.ModClassesCreated = originalModClassesCreated;
            RunningModClasses.Remove(typeof(RimWorldHyperdrive.HyperdriveMod));
            RimWorldHyperdrive.HyperdriveMod.ThrowInConstructor = false;
        }

        private static bool HasFglPrefixOnLoadModXML()
        {
            return Harmony.GetPatchInfo(LoadModXML)?.Prefixes.Any(static p => string.Equals(p.owner, HarmonyId, StringComparison.Ordinal)) is true;
        }

        private static ModContentPack CreateMod(string packageId, params Assembly[] assemblies)
        {
            var mod = (ModContentPack)FormatterServices.GetUninitializedObject(typeof(ModContentPack));
            AccessTools.Field(typeof(ModContentPack), "packageIdPlayerFacingInt").SetValue(mod, packageId);
            var handler = (ModAssemblyHandler)FormatterServices.GetUninitializedObject(typeof(ModAssemblyHandler));
            handler.loadedAssemblies = assemblies.ToList();
            mod.assemblies = handler;
            return mod;
        }

        [Test]
        public void FindModType_WhenHyperdriveIsRunning_ReturnsItsModType()
        {
            RunningModsField.SetValue(null, new List<ModContentPack>
            {
                CreateMod("other.mod", typeof(HyperdriveCompatTests).Assembly),
                CreateMod("vopaga.hyperdrive", typeof(HyperdriveCompatTests).Assembly),
            });

            Assert.That(HyperdriveCompat.FindModType(), Is.EqualTo(typeof(RimWorldHyperdrive.HyperdriveMod)));
        }

        [Test]
        public void FindModType_WhenHyperdriveIsNotRunning_ReturnsNull()
        {
            // 即使組件裡有同名類別，packageId 不符就不算啟用。
            RunningModsField.SetValue(null, new List<ModContentPack>
            {
                CreateMod("other.mod", typeof(HyperdriveCompatTests).Assembly),
            });

            Assert.That(HyperdriveCompat.FindModType(), Is.Null);
        }

        [Test]
        public void OnLoadModXMLStarting_WhenHyperdriveStoodDown_KeepsDefsParallel()
        {
            // Hyperdrive 已啟用但放棄跨 mod 平行解析時，LoadModXML 上沒有它的 prefix，FGL 不能讓出 Defs/。
            HyperdriveCompat.ParallelizesModDefs = true;

            HyperdriveCompat.OnLoadModXMLStarting();

            Assert.That(HyperdriveCompat.ParallelizesModDefs, Is.False);
        }

        [Test]
        public void LoadModXML_WithHyperdrivePrefix_LeavesDefsOnlyDuringTheCall()
        {
            RunningModsField.SetValue(null, new List<ModContentPack>());
            HyperdriveCompat.PatchLoadModXML(harmony, hyperdriveModType: null);
            hyperdriveHarmony.Patch(LoadModXML, prefix: new HarmonyMethod(typeof(RimWorldHyperdrive.HyperdriveMod), nameof(RimWorldHyperdrive.HyperdriveMod.LoadModXMLPrefix)));

            _ = LoadedModManager.LoadModXML();

            Assert.That(RimWorldHyperdrive.HyperdriveMod.ParallelizesModDefsDuringLoad, Is.True);
            Assert.That(HyperdriveCompat.ParallelizesModDefs, Is.False);
        }

        [Test]
        public void PatchLoadModXML_WithoutHyperdrive_PatchesImmediatelyWithFirstPriority()
        {
            HyperdriveCompat.PatchLoadModXML(harmony, hyperdriveModType: null);

            var prefix = Harmony.GetPatchInfo(LoadModXML).Prefixes.Single(static p => string.Equals(p.owner, HarmonyId, StringComparison.Ordinal));
            Assert.That(prefix.priority, Is.EqualTo(Priority.First));
        }

        [Test]
        public void PatchLoadModXML_WhenHyperdriveAlreadyConstructed_PatchesImmediately()
        {
            RunningModClasses[typeof(RimWorldHyperdrive.HyperdriveMod)] =
                (Mod)FormatterServices.GetUninitializedObject(typeof(RimWorldHyperdrive.HyperdriveMod));

            HyperdriveCompat.PatchLoadModXML(harmony, typeof(RimWorldHyperdrive.HyperdriveMod));

            Assert.That(HasFglPrefixOnLoadModXML(), Is.True);
        }

        [Test]
        public void PatchLoadModXML_WhenHyperdriveNotYetConstructed_DefersUntilAfterItsConstructor()
        {
            // FGL 的建構子比 Hyperdrive 先執行：Hyperdrive 檢查時不得看到 FGL 的 prefix，建構完成後 FGL 的 prefix 必須就位。
            HyperdriveCompat.PatchLoadModXML(harmony, typeof(RimWorldHyperdrive.HyperdriveMod));
            Assert.That(HasFglPrefixOnLoadModXML(), Is.False);

            _ = Activator.CreateInstance(typeof(RimWorldHyperdrive.HyperdriveMod), new object[] { null });

            Assert.That(RimWorldHyperdrive.HyperdriveMod.StoodDown, Is.False);
            Assert.That(HasFglPrefixOnLoadModXML(), Is.True);
        }

        [Test]
        public void PatchLoadModXML_WhenHyperdriveConstructorThrows_StillPatches()
        {
            // CreateModClasses 會吞掉建構子的例外繼續執行；閘門仍須套用，否則提早載入整個 session 失效。
            HyperdriveCompat.PatchLoadModXML(harmony, typeof(RimWorldHyperdrive.HyperdriveMod));
            RimWorldHyperdrive.HyperdriveMod.ThrowInConstructor = true;

            Assert.Throws<TargetInvocationException>(static () => Activator.CreateInstance(typeof(RimWorldHyperdrive.HyperdriveMod), new object[] { null }));

            Assert.That(HasFglPrefixOnLoadModXML(), Is.True);
        }

        [Test]
        public void PatchLoadModXML_WhenHyperdriveConstructedTwice_PatchesOnlyOnce()
        {
            HyperdriveCompat.PatchLoadModXML(harmony, typeof(RimWorldHyperdrive.HyperdriveMod));

            _ = Activator.CreateInstance(typeof(RimWorldHyperdrive.HyperdriveMod), new object[] { null });
            _ = Activator.CreateInstance(typeof(RimWorldHyperdrive.HyperdriveMod), new object[] { null });

            Assert.That(Harmony.GetPatchInfo(LoadModXML).Prefixes.Count(static p => string.Equals(p.owner, HarmonyId, StringComparison.Ordinal)), Is.EqualTo(1));
        }
    }
}
