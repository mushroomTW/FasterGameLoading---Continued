using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RimWorld.IO;
using UnityEngine;
using Verse;
using HarmonyLib;

namespace FasterGameLoading.Tests.TextureDownscaler
{
    /// <summary>
    /// ModContentLoaderTexture2D_LoadTexture_Patch 的 headless 測試：背景轉交、registry 命中、
    /// 相容性短路、Postfix 登記與背景預讀。佇列與登記本身另見
    /// MainThreadTextureLoaderTests、LoadedTextureRegistryTests。
    /// </summary>
    [TestFixture]
    public class ModContentLoaderTexture2D_LoadTexture_PatchTests
    {
        private static readonly Type PatchType = typeof(ModContentLoaderTexture2D_LoadTexture_Patch);

        private string tempDir;
        private FasterGameLoadingMod originalInstance;
        private bool originalStaticAtlasesBaking;
        private bool originalVerboseLogging;
        private int originalRedirectTimeoutMs;
        private Func<VirtualFile, Texture2D> originalMainThreadLoad;

        /// <summary>測試用的轉交逾時；正式值為 10 秒，會讓「泵送從未執行」的測試空等太久。</summary>
        private const int TestRedirectTimeoutMs = 1000;

        /// <summary>不觸碰檔案系統的 VirtualFile 替身，只需要 FullPath 可讀。</summary>
        private sealed class FakeVirtualFile : VirtualFile
        {
            private readonly string path;

            public FakeVirtualFile(string path)
            {
                this.path = path;
            }

            public override string Name => Path.GetFileName(path);
            public override string FullPath => path;
            public override bool Exists => true;
            public override Stream CreateReadStream() => new MemoryStream(Array.Empty<byte>());
            public override byte[] ReadAllBytes() => Array.Empty<byte>();
            public override string ReadAllText() => string.Empty;
            public override string[] ReadAllLines() => Array.Empty<string>();
            public override long Length => 0L;
        }

        private static Texture2D NewDetachedTexture()
        {
            return (Texture2D)FormatterServices.GetUninitializedObject(typeof(Texture2D));
        }

        [SetUp]
        public void SetUp()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "FGLLoadTexTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            originalStaticAtlasesBaking = FasterGameLoadingSettings.StaticAtlasesBaking;
            originalVerboseLogging = FasterGameLoadingSettings.VerboseLogging;
            originalInstance = FasterGameLoadingMod.Instance;
            originalRedirectTimeoutMs = MainThreadTextureLoader.RedirectTimeoutMs;
            originalMainThreadLoad = MainThreadTextureLoader.LoadOnMainThread;
            MainThreadTextureLoader.RedirectTimeoutMs = TestRedirectTimeoutMs;

            // 預設由 FGL 負責載入貼圖，否則 Prefix 會在第一個分支就短路。
            TextureOwnership.OverrideForTests(TextureOwner.Fgl);
            FasterGameLoadingSettings.StaticAtlasesBaking = false;
            FasterGameLoadingSettings.VerboseLogging = false;

            SetModInstance(CreateModWithCacheManager(new TextureCacheManager(tempDir)));

            ClearPatchState();

        }

        [TearDown]
        public void TearDown()
        {
            TestSetup.IsInMainThreadOverride = null;
            MainThreadTextureLoader.Drain();
            MainThreadTextureLoader.RedirectTimeoutMs = originalRedirectTimeoutMs;
            MainThreadTextureLoader.LoadOnMainThread = originalMainThreadLoad;
            ClearPatchState();
            SessionLifecycle.Raise(LifecyclePhase.LanguageReloading);

            SetModInstance(originalInstance);
            FasterGameLoadingSettings.StaticAtlasesBaking = originalStaticAtlasesBaking;
            FasterGameLoadingSettings.VerboseLogging = originalVerboseLogging;
            TextureOwnership.OverrideForTests(null);

            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (IOException)
            {
                // 背景預載入工作可能仍持有檔案控制代碼；殘留暫存目錄不影響斷言結果。
            }
        }

