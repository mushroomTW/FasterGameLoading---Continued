using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;
using Verse;
using Verse.Sound;

namespace FasterGameLoading.Tests.DelaySoundLoading
{
    [TestFixture]
    public class SubSoundDef_Resolve_PatchTests
    {
        private static Harmony harmony;
        private DelayedActions delayedActions;
        private static Action capturedExecuteWhenFinishedAction;
        private static Type patchType;
        private static MethodInfo transpilerMethod;
        private static MethodInfo executeDelayedMethod;

        private static bool PrefixSkip() => false;

        private static bool MockExecuteWhenFinished(Action action)
        {
            capturedExecuteWhenFinishedAction = action;
            return false;
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            harmony = new Harmony("FasterGameLoading.Tests.SubSoundDef_Resolve_PatchTests");

            patchType = typeof(FasterGameLoadingMod).Assembly.GetType("FasterGameLoading.SubSoundDef_ResolvePatch");
            transpilerMethod = AccessTools.Method(patchType, "LateExecute");
            executeDelayedMethod = AccessTools.Method(patchType, "ExecuteDelayed");

            var emitMethod = AccessTools.Method(typeof(FGLLog), "Emit");
            if (emitMethod != null)
            {
                harmony.Patch(emitMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(SubSoundDef_Resolve_PatchTests), nameof(PrefixSkip))));
            }

            // Patch LongEventHandler.ExecuteWhenFinished (may fail if ECall, guard with try/catch)
            try
            {
                var execWhenFinished = AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.ExecuteWhenFinished));
                if (execWhenFinished != null)
                {
                    harmony.Patch(execWhenFinished, prefix: new HarmonyMethod(AccessTools.Method(typeof(SubSoundDef_Resolve_PatchTests), nameof(MockExecuteWhenFinished))));
                }
            }
            catch { }
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            harmony?.UnpatchAll("FasterGameLoading.Tests.SubSoundDef_Resolve_PatchTests");
        }

        [SetUp]
        public void SetUp()
        {
            delayedActions = new DelayedActions();
            capturedExecuteWhenFinishedAction = null;

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
        public void LateExecute_Transpiler_ReplacesExecuteWhenFinishedWithLdarg0AndExecuteDelayed()
        {
            Assert.That(transpilerMethod, Is.Not.Null);
            Assert.That(executeDelayedMethod, Is.Not.Null);

            var executeWhenFinished = AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.ExecuteWhenFinished));

            var instructions = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Nop),
                new CodeInstruction(OpCodes.Call, executeWhenFinished),
                new CodeInstruction(OpCodes.Ret),
            };

            var output = ((IEnumerable<CodeInstruction>)transpilerMethod.Invoke(null, new object[] { instructions })).ToList();

            Assert.That(output.Count, Is.EqualTo(4));
            Assert.That(output[0].opcode, Is.EqualTo(OpCodes.Nop));
            Assert.That(output[1].opcode, Is.EqualTo(OpCodes.Ldarg_0));
            Assert.That(output[2].opcode, Is.EqualTo(OpCodes.Call));
            Assert.That(output[2].operand, Is.EqualTo(executeDelayedMethod));
            Assert.That(output[3].opcode, Is.EqualTo(OpCodes.Ret));
        }

        [Test]
        public void ExecuteDelayed_WhenActionIsNull_DoesNotEnqueueOrDispatch()
        {
            var sound = (SubSoundDef)FormatterServices.GetUninitializedObject(typeof(SubSoundDef));

            executeDelayedMethod.Invoke(null, new object[] { null, sound });

            Assert.That(delayedActions.SubSoundDefToResolveCount, Is.EqualTo(0));
            Assert.That(capturedExecuteWhenFinishedAction, Is.Null);
        }

        [Test]
        public void ExecuteDelayed_WhenDelayedActionsNotNull_EnqueuesToDelayedActions()
        {
            var sound = (SubSoundDef)FormatterServices.GetUninitializedObject(typeof(SubSoundDef));
            bool actionExecuted = false;
            Action act = () => actionExecuted = true;

            executeDelayedMethod.Invoke(null, new object[] { act, sound });

            Assert.That(capturedExecuteWhenFinishedAction, Is.Null);
            Assert.That(delayedActions.SubSoundDefToResolveCount, Is.EqualTo(1));

            delayedActions.ResolvePendingSubSounds();
            Assert.That(actionExecuted, Is.True);
            Assert.That(delayedActions.SubSoundDefToResolveCount, Is.EqualTo(0));
        }

        [Test]
        public void ExecuteDelayed_WhenDelayedActionsIsNull_DispatchesToLongEventHandler()
        {
            var prop = typeof(FasterGameLoadingMod).GetProperty(nameof(FasterGameLoadingMod.delayedActions), BindingFlags.Public | BindingFlags.Static);
            prop?.SetValue(null, value: null, index: null);

            var sound = (SubSoundDef)FormatterServices.GetUninitializedObject(typeof(SubSoundDef));
            bool actionExecuted = false;
            Action act = () => actionExecuted = true;

            executeDelayedMethod.Invoke(null, new object[] { act, sound });

            Assert.That(capturedExecuteWhenFinishedAction, Is.SameAs(act));
            capturedExecuteWhenFinishedAction();
            Assert.That(actionExecuted, Is.True);
        }
    }
}
