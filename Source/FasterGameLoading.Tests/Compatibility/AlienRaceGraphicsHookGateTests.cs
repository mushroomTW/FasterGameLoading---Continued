using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;
using Verse;

namespace FasterGameLoading.Tests.Compatibility
{
    [TestFixture]
    public class AlienRaceGraphicsHookGateTests
    {
        private static readonly FieldInfo releasedField = AccessTools.Field(typeof(AlienRaceGraphicsHookGate), "released");
        private static readonly FieldInfo runningModsField = AccessTools.Field(typeof(LoadedModManager), "runningMods");

        private object originalRunningMods;
        private readonly List<ModContentPack> addedToLoadedMods = new List<ModContentPack>();

        // 只重設閘門自己的狀態，不 Raise 全域生命週期事件，避免清掉其他類別的快取。
        [SetUp]
        public void SetUp()
        {
            releasedField.SetValue(null, false);
            originalRunningMods = runningModsField.GetValue(null);
        }

        [TearDown]
        public void TearDown()
        {
            releasedField.SetValue(null, false);
            runningModsField.SetValue(null, originalRunningMods);
            foreach (var mod in addedToLoadedMods)
            {
                ModContentPack_ReloadContentInt_Patch.loadedMods.Remove(mod);
            }
            addedToLoadedMods.Clear();
        }

        [Test]
        public void WithoutHar_DoesNotPatchOrDefer()
        {
            // 單元測試環境沒有載入 HAR：不套用 patch，HAR 與其衍生仍維持排除在提早載入之外。
            AlienRaceGraphicsHookGate.TryPatch(new Harmony("FasterGameLoading.Tests.AlienRaceGraphicsHookGate"));

            Assert.That(AlienRaceGraphicsHookGate.TargetMethod(), Is.Null);
            Assert.That(AlienRaceGraphicsHookGate.DefersGraphicsHook, Is.False);
        }

        [Test]
        public void Prefix_BlocksHookUntilAllRunningModContentLoaded()
        {
            var har = CreateMod();
            var raceMod = CreateMod();
            runningModsField.SetValue(null, new List<ModContentPack> { har, raceMod });

            MarkLoaded(har);
            Assert.That(AlienRaceGraphicsHookGate.Prefix(), Is.False);

            MarkLoaded(raceMod);
            Assert.That(AlienRaceGraphicsHookGate.Prefix(), Is.True);
        }

        [Test]
        public void Prefix_StaysOpenOnceReleased()
        {
            var har = CreateMod();
            runningModsField.SetValue(null, new List<ModContentPack> { har });
            MarkLoaded(har);
            Assert.That(AlienRaceGraphicsHookGate.Prefix(), Is.True);

            // 放行後不再逐幀走訪 mod 清單。
            runningModsField.SetValue(null, new List<ModContentPack> { har, CreateMod() });
            Assert.That(AlienRaceGraphicsHookGate.Prefix(), Is.True);
        }

        private void MarkLoaded(ModContentPack mod)
        {
            ModContentPack_ReloadContentInt_Patch.loadedMods.Add(mod);
            addedToLoadedMods.Add(mod);
        }

        private static ModContentPack CreateMod()
            => (ModContentPack)FormatterServices.GetUninitializedObject(typeof(ModContentPack));
    }
}