        // ── Prefix：非主執行緒轉交 ──

        [Test]
        public void Prefix_OffMainThread_ReturnsTextureFromMainThreadLoaderAndSkipsOriginal()
        {
            // 佇列、逾時與取消的細節由 MainThreadTextureLoaderTests 驗證；這裡只確認補丁把背景載入交給它。
            var expected = NewDetachedTexture();
            MainThreadTextureLoader.LoadOnMainThread = _ => expected;
            TestSetup.IsInMainThreadOverride = () => false;
            var pump = Task.Run(() =>
            {
                SpinWait.SpinUntil(() => MainThreadTextureLoader.PendingCount > 0, TimeSpan.FromMilliseconds(900));
                MainThreadTextureLoader.Drain();
            });

            Texture2D result = null;
            bool runOriginal = ModContentLoaderTexture2D_LoadTexture_Patch.Prefix(
                new FakeVirtualFile(Path.Combine(tempDir, "f.png")), out bool state, ref result);
            pump.Wait(2000);

            Assert.That(runOriginal, Is.False, "背景執行緒不得去碰 Unity 資源 API。");
            Assert.That(state, Is.False);
            Assert.That(result, Is.SameAs(expected));
        }

        // ── Prefix：相容性旗標的短路分支 ──

        [Test]
        public void Prefix_WhenImageOptActive_DefersToOriginalLoader()
        {
            TextureOwnership.OverrideForTests(TextureOwner.ImageOpt);

            Texture2D result = null;
            bool runOriginal = ModContentLoaderTexture2D_LoadTexture_Patch.Prefix(
                new FakeVirtualFile(Path.Combine(tempDir, "Textures", "imageopt.png")), out bool state, ref result);

            Assert.That(runOriginal, Is.True);
            Assert.That(state, Is.False, "__state 必須為 false，否則 Postfix 會把 ImageOpt 載入的紋理誤登記為 FGL 的快取。");
        }

        [Test]
        public void Prefix_WhenGraphicsSettingsActive_DefersToOriginalLoader()
        {
            TextureOwnership.OverrideForTests(TextureOwner.GraphicsSettings);

            Texture2D result = null;
            bool runOriginal = ModContentLoaderTexture2D_LoadTexture_Patch.Prefix(
                new FakeVirtualFile(Path.Combine(tempDir, "Textures", "gsplus.png")), out bool state, ref result);

            Assert.That(runOriginal, Is.True);
            Assert.That(state, Is.False);
        }

        // ── Prefix：WeakReference 快取命中 ──

        [Test]
        public void Prefix_ServesFromWeakReferenceCacheWithoutRunningOriginal()
        {
            string fullPath = Path.Combine(tempDir, "Textures", "cached.png");
            var cached = NewDetachedTexture();
            LoadedTextureRegistry.Record(fullPath, cached);

            Texture2D result = null;
            bool runOriginal = ModContentLoaderTexture2D_LoadTexture_Patch.Prefix(
                new FakeVirtualFile(fullPath), out bool state, ref result);

            Assert.That(runOriginal, Is.False);
            Assert.That(state, Is.False);
            Assert.That(result, Is.SameAs(cached));
        }

        // 註：Prefix 的降質快取分支（TryServeFromDownscaleCache）在此環境下無法測試。
        // 該方法本體含 Texture2D 建構式、LoadImage、Compress、Apply 等 Unity ECall，
        // 而測試環境已由 TestSetup 掛上 MonoMod 的 JIT hook；JIT 該方法時 CLR 會拋出
        // 「ECall methods must be packaged into a system module」而非在呼叫時才失敗，
        // 因此連「進入方法後立刻回傳 false」的路徑也無法執行。

        // ── Postfix ──

