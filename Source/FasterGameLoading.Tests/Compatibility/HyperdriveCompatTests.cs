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

        public HyperdriveMod(ModContentPack content) : base(content)
        {
            var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.LoadModXML)));
            StoodDown = info != null && info.Prefixes.Any(static p => !string.Equals(p.owner, "vopaga.hyperdrive", StringComparison.Ordinal));
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
        private object originalRunningMods;
        private bool originalParallelizesModDefs;

        private static FieldInfo RunningModsField => AccessTools.Field(typeof(LoadedModManager), "runningMods");
        private static Dictionary<Type, Mod> RunningModClasses =>
            (Dictionary<Type, Mod>)AccessTools.Field(typeof(LoadedModManager), "runningModClasses").GetValue(null);
        private static MethodInfo LoadModXML => AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.LoadModXML));

        [SetUp]
        public void SetUp()
        {
            harmony = new Harmony(HarmonyId);
            originalRunningMods = RunningModsField.GetValue(null);
            originalParallelizesModDefs = HyperdriveCompat.ParallelizesModDefs;
            RunningModClasses.Remove(typeof(RimWorldHyperdrive.HyperdriveMod));
            RimWorldHyperdrive.HyperdriveMod.StoodDown = false;
        }

        [TearDown]
        public void TearDown()
        {
            harmony.UnpatchAll(HarmonyId);
            RunningModsField.SetValue(null, originalRunningMods);
            HyperdriveCompat.ParallelizesModDefs = originalParallelizesModDefs;
            RunningModClasses.Remove(typeof(RimWorldHyperdrive.HyperdriveMod));
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
        public void Detect_SetsParallelizesModDefsFromHyperdriveType()
        {
            HyperdriveCompat.Detect(typeof(RimWorldHyperdrive.HyperdriveMod));
            Assert.That(HyperdriveCompat.ParallelizesModDefs, Is.True);

            HyperdriveCompat.Detect(null);
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
        public void PatchLoadModXML_WhenHyperdriveConstructedTwice_PatchesOnlyOnce()
        {
            HyperdriveCompat.PatchLoadModXML(harmony, typeof(RimWorldHyperdrive.HyperdriveMod));

            _ = Activator.CreateInstance(typeof(RimWorldHyperdrive.HyperdriveMod), new object[] { null });
            _ = Activator.CreateInstance(typeof(RimWorldHyperdrive.HyperdriveMod), new object[] { null });

            Assert.That(Harmony.GetPatchInfo(LoadModXML).Prefixes.Count(static p => string.Equals(p.owner, HarmonyId, StringComparison.Ordinal)), Is.EqualTo(1));
        }
    }
}
