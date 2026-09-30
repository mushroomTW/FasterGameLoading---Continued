using System;
using NUnit.Framework;

namespace FasterGameLoading.Tests.Settings
{
    [TestFixture]
    public class FasterGameLoadingSettingsTests
    {
        private bool origVerboseLogging;
        private bool origDelayGraphicLoading;
        private bool origEarlyModContentLoading;
        private bool origStaticAtlasesBaking;
        private bool origEnableMultiThreading;
        private bool origTypeLookupCache;

        [SetUp]
        public void SetUp()
        {
            origVerboseLogging = FasterGameLoadingSettings.VerboseLogging;
            origDelayGraphicLoading = FasterGameLoadingSettings.DelayGraphicLoading;
            origEarlyModContentLoading = FasterGameLoadingSettings.earlyModContentLoading;
            origStaticAtlasesBaking = FasterGameLoadingSettings.StaticAtlasesBaking;
            origEnableMultiThreading = FasterGameLoadingSettings.EnableMultiThreading;
            origTypeLookupCache = FasterGameLoadingSettings.TypeLookupCache;
        }

        [TearDown]
        public void TearDown()
        {
            FasterGameLoadingSettings.VerboseLogging = origVerboseLogging;
            FasterGameLoadingSettings.DelayGraphicLoading = origDelayGraphicLoading;
            FasterGameLoadingSettings.earlyModContentLoading = origEarlyModContentLoading;
            FasterGameLoadingSettings.StaticAtlasesBaking = origStaticAtlasesBaking;
            FasterGameLoadingSettings.EnableMultiThreading = origEnableMultiThreading;
            FasterGameLoadingSettings.TypeLookupCache = origTypeLookupCache;
        }

        [Test]
        public void VerboseLogging_PropertyGetSet()
        {
            FasterGameLoadingSettings.VerboseLogging = true;
            Assert.That(FasterGameLoadingSettings.VerboseLogging, Is.True);

            FasterGameLoadingSettings.VerboseLogging = false;
            Assert.That(FasterGameLoadingSettings.VerboseLogging, Is.False);
        }

        [Test]
        public void DelayGraphicLoading_PropertyGetSet()
        {
            FasterGameLoadingSettings.DelayGraphicLoading = true;
            Assert.That(FasterGameLoadingSettings.DelayGraphicLoading, Is.True);

            FasterGameLoadingSettings.DelayGraphicLoading = false;
            Assert.That(FasterGameLoadingSettings.DelayGraphicLoading, Is.False);
        }

        [Test]
        public void EarlyModContentLoading_FieldGetSet()
        {
            FasterGameLoadingSettings.earlyModContentLoading = true;
            Assert.That(FasterGameLoadingSettings.earlyModContentLoading, Is.True);

            FasterGameLoadingSettings.earlyModContentLoading = false;
            Assert.That(FasterGameLoadingSettings.earlyModContentLoading, Is.False);
        }

        [Test]
        public void StaticAtlasesBaking_PropertyGetSet()
        {
            FasterGameLoadingSettings.StaticAtlasesBaking = true;
            Assert.That(FasterGameLoadingSettings.StaticAtlasesBaking, Is.True);

            FasterGameLoadingSettings.StaticAtlasesBaking = false;
            Assert.That(FasterGameLoadingSettings.StaticAtlasesBaking, Is.False);
        }

        [Test]
        public void EnableMultiThreading_PropertyGetSet()
        {
            FasterGameLoadingSettings.EnableMultiThreading = true;
            Assert.That(FasterGameLoadingSettings.EnableMultiThreading, Is.True);

            FasterGameLoadingSettings.EnableMultiThreading = false;
            Assert.That(FasterGameLoadingSettings.EnableMultiThreading, Is.False);
        }

        [Test]
        public void TypeLookupCache_PropertyGetSet()
        {
            FasterGameLoadingSettings.TypeLookupCache = true;
            Assert.That(FasterGameLoadingSettings.TypeLookupCache, Is.True);

            FasterGameLoadingSettings.TypeLookupCache = false;
            Assert.That(FasterGameLoadingSettings.TypeLookupCache, Is.False);
        }