        [Test]
        public void Postfix_SavesPathWhenOriginalLoaderRan()
        {
            string fullPath = Path.Combine(tempDir, "Textures", "post.png");
            var texture = NewDetachedTexture();

            ModContentLoaderTexture2D_LoadTexture_Patch.Postfix(
                new FakeVirtualFile(fullPath), __state: true, texture);

            Assert.That(
                LoadedTextureRegistry.TryGetPath(texture, out string resolved),
                Is.True);
            Assert.That(resolved, Is.EqualTo(fullPath));
        }

        [Test]
        public void Postfix_DoesNotSavePathWhenPrefixAlreadyServedTexture()
        {
            string fullPath = Path.Combine(tempDir, "Textures", "served.png");
            var texture = NewDetachedTexture();

            ModContentLoaderTexture2D_LoadTexture_Patch.Postfix(
                new FakeVirtualFile(fullPath), __state: false, texture);

            Assert.That(
                LoadedTextureRegistry.TryGetPath(texture, out _),
                Is.False);
        }

        [Test]
        public void Postfix_SkipsProtectedModTexturePath()
        {
            string modRoot = Path.Combine(tempDir, "TargetMod").Replace('\\', '/');
            ProtectedMods.SetProtectedTextureRootsForTests(modRoot);
            string fullPath = modRoot + "/Textures/Alien/body.png";
            var texture = NewDetachedTexture();

            ModContentLoaderTexture2D_LoadTexture_Patch.Postfix(
                new FakeVirtualFile(fullPath), __state: true, texture);

            Assert.That(
                LoadedTextureRegistry.TryGetPath(texture, out _),
                Is.False,
                "排除烘焙的 Mod 紋理不得進入 WeakReference 快取，否則下次載入會沿用同一實體而繞過排除判定。");
        }

        [Test]
        public void Postfix_NullResultIsIgnored()
        {
            Assert.DoesNotThrow(() => ModContentLoaderTexture2D_LoadTexture_Patch.Postfix(
                new FakeVirtualFile(Path.Combine(tempDir, "none.png")), __state: true, __result: null));
        }

        // ── 背景預載入 ──

        [Test]
        public void StartPreloadCachedTextures_ReadsCacheFilesIntoMemory()
        {
            string originalPath = Path.Combine(tempDir, "Textures", "pre.png");
            string cachePath = Path.Combine(tempDir, "pre_cache.png");
            var payload = new byte[] { 9, 8, 7, 6 };
            File.WriteAllBytes(cachePath, payload);

            FasterGameLoadingMod.Instance.CacheManager.SetCacheEntry(originalPath, cachePath);

            ModContentLoaderTexture2D_LoadTexture_Patch.StartPreloadCachedTextures();

            // 預載入刻意延遲 TexturePreloadDelayMs 才開始，避免與啟動期 XML I/O 爭頻寬。
            SpinWait.SpinUntil(
                () => ModContentLoaderTexture2D_LoadTexture_Patch.preloadedCacheBytes.ContainsKey(cachePath),
                TimeSpan.FromSeconds(5));

            Assert.That(
                ModContentLoaderTexture2D_LoadTexture_Patch.preloadedCacheBytes.TryGetValue(cachePath, out var loaded),
                Is.True);
            Assert.That(loaded, Is.EqualTo(payload));
        }

        [Test]
        public void StartPreloadCachedTextures_EmptyCacheStartsNoBackgroundWork()
        {
            ModContentLoaderTexture2D_LoadTexture_Patch.preloadedCacheBytes["stale"] = new byte[] { 1 };

            ModContentLoaderTexture2D_LoadTexture_Patch.StartPreloadCachedTextures();

            Assert.That(ModContentLoaderTexture2D_LoadTexture_Patch.preloadedCacheBytes, Is.Empty,
                "即使沒有快取項目也必須先清空舊的預載入位元組，避免沿用上個 session 的內容。");
        }

