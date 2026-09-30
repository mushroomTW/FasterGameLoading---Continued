using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NUnit.Framework;
using Verse;

namespace FasterGameLoading.Tests.Core
{
    [TestFixture]
    public class StartupTests
    {
        // 期望值陣列：避免每個斷言反覆建立常數陣列 (CA1861)
        private static readonly int[] ExpectedFullOrder = { 1, 2, 3 };
        private static readonly int[] ExpectedOrderAfterThrow = { 1, 3 };

        // 註：SessionLifecycle 的處理常式無法取消登記；各測試只登記寫入自身區域變數的處理常式，
        // 之後其他測試再觸發時只會改到已結束測試的區域變數，不影響斷言。

        [Test]
        public void On_WithNullHandler_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => SessionLifecycle.On(LifecyclePhase.StartupCompleted, handler: null));
        }

        [Test]
        public void Postfix_RaisesStartupCompletedInRegistrationOrder()
        {
            var executionOrder = new List<int>();

            SessionLifecycle.On(LifecyclePhase.StartupCompleted, () => executionOrder.Add(1));
            SessionLifecycle.On(LifecyclePhase.StartupCompleted, () => executionOrder.Add(2));
            SessionLifecycle.On(LifecyclePhase.StartupCompleted, () => executionOrder.Add(3));

            Startup.Postfix();

            Assert.That(executionOrder, Is.EqualTo(ExpectedFullOrder));
        }

        [Test]
        public void Postfix_WhenHandlerThrows_ContinuesExecutingRemainingHandlers()
        {
            var executionOrder = new List<int>();

            SessionLifecycle.On(LifecyclePhase.StartupCompleted, () => executionOrder.Add(1));
            SessionLifecycle.On(LifecyclePhase.StartupCompleted, () => throw new InvalidOperationException("Simulated error"));
            SessionLifecycle.On(LifecyclePhase.StartupCompleted, () => executionOrder.Add(3));

            Assert.DoesNotThrow(() => Startup.Postfix());

            Assert.That(executionOrder, Is.EqualTo(ExpectedOrderAfterThrow));
        }

        [Test]
        public void Postfix_RaisesStartupCompletedAgainAfterLanguageReload()
        {
            // 切換語言後 CallAll 會再跑一次；型別快照等結算必須反映重載後這一輪，而不是停在第一輪。
            var executionCount = 0;
            SessionLifecycle.On(LifecyclePhase.StartupCompleted, () => executionCount++);

            Startup.Postfix();
            SessionLifecycle.Raise(LifecyclePhase.LanguageReloading);
            Startup.Postfix();

            Assert.That(executionCount, Is.EqualTo(2));
        }

        [Test]
        public void Raise_OnlyRunsHandlersOfThatPhase()
        {
            bool reloadHandlerRan = false;
            SessionLifecycle.On(LifecyclePhase.LanguageReloading, () => reloadHandlerRan = true);

            SessionLifecycle.Raise(LifecyclePhase.StartupCompleted);

            Assert.That(reloadHandlerRan, Is.False);
        }

        [Test]
        public void Postfix_PopulatesModsInLastSession()
        {
            SessionCache.modsInLastSession = null;

            Startup.Postfix();

            Assert.That(SessionCache.modsInLastSession, Is.Not.Null);
        }

        private static List<ModMetaData> stubActiveMods;
        private static bool Prefix_StubActiveMods(ref IEnumerable<ModMetaData> __result)
        {
            __result = stubActiveMods;
            return false;
        }

        [Test]
        public void Postfix_WhenActiveModsHasElements_ExtractsPackageIds()
        {
            var meta = (ModMetaData)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(ModMetaData));
            AccessTools.Field(typeof(ModMetaData), "packageIdLowerCase").SetValue(meta, "test.mod.package");

            var activeModsGetter = AccessTools.PropertyGetter(typeof(Verse.ModsConfig), nameof(Verse.ModsConfig.ActiveModsInLoadOrder));
            var harmonyInstance = new Harmony("test.startup.activemods");
            var stubList = new List<ModMetaData> { null, meta };
            harmonyInstance.Patch(activeModsGetter, prefix: new HarmonyMethod(AccessTools.Method(typeof(StartupTests), nameof(Prefix_StubActiveMods))));
            stubActiveMods = stubList;

            try
            {
                Startup.Postfix();
                Assert.That(SessionCache.modsInLastSession, Does.Contain("test.mod.package"));
            }
            finally
            {
                harmonyInstance.Unpatch(activeModsGetter, HarmonyPatchType.Prefix, harmonyInstance.Id);
                stubActiveMods = null;
            }
        }

        private static bool Prefix_ExecuteWhenFinishedImmediately(Action action)
        {
            try
            {
                action?.Invoke();
            }
            catch
            {
                // 模擬在啟動委派中可能拋出的任何例外，確認其安全執行
            }
            return false;
        }

        private static bool Prefix_ExecuteWhenFinishedThrows(Action action)
        {
            throw new InvalidOperationException("Simulated LongEventHandler error");
        }

        [Test]
        public void ScheduleDeferredStartupActions_WhenExecuteWhenFinished_InvokesCallbackSafely()
        {
            var harmonyInstance = new Harmony("test.startup.deferred.success");
            var method = AccessTools.Method(typeof(Verse.LongEventHandler), nameof(Verse.LongEventHandler.ExecuteWhenFinished), new Type[] { typeof(Action) });
            harmonyInstance.Patch(method, prefix: new HarmonyMethod(AccessTools.Method(typeof(StartupTests), nameof(Prefix_ExecuteWhenFinishedImmediately))));

            try
            {
                var scheduleMethod = typeof(Startup).GetMethod("ScheduleDeferredStartupActions", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.DoesNotThrow(() => scheduleMethod.Invoke(null, null));
            }
            finally
            {
                harmonyInstance.Unpatch(method, HarmonyPatchType.Prefix, harmonyInstance.Id);
            }
        }

        [Test]
        public void ScheduleDeferredStartupActions_WhenExecuteWhenFinishedThrows_LogsError()
        {
            var harmonyInstance = new Harmony("test.startup.deferred.throw");
            var method = AccessTools.Method(typeof(Verse.LongEventHandler), nameof(Verse.LongEventHandler.ExecuteWhenFinished), new Type[] { typeof(Action) });
            harmonyInstance.Patch(method, prefix: new HarmonyMethod(AccessTools.Method(typeof(StartupTests), nameof(Prefix_ExecuteWhenFinishedThrows))));
            var errors = new List<string>();
            var previousOnLogError = TestSetup.OnLogError;
            TestSetup.OnLogError = errors.Add;

            try
            {
                var scheduleMethod = typeof(Startup).GetMethod("ScheduleDeferredStartupActions", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.DoesNotThrow(() => scheduleMethod.Invoke(null, null));
                Assert.That(errors, Has.Some.Contains("Error scheduling startup completion actions in LongEventHandler"));
            }
            finally
            {
                TestSetup.OnLogError = previousOnLogError;
                harmonyInstance.Unpatch(method, HarmonyPatchType.Prefix, harmonyInstance.Id);
            }
        }

        private static bool Prefix_InjectTranslationsThrows()
        {
            throw new InvalidOperationException("Simulated InjectTranslations error");
        }

        [Test]
        public void InjectTranslations_WhenThrows_LogsError()
        {
            var harmonyInstance = new Harmony("test.startup.inject.throw");
            var translationInjectorType = AccessTools.TypeByName("FasterGameLoading.TranslationInjector");
            var method = AccessTools.Method(translationInjectorType, "InjectTranslations");
            harmonyInstance.Patch(method, prefix: new HarmonyMethod(AccessTools.Method(typeof(StartupTests), nameof(Prefix_InjectTranslationsThrows))));
            var errors = new List<string>();
            var previousOnLogError = TestSetup.OnLogError;
            TestSetup.OnLogError = errors.Add;

            try
            {
                var injectMethod = typeof(Startup).GetMethod("InjectTranslations", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.DoesNotThrow(() => injectMethod.Invoke(null, null));
                Assert.That(errors, Has.Some.Contains("TranslationInjector.InjectTranslations execution failed"));
            }
            finally
            {
                TestSetup.OnLogError = previousOnLogError;
                harmonyInstance.Unpatch(method, HarmonyPatchType.Prefix, harmonyInstance.Id);
            }
        }

        [Test]
        public void StartBackgroundCacheCleanup_RunsWithoutThrowing()
        {
            var cleanupMethod = typeof(Startup).GetMethod("StartBackgroundCacheCleanup", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.DoesNotThrow(() => cleanupMethod.Invoke(null, null));
        }
    }
}