        [Test]
        public void ExposeData_WhenTypeLookupCacheDisabled_SavesSetting()
        {
            var savePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"FGL_TypeLookupCache_{Guid.NewGuid():N}.xml");
            try
            {
                FasterGameLoadingSettings.TypeLookupCache = false;
                Verse.Scribe.saver.InitSaving(savePath, "settings");
                try
                {
                    new FasterGameLoadingSettings().ExposeData();
                }
                finally
                {
                    Verse.Scribe.saver.FinalizeSaving();
                }

                // 預設為開啟，只有關閉時才會寫入；讀檔端依此還原使用者的選擇。
                Assert.That(System.IO.File.ReadAllText(savePath), Does.Contain("<typeLookupCache>False</typeLookupCache>"));
            }
            finally
            {
                if (Verse.Scribe.mode is not Verse.LoadSaveMode.Inactive)
                {
                    Verse.Scribe.ForceStop();
                }
                if (System.IO.File.Exists(savePath))
                {
                    System.IO.File.Delete(savePath);
                }
            }
        }

        [Test]
        public void ExposeData_ExecutesSuccessfully()
        {
            var settings = new FasterGameLoadingSettings();
            Assert.DoesNotThrow(() => settings.ExposeData());
        }

        [Test]
        public void ExposeData_WhenSaving_SnapshotsTextureCacheUnderCacheLock()
        {
            WithTextureCacheManager((mgr, savePath) =>
            {
                mgr.SetCacheEntry("orig.png", "cache.png");
                var cacheLock = HarmonyLib.AccessTools.Field(typeof(TextureCacheManager), "cacheLock").GetValue(mgr);

                System.Threading.Tasks.Task save;
                lock (cacheLock)
                {
                    save = System.Threading.Tasks.Task.Run(() => SaveSettings(savePath));
                    // 背景清理會在持鎖下從對照表移除項目；存檔若不持鎖直接讓 Scribe 列舉同一個字典，
                    // 兩者同時進行時會拋例外，而 InitSaving 已先截斷設定檔，只會留下半份設定。
                    Assert.That(save.Wait(300), Is.False, "序列化降質快取對照表前必須先取得 cacheLock。");
                }

                Assert.That(save.Wait(5000), Is.True);
                Assert.That(System.IO.File.ReadAllText(savePath), Does.Contain("cache.png"));
            });
        }

        [Test]
        public void ExposeData_RoundTrip_RestoresTextureCacheEntries()
        {
            WithTextureCacheManager((mgr, savePath) =>
            {
                mgr.SetCacheEntry("orig.png", "cache.png");
                SaveSettings(savePath);

                mgr.ReplaceCacheMap(null);
                LoadSettings(savePath);

                Assert.That(mgr.ResizedTextureCache.TryGetValue("orig.png", out var cachePath), Is.True);
                Assert.That(cachePath, Is.EqualTo("cache.png"));
            });
        }

        [Test]
        public void ExposeData_WhenLoadingSettingsWithoutTextureCache_UsesEmptyMap()
        {
            WithTextureCacheManager((mgr, savePath) =>
            {
                // 模擬舊版或首次啟動的設定檔：存檔時沒有 mod 實體，因此不會寫出 resizedTextureCache 節點。
                SetModInstance(null);
                SaveSettings(savePath);
                SetModInstance(CreateModWithCacheManager(mgr));

                mgr.SetCacheEntry("stale.png", "stale_cache.png");
                LoadSettings(savePath);

                Assert.That(mgr.ResizedTextureCache, Is.Not.Null.And.Empty);
            });
        }

