using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;
using Verse;

namespace FasterGameLoading.Tests.DelayGraphicAndIconLoading
{
    [TestFixture]
    public class ThingDef_PostLoad_PatchTests
    {
        private static Harmony harmony;
        private DelayedActions delayedActions;
        private static Action capturedExecuteWhenFinishedAction;

        private static bool PrefixSkip() => false;

        private static bool MockExecuteWhenFinished(Action action)
        {
            capturedExecuteWhenFinishedAction = action;
            return false;
        }

        private static ThingDef CreateMockThingDef(string name)
        {
            var def = (ThingDef)FormatterServices.GetUninitializedObject(typeof(ThingDef));
            def.defName = name;
            return def;
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            harmony = new Harmony("FasterGameLoading.Tests.ThingDef_PostLoad_PatchTests");

            var emitMethod = AccessTools.Method(typeof(FGLLog), "Emit");
            if (emitMethod != null)
            {
                harmony.Patch(emitMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(ThingDef_PostLoad_PatchTests), nameof(PrefixSkip))));
            }

            var execWhenFinished = AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.ExecuteWhenFinished));
            if (execWhenFinished != null)
            {
                harmony.Patch(execWhenFinished, prefix: new HarmonyMethod(AccessTools.Method(typeof(ThingDef_PostLoad_PatchTests), nameof(MockExecuteWhenFinished))));
            }
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            harmony?.UnpatchAll("FasterGameLoading.Tests.ThingDef_PostLoad_PatchTests");
        }

        [SetUp]
        public void SetUp()
        {
            delayedActions = new DelayedActions();
            capturedExecuteWhenFinishedAction = null;

            // Set FasterGameLoadingMod.delayedActions
            var prop = typeof(FasterGameLoadingMod).GetProperty(nameof(FasterGameLoadingMod.delayedActions), BindingFlags.Public | BindingFlags.Static);
            prop?.SetValue(null, delayedActions, index: null);
        }

        [TearDown]
        public void TearDown()
        {
            delayedActions?.ClearQueues();
            var prop = typeof(FasterGameLoadingMod).GetProperty(nameof(FasterGameLoadingMod.delayedActions), BindingFlags.Public | BindingFlags.Static);
            prop?.SetValue(null, value: null, index: null);
        }

        [Test]
        public void Prepare_ReflectsDelayGraphicLoadingSetting()
        {
            var previous = FasterGameLoadingSettings.DelayGraphicLoading;
            try
            {
                FasterGameLoadingSettings.DelayGraphicLoading = true;
                Assert.That(ThingDef_PostLoad_Patch.Prepare(), Is.True);

                FasterGameLoadingSettings.DelayGraphicLoading = false;
                Assert.That(ThingDef_PostLoad_Patch.Prepare(), Is.False);
            }
            finally
            {
                FasterGameLoadingSettings.DelayGraphicLoading = previous;
            }
        }

        [Test]
        public void Transpiler_ReplacesExecuteWhenFinished_WithLdarg0AndExecuteDelayed()
        {
            var executeWhenFinished = AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.ExecuteWhenFinished));
            var executeDelayed = AccessTools.Method(typeof(ThingDef_PostLoad_Patch), nameof(ThingDef_PostLoad_Patch.ExecuteDelayed));

            var instructions = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Nop),
                new CodeInstruction(OpCodes.Call, executeWhenFinished),
                new CodeInstruction(OpCodes.Ret),
            };

            var output = ThingDef_PostLoad_Patch.Transpiler(instructions).ToList();

            Assert.That(output.Count, Is.EqualTo(4));
            Assert.That(output[0].opcode, Is.EqualTo(OpCodes.Nop));
            Assert.That(output[1].opcode, Is.EqualTo(OpCodes.Ldarg_0));
            Assert.That(output[2].opcode, Is.EqualTo(OpCodes.Call));
            Assert.That(output[2].operand, Is.EqualTo(executeDelayed));
            Assert.That(output[3].opcode, Is.EqualTo(OpCodes.Ret));
        }

        [Test]
        public void ExecuteDelayed_WhenShouldBeLoadedImmediately_RunsActionInExecuteWhenFinished()
        {
            var def = CreateMockThingDef("ImmediateDef");
            def.uiIconPath = "Things/Item/TestIcon"; // causes ShouldBeLoadedImmediately to return true

            bool actionExecuted = false;
            Action act = () => actionExecuted = true;

            ThingDef_PostLoad_Patch.ExecuteDelayed(act, def);

            Assert.That(capturedExecuteWhenFinishedAction, Is.Not.Null);
            Assert.That(actionExecuted, Is.False, "PostLoad 當下只排程，不直接執行圖形初始化。");

            capturedExecuteWhenFinishedAction();
            Assert.That(actionExecuted, Is.True);
            Assert.That(delayedActions.GraphicsToLoadCount, Is.EqualTo(0));
        }

        [Test]
        public void ExecuteDelayed_WhenNotShouldBeLoadedImmediately_EnqueuesToDelayedActions()
        {
            var def = CreateMockThingDef("DelayedDef");
            def.uiIconPath = null; // ShouldBeLoadedImmediately returns false

            bool actionExecuted = false;
            Action act = () => actionExecuted = true;

            ThingDef_PostLoad_Patch.ExecuteDelayed(act, def);
            capturedExecuteWhenFinishedAction();

            Assert.That(actionExecuted, Is.False);
            Assert.That(delayedActions.GraphicsToLoadCount, Is.EqualTo(1));

            var loadedDefs = new System.Collections.Generic.List<ThingDef>();
            var drain = delayedActions.LoadDeferredGraphicsCoroutine(loadedDefs);
            while (drain.MoveNext()) { }
            Assert.That(loadedDefs, Is.EqualTo(new[] { def }));
            Assert.That(actionExecuted, Is.True);
        }

        [Test]
        public void ExecuteDelayed_DecidesOnlyAfterCrossReferencesAreResolved()
        {
            // PostLoad 當下交叉參照尚未解析，designationCategory 仍是 null。
            var def = CreateMockThingDef("Bed");
            bool actionExecuted = false;

            ThingDef_PostLoad_Patch.ExecuteDelayed(() => actionExecuted = true, def);
            Assert.That(delayedActions.GraphicsToLoadCount, Is.EqualTo(0), "不得在 PostLoad 當下就決定延遲。");

            // ResolveAllWantedCrossReferences 之後才輪到 ExecuteWhenFinished 回呼。
            def.designationCategory = (DesignationCategoryDef)FormatterServices.GetUninitializedObject(typeof(DesignationCategoryDef));
            capturedExecuteWhenFinishedAction();

            Assert.That(actionExecuted, Is.True, "有 designationCategory 的建築（例如床）必須立即載入圖形。");
            Assert.That(delayedActions.GraphicsToLoadCount, Is.EqualTo(0));
        }

        [Test]
        public void ExecuteDelayed_WhenDelayedActionsIsNull_RunsActionInExecuteWhenFinished()
        {
            var prop = typeof(FasterGameLoadingMod).GetProperty(nameof(FasterGameLoadingMod.delayedActions), BindingFlags.Public | BindingFlags.Static);
            prop?.SetValue(null, value: null, index: null);

            var def = CreateMockThingDef("DelayedDef");
            def.uiIconPath = null;

            bool actionExecuted = false;
            ThingDef_PostLoad_Patch.ExecuteDelayed(() => actionExecuted = true, def);
            capturedExecuteWhenFinishedAction();

            Assert.That(actionExecuted, Is.True);
        }
    }
}
