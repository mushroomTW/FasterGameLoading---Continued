using NUnit.Framework;

namespace FasterGameLoading.Tests.Utilities;
[TestFixture]
public class FGLConstsTests
{
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
    }
}