        /// <summary>
        /// 以暫存目錄建立 TextureCacheManager 並掛到 mod 實體上執行測試本體；
        /// 讀檔會覆寫 SessionCache 的靜態欄位，結束後一併還原，避免影響其他測試。
        /// </summary>
        private static void WithTextureCacheManager(Action<TextureCacheManager, string> body)
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fgl_settings_test_" + Guid.NewGuid().ToString("N"));
            string savePath = System.IO.Path.Combine(tempDir, "settings.xml");
            System.IO.Directory.CreateDirectory(tempDir);
            var originalInstance = FasterGameLoadingMod.Instance;
            var originalTypes = TypeLookupCache.FullNamesFromLastSession;
            var originalFingerprint = TypeLookupCache.PersistedFingerprint;
            var originalMods = SessionCache.modsInLastSession;
            var originalBakeSpeeds = AdaptiveAtlasBaker.BakeSpeedHistory;
            try
            {
                var mgr = new TextureCacheManager(tempDir);
                SetModInstance(CreateModWithCacheManager(mgr));
                body(mgr, savePath);
            }
            finally
            {
                if (Verse.Scribe.mode is not Verse.LoadSaveMode.Inactive)
                {
                    Verse.Scribe.ForceStop();
                }
                SetModInstance(originalInstance);
                TypeLookupCache.FullNamesFromLastSession = originalTypes;
                TypeLookupCache.PersistedFingerprint = originalFingerprint;
                SessionCache.modsInLastSession = originalMods;
                AdaptiveAtlasBaker.BakeSpeedHistory = originalBakeSpeeds;
                try { System.IO.Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static void SaveSettings(string path)
        {
            Verse.Scribe.saver.InitSaving(path, "settings");
            try
            {
                new FasterGameLoadingSettings().ExposeData();
            }
            finally
            {
                Verse.Scribe.saver.FinalizeSaving();
            }
        }

        /// <summary>只跑 LoadingVars 一輪（沒有 Scribe_Deep 登記，不會觸發 PostLoadInit 的 mod 清單比對）。</summary>
        private static void LoadSettings(string path)
        {
            Verse.Scribe.loader.InitLoading(path);
            try
            {
                new FasterGameLoadingSettings().ExposeData();
            }
            finally
            {
                Verse.Scribe.loader.FinalizeLoading();
            }
        }

        private static FasterGameLoadingMod CreateModWithCacheManager(TextureCacheManager mgr)
        {
            var mod = (FasterGameLoadingMod)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(FasterGameLoadingMod));
            HarmonyLib.AccessTools.PropertySetter(typeof(FasterGameLoadingMod), nameof(FasterGameLoadingMod.CacheManager))
                .Invoke(mod, new object[] { mgr });
            return mod;
        }

        private static void SetModInstance(FasterGameLoadingMod mod)
        {
            HarmonyLib.AccessTools.PropertySetter(typeof(FasterGameLoadingMod), nameof(FasterGameLoadingMod.Instance))
                .Invoke(null, new object[] { mod });
        }

        private static bool Prefix_Skip() => false;
        private static bool Prefix_CheckboxLabeled(ref bool checkOn) => false;
        private static bool Prefix_ButtonText(ref bool __result)
        {
            __result = true;
            return false;
        }
        private static bool Prefix_GetRect(ref UnityEngine.Rect __result)
        {
            __result = new UnityEngine.Rect(0, 0, 100, 20);
            return false;
        }
        private static bool Prefix_CalcHeight(ref float __result)
        {
            __result = 20f;
            return false;
        }
        private static bool Prefix_WindowStackAdd(Verse.Window window)
        {
            if (window is Verse.Dialog_MessageBox box)
            {
                // 執行確認委派以覆蓋確認按鈕的處理邏輯
                var confirmedAct = (Action)HarmonyLib.AccessTools.Field(typeof(Verse.Dialog_MessageBox), "confirmedAct")?.GetValue(box);
                try { confirmedAct?.Invoke(); } catch { }
            }
            return false;
        }
        private static bool Prefix_WindowStack(ref Verse.WindowStack __result)
        {
            __result = (Verse.WindowStack)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Verse.WindowStack));
            return false;
        }

        [Test]
        public void DrawOptions_InvokesCheckboxLabeledSafely()
        {
            var harmony = new HarmonyLib.Harmony("test.settings.drawoptions");

            foreach (var m in typeof(Verse.Listing_Standard).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (m.Name == nameof(Verse.Listing_Standard.CheckboxLabeled))
                {
                    harmony.Patch(m, prefix: new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.Method(typeof(FasterGameLoadingSettingsTests), nameof(Prefix_Skip))));
                }
            }

            try
            {
                var ls = (Verse.Listing_Standard)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Verse.Listing_Standard));
                var drawLoading = typeof(FasterGameLoadingSettings).GetMethod("DrawLoadingOptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var drawDiag = typeof(FasterGameLoadingSettings).GetMethod("DrawDiagnosticsOptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

                Assert.DoesNotThrow(() => drawLoading?.Invoke(null, new object[] { ls }));
                Assert.DoesNotThrow(() => drawDiag?.Invoke(null, new object[] { ls }));
            }
            finally
            {
                harmony.UnpatchAll("test.settings.drawoptions");
            }
        }
    }
}
