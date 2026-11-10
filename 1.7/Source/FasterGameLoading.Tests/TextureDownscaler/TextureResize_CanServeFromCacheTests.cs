using System.IO;
using NUnit.Framework;
using Waylith.Graphics.Textures;
using Waylith.Rendering;

namespace FasterGameLoading.Tests.TextureDownscaler;

[TestFixture]
[NonParallelizable]
public class TextureResize_CanServeFromCacheTests
{
    private string file;

    [SetUp]
    public void SetUp()
    {
        file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".png");
        File.WriteAllBytes(file, [1]);
    }

    [TearDown]
    public void TearDown()
    {
        GpuTextureService.WantsBorderMipMaps = null;
        TextureCompressionSettings.KeepRaw = null;
        File.Delete(file);
    }

    [Test]
    public void FileOnDisk_IsAllowed() => Assert.That(TextureResize.CanServeFromCache(file), Is.True);

    // 打包在 Assets.zip 的貼圖（含官方內容）在磁碟上沒有檔案。
    [Test]
    public void FileNotOnDisk_IsRejected() => Assert.That(TextureResize.CanServeFromCache(file + ".missing.png"), Is.False);

    [Test]
    public void BorderMipMapsTexture_IsRejected()
    {
        GpuTextureService.WantsBorderMipMaps = path => path == Path.GetFullPath(file);
        Assert.That(TextureResize.CanServeFromCache(file), Is.False);
    }

    [Test]
    public void UncompressedTexture_IsRejected()
    {
        TextureCompressionSettings.KeepRaw = path => path == Path.GetFullPath(file);
        Assert.That(TextureResize.CanServeFromCache(file), Is.False);
    }
}
