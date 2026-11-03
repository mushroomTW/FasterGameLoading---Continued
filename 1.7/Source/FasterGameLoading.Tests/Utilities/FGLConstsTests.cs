using NUnit.Framework;

namespace FasterGameLoading.Tests.Utilities;
[TestFixture]
public class FGLConstsTests
{
    // 期望值陣列提為欄位：避免每次斷言重新配置常數陣列 (CA1861)
    private static readonly string[] ExpectedFurnitureKeywords = ["Furniture", "Production", "Security"];

    [Test]
    public void Constants_ExposeStableCacheAndDirectoryContract()
    {
        Assert.That(FGLConsts.ModName, Is.EqualTo("FasterGameLoading"));
        Assert.That(FGLConsts.TextureCacheDir, Is.EqualTo("TextureCache"));
        Assert.That(FGLConsts.TextureCacheStagingDir, Is.EqualTo("TextureCache_New"));
        Assert.That(FGLConsts.TexturesDirName, Is.EqualTo("Textures"));
    }

    [Test]
    public void Constants_ExposeExpectedTextureAndReflectionValues()
    {
        Assert.That(FGLConsts.PlaceholderTextureSize, Is.EqualTo(2));
        Assert.That(FGLConsts.AccessToolsPreloadDelayMs, Is.EqualTo(50));
        Assert.That(FGLConsts.TexturePreloadDelayMs, Is.EqualTo(150));
        Assert.That(FGLConsts.FurnitureKeywords, Is.EqualTo(ExpectedFurnitureKeywords));
    }
}
