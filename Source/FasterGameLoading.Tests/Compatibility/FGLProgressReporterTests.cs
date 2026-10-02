using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;

namespace FasterGameLoading.Tests.Compatibility
{
    [TestFixture]
    public class FGLProgressReporterTests
    {
        private FieldInfo getIsPausedField;
        private Func<bool> originalGetIsPaused;
        private bool originalEarlyModContentLoading;
        private DelayedActions originalDelayedActions;

        [SetUp]
        public void SetUp()
        {
            getIsPausedField = AccessTools.Field(typeof(FGLProgressReporter), "GetIsPaused");
            originalGetIsPaused = (Func<bool>)getIsPausedField?.GetValue(null);
            originalEarlyModContentLoading = FasterGameLoadingSettings.earlyModContentLoading;
            originalDelayedActions = FasterGameLoadingMod.delayedActions;
        }

        [TearDown]
        public void TearDown()
        {
            getIsPausedField?.SetValue(null, originalGetIsPaused);
            FasterGameLoadingSettings.earlyModContentLoading = originalEarlyModContentLoading;
            SetDelayedActions(originalDelayedActions);
        }

        [Test]
        public void Prepare_MatchesAvailabilityOfOptionalLoadingProgressGetter()
        {
            var targetMethod = FGLProgressReporter.TargetMethod();
            Assert.That(FGLProgressReporter.Prepare(), Is.EqualTo(targetMethod != null));
        }

        [Test]
        public void Postfix_WhenOriginalResultIsFalse_LeavesItFalse()
        {
            var result = false;
            FGLProgressReporter.Postfix(ref result);
            Assert.That(result, Is.False);
        }

        [Test]
        public void Postfix_WhenEarlyLoadingIsDisabled_LeavesOriginalResultUnchanged()
        {
            FasterGameLoadingSettings.earlyModContentLoading = false;
            getIsPausedField?.SetValue(null, (Func<bool>)(() => true));
            var result = true;

            FGLProgressReporter.Postfix(ref result);

            Assert.That(result, Is.True);
        }

        [Test]
        public void Postfix_WhenGetIsPausedIsNull_LeavesOriginalResultUnchanged()
        {
            FasterGameLoadingSettings.earlyModContentLoading = true;
            getIsPausedField?.SetValue(null, value: null);
            var result = true;

            FGLProgressReporter.Postfix(ref result);

            Assert.That(result, Is.True);
        }

        [Test]
        public void Postfix_WhenEarlyLoadingIsComplete_LeavesOriginalResultUnchanged()
        {
            FasterGameLoadingSettings.earlyModContentLoading = true;
            getIsPausedField?.SetValue(null, (Func<bool>)(() => true));

            var delayedActions = CreateDelayedActions(earlyLoadingComplete: true);
            SetDelayedActions(delayedActions);

            var result = true;
            FGLProgressReporter.Postfix(ref result);
            Assert.That(result, Is.True);
        }

        [Test]
        public void Postfix_WhenPausedAndEarlyLoadingNotComplete_SetsResultToFalse()
        {
            FasterGameLoadingSettings.earlyModContentLoading = true;
            getIsPausedField?.SetValue(null, (Func<bool>)(() => true));

            var delayedActions = CreateDelayedActions(earlyLoadingComplete: false);
            SetDelayedActions(delayedActions);

            var result = true;
            FGLProgressReporter.Postfix(ref result);
            Assert.That(result, Is.False);
        }

        [Test]
        public void Postfix_WhenNotPaused_LeavesResultTrue()
        {
            FasterGameLoadingSettings.earlyModContentLoading = true;
            getIsPausedField?.SetValue(null, (Func<bool>)(() => false));

            var result = true;
            FGLProgressReporter.Postfix(ref result);
            Assert.That(result, Is.True);
        }

        [Test]
        public void Postfix_WhenGetIsPausedThrows_SafelyCatchesAndLeavesResultTrue()
        {
            FasterGameLoadingSettings.earlyModContentLoading = true;
            getIsPausedField?.SetValue(null, (Func<bool>)(() => throw new InvalidOperationException("mock error")));

            var result = true;
            FGLProgressReporter.Postfix(ref result);
            Assert.That(result, Is.True);
        }

        [Test]
        public void SettingsGetter_IsAccessibleWithoutThrowing()
        {
            // 驗證 FasterGameLoadingMod.settings 的 getter 可被安全讀取（未經建構子時為 null）
            Assert.DoesNotThrow(() =>
            {
                var unused = FasterGameLoadingMod.settings;
            });
            Assert.That(FasterGameLoadingMod.settings, Is.Null);
        }

        private static DelayedActions CreateDelayedActions(bool earlyLoadingComplete)
        {
            var delayedActions = (DelayedActions)FormatterServices.GetUninitializedObject(typeof(DelayedActions));
            var loader = new EarlyModContentLoader();
            var completeField = AccessTools.Field(typeof(EarlyModContentLoader), "<EarlyLoadingComplete>k__BackingField")
                ?? AccessTools.Field(typeof(EarlyModContentLoader), "EarlyLoadingComplete");
            completeField?.SetValue(loader, earlyLoadingComplete);

            var loaderField = AccessTools.Field(typeof(DelayedActions), "earlyModContentLoader");
            loaderField?.SetValue(delayedActions, loader);

            return delayedActions;
        }

        private static void SetDelayedActions(DelayedActions delayedActions)
        {
            var prop = AccessTools.Property(typeof(FasterGameLoadingMod), nameof(FasterGameLoadingMod.delayedActions));
            prop?.SetValue(null, delayedActions, index: null);
        }
    }
}
