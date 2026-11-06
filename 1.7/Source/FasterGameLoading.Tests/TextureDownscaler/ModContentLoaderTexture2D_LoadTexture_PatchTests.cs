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

namespace FasterGameLoading.Tests.TextureDownscaler;
/// <summary>
/// ModContentLoaderTexture2D_LoadTexture_Patch 的 headless 測試：背景轉交、registry 命中、
/// 相容性短路、降質快取命中與 Postfix 登記。佇列與登記本身另見
/// MainThreadTextureLoaderTests、LoadedTextureRegistryTests。
/// </summary>
[TestFixture]
public class ModContentLoaderTexture2D_LoadTexture_PatchTests
{
    private string tempDir;
    private FasterGameLoadingMod originalInstance;
    private bool originalVerboseLogging;
    private int originalRedirectTimeoutMs;
    private Func<VirtualFile, Texture2D> originalMainThreadLoad;
    private Func<string, Texture2D> originalLoadCacheFile;

    // 測試用的轉交逾時；正式值為 10 秒，會讓「泵送從未執行」的測試空等太久。
    private const int TestRedirectTimeoutMs = 1000;

    // 不觸碰檔案系統的 VirtualFile 替身，只需要 FullPath 可讀。
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
        public override Stream CreateReadStream() => new MemoryStream([]);
        public override byte[] ReadAllBytes() => [];
        public override string ReadAllText() => string.Empty;
        public override string[] ReadAllLines() => [];
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

        originalVerboseLogging = FasterGameLoadingSettings.VerboseLogging;
        originalInstance = FasterGameLoadingMod.Instance;
        originalRedirectTimeoutMs = MainThreadTextureLoader.RedirectTimeoutMs;
        originalMainThreadLoad = MainThreadTextureLoader.LoadOnMainThread;
        originalLoadCacheFile = ModContentLoaderTexture2D_LoadTexture_Patch.LoadCacheFile;
        MainThreadTextureLoader.RedirectTimeoutMs = TestRedirectTimeoutMs;

        // 預設由 FGL 負責載入貼圖，否則 Prefix 會在第一個分支就短路。
        TextureOwnership.OverrideForTests(TextureOwner.Fgl);
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
        ModContentLoaderTexture2D_LoadTexture_Patch.LoadCacheFile = originalLoadCacheFile;
        ClearPatchState();
        SessionLifecycle.Raise(LifecyclePhase.LanguageReloading);

        SetModInstance(originalInstance);
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
            // 防毒軟體等外部程序可能仍持有檔案控制代碼；殘留暫存目錄不影響斷言結果。
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

    // ── Prefix：降質快取命中 ──

    // 只含 PNG 簽章與 IHDR 尺寸的最小檔頭，足以通過 PngUtils 的標頭檢查。
    private static byte[] PngHeader(int width, int height)
    {
        var header = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(header, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20, 4), height);
        return header;
    }

    // 建立一筆降質快取項目與其快取檔，回傳原始貼圖路徑。
    private string WriteCacheEntry(string cacheFileName, byte[] contents)
    {
        string cachePath = Path.Combine(tempDir, cacheFileName);
        File.WriteAllBytes(cachePath, contents);
        string originalPath = Path.Combine(tempDir, "Textures", cacheFileName);
        FasterGameLoadingMod.Instance.CacheManager.SetCacheEntry(originalPath, cachePath);
        return originalPath;
    }

    [Test]
    public void Prefix_DownscaleCacheHit_LoadsCacheFileThroughVanillaFileLoader()
    {
        TestSetup.IsInMainThreadOverride = () => true;
        string originalPath = WriteCacheEntry("hit_cache.png", PngHeader(64, 32));
        string loadedFrom = null;
        var expected = NewDetachedTexture();
        ModContentLoaderTexture2D_LoadTexture_Patch.LoadCacheFile = path =>
        {
            loadedFrom = path;
            return expected;
        };
        int hitsBefore = ModContentLoaderTexture2D_LoadTexture_Patch.CacheLoadHits;

        Texture2D result = null;
        bool runOriginal = ModContentLoaderTexture2D_LoadTexture_Patch.Prefix(new FakeVirtualFile(originalPath), out _, ref result);

        Assert.That(runOriginal, Is.False);
        Assert.That(result, Is.SameAs(expected));
        Assert.That(loadedFrom, Is.EqualTo(Path.Combine(tempDir, "hit_cache.png")),
            "快取檔必須以檔案路徑交給原版載入，貼圖才帶有來源路徑，1.7 圖集才能使用磁碟快取。");
        Assert.That(ModContentLoaderTexture2D_LoadTexture_Patch.CacheLoadHits, Is.EqualTo(hitsBefore + 1));
        Assert.That(LoadedTextureRegistry.TryGetPath(expected, out var recorded), Is.True);
        Assert.That(recorded, Is.EqualTo(originalPath));
    }

    [Test]
    public void Prefix_DownscaleCacheWithInvalidPngHeader_DropsEntryAndRunsOriginal()
    {
        TestSetup.IsInMainThreadOverride = () => true;
        string originalPath = WriteCacheEntry("broken_cache.png", [9, 8, 7]);
        bool loaderCalled = false;
        ModContentLoaderTexture2D_LoadTexture_Patch.LoadCacheFile = _ =>
        {
            loaderCalled = true;
            return NewDetachedTexture();
        };
        int failuresBefore = ModContentLoaderTexture2D_LoadTexture_Patch.CacheLoadFailures;

        Texture2D result = null;
        bool runOriginal = ModContentLoaderTexture2D_LoadTexture_Patch.Prefix(new FakeVirtualFile(originalPath), out bool state, ref result);

        Assert.That(runOriginal, Is.True);
        Assert.That(state, Is.True);
        Assert.That(result, Is.Null);
        Assert.That(loaderCalled, Is.False, "1.7 的檔案載入延後才解碼，壞檔必須在交給它之前就被擋下。");
        Assert.That(ModContentLoaderTexture2D_LoadTexture_Patch.CacheLoadFailures, Is.EqualTo(failuresBefore + 1));
        Assert.That(FasterGameLoadingMod.Instance.CacheManager.TryGetCachedTexturePath(originalPath, out _), Is.False);
    }

    [Test]
    public void Prefix_DownscaleCacheLoaderThrows_DropsEntryAndRunsOriginal()
    {
        TestSetup.IsInMainThreadOverride = () => true;
        string originalPath = WriteCacheEntry("throw_cache.png", PngHeader(16, 16));
        ModContentLoaderTexture2D_LoadTexture_Patch.LoadCacheFile = _ => throw new IOException("locked");

        Texture2D result = null;
        bool runOriginal = ModContentLoaderTexture2D_LoadTexture_Patch.Prefix(new FakeVirtualFile(originalPath), out _, ref result);

        Assert.That(runOriginal, Is.True);
        Assert.That(result, Is.Null);
        Assert.That(FasterGameLoadingMod.Instance.CacheManager.TryGetCachedTexturePath(originalPath, out _), Is.False);
    }


    // ── 測試輔助 ──

    private static void ClearPatchState()
    {
        LoadedTextureRegistry.Clear();
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
}