        [Test]
        public void StartPreloadCachedTextures_WithoutModInstanceDoesNotThrow()
        {
            SetModInstance(null);

            Assert.DoesNotThrow(ModContentLoaderTexture2D_LoadTexture_Patch.StartPreloadCachedTextures);
        }

        [Test]
        public void StartPreloadCachedTextures_WhenGraphicsSettingsActive_SkipsPreload()
        {
            string cachePath = WriteCacheEntry("gs_cache.png");
            TextureOwnership.OverrideForTests(TextureOwner.GraphicsSettings);

            ModContentLoaderTexture2D_LoadTexture_Patch.StartPreloadCachedTextures();
            WaitForPreloadTask();

            // GS+ 啟用時 Prefix 一律交給原始流程，預讀的位元組永遠不會被取用，只會佔住記憶體到遊戲結束。
            Assert.That(ModContentLoaderTexture2D_LoadTexture_Patch.preloadedCacheBytes.ContainsKey(cachePath), Is.False);
        }

        [Test]
        public void TakeCachedTextureBytes_PrefersPreloadedBytes()
        {
            string cachePath = Path.Combine(tempDir, "missing_on_disk.png");
            var preloaded = new byte[] { 4, 2 };
            ModContentLoaderTexture2D_LoadTexture_Patch.preloadedCacheBytes[cachePath] = preloaded;

            var data = ModContentLoaderTexture2D_LoadTexture_Patch.TakeCachedTextureBytes(cachePath);

            Assert.That(data, Is.SameAs(preloaded));
            Assert.That(ModContentLoaderTexture2D_LoadTexture_Patch.preloadedCacheBytes.ContainsKey(cachePath), Is.False);
        }

        [Test]
        public void TakeCachedTextureBytes_BeforePreloaderReachesFile_PreloaderSkipsIt()
        {
            string cachePath = WriteCacheEntry("raced_cache.png");

            ModContentLoaderTexture2D_LoadTexture_Patch.StartPreloadCachedTextures();
            // 預讀會先延遲 TexturePreloadDelayMs；在這之前主執行緒已自行讀取這個檔案。
            var data = ModContentLoaderTexture2D_LoadTexture_Patch.TakeCachedTextureBytes(cachePath);
            WaitForPreloadTask();

            Assert.That(data, Is.EqualTo(new byte[] { 9, 8, 7 }));
            Assert.That(ModContentLoaderTexture2D_LoadTexture_Patch.preloadedCacheBytes.ContainsKey(cachePath), Is.False,
                "主執行緒已讀過的檔案不得再被預讀留在記憶體裡。");
        }

        [Test]
        public void ReleasePreloadedCacheBytes_StopsPreloadAndDropsLeftovers()
        {
            WriteCacheEntry("left_cache.png");
            ModContentLoaderTexture2D_LoadTexture_Patch.preloadedCacheBytes["stale"] = new byte[] { 1 };

            ModContentLoaderTexture2D_LoadTexture_Patch.StartPreloadCachedTextures();
            ModContentLoaderTexture2D_LoadTexture_Patch.ReleasePreloadedCacheBytes();
            WaitForPreloadTask();

            Assert.That(
                SpinWait.SpinUntil(() => ModContentLoaderTexture2D_LoadTexture_Patch.preloadedCacheBytes.IsEmpty, TimeSpan.FromSeconds(2)),
                Is.True,
                "啟動完成後，未被取用的預讀位元組（含仍在執行的預讀迴圈稍後寫入的）都必須釋放。");
        }

        /// <summary>建立一筆降質快取項目與其快取檔，回傳快取檔路徑。</summary>
        private string WriteCacheEntry(string cacheFileName)
        {
            string cachePath = Path.Combine(tempDir, cacheFileName);
            File.WriteAllBytes(cachePath, new byte[] { 9, 8, 7 });
            FasterGameLoadingMod.Instance.CacheManager.SetCacheEntry(Path.Combine(tempDir, "Textures", cacheFileName), cachePath);
            return cachePath;
        }

        private static void WaitForPreloadTask()
        {
            var task = (Task)PatchType.GetField("_preloadTask", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            Assert.That(task.Wait(TimeSpan.FromSeconds(5)), Is.True);
        }

        // ── 測試輔助 ──

        private static void ClearPatchState()
        {
            LoadedTextureRegistry.Clear();
            ModContentLoaderTexture2D_LoadTexture_Patch.preloadedCacheBytes.Clear();
        }


        private static FasterGameLoadingMod CreateModWithCacheManager(TextureCacheManager cacheManager)
        {
            // Mod 的建構式會建立 GameObject 並套用 Harmony，headless 下無法執行；
            // 這裡直接取得未初始化實體，只補上測試需要的 CacheManager。
            var mod = (FasterGameLoadingMod)FormatterServices.GetUninitializedObject(typeof(FasterGameLoadingMod));
            typeof(FasterGameLoadingMod)
                .GetProperty(nameof(FasterGameLoadingMod.CacheManager))
                .SetValue(mod, cacheManager);
            return mod;
        }

        private static void SetModInstance(FasterGameLoadingMod mod)
        {
            typeof(FasterGameLoadingMod)
                .GetProperty(nameof(FasterGameLoadingMod.Instance), BindingFlags.Public | BindingFlags.Static)
                .SetValue(obj: null, value: mod);
        }

        [Test]
        public void Prefix_WhenProtectedModTexturePath_BypassesCacheAndCallsOriginal()
        {
            TestSetup.IsInMainThreadOverride = () => true;
            ProtectedMods.SetProtectedTextureRootsForTests("C:/MockProtectedMod");

            var fakeFile = new FakeVirtualFile(@"C:\MockProtectedMod\Textures\Pawn.png");
            Texture2D result = null;
            bool __state;

            bool runOriginal = ModContentLoaderTexture2D_LoadTexture_Patch.Prefix(fakeFile, out __state, ref result);

            Assert.That(runOriginal, Is.True);
            Assert.That(__state, Is.True);
            Assert.That(result, Is.Null);
        }



        [Test]
        public void StartPreloadCachedTextures_WhenFilesExist_LoadsBytesIntoPreloadMap()
        {
            var mod = (FasterGameLoadingMod)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(FasterGameLoadingMod));
            var mgr = new TextureCacheManager(tempDir);
            string sampleFile = Path.Combine(tempDir, "preloaded.png");
            File.WriteAllBytes(sampleFile, new byte[] { 1, 2, 3, 4 });
            mgr.SetCacheEntry("test_key", sampleFile);

            AccessTools.PropertySetter(typeof(FasterGameLoadingMod), nameof(FasterGameLoadingMod.CacheManager))
                ?.Invoke(mod, new object[] { mgr });
            AccessTools.PropertySetter(typeof(FasterGameLoadingMod), nameof(FasterGameLoadingMod.Instance))
                ?.Invoke(null, new object[] { mod });

            try
            {
                ModContentLoaderTexture2D_LoadTexture_Patch.StartPreloadCachedTextures();
                SpinWait.SpinUntil(() => ModContentLoaderTexture2D_LoadTexture_Patch.preloadedCacheBytes.ContainsKey(sampleFile), TimeSpan.FromSeconds(3));

                Assert.That(ModContentLoaderTexture2D_LoadTexture_Patch.preloadedCacheBytes, Does.ContainKey(sampleFile));
            }
            finally
            {
                ModContentLoaderTexture2D_LoadTexture_Patch.preloadedCacheBytes.Clear();
                AccessTools.PropertySetter(typeof(FasterGameLoadingMod), nameof(FasterGameLoadingMod.Instance))
                    ?.Invoke(null, new object[] { null });
            }
        }
    }
}
