namespace FasterGameLoading;

// FasterGameLoading 內部共用常數。
public static class FGLConsts
{
    public const string ModName = "FasterGameLoading";
    public const int PlaceholderTextureSize = 2;

    public const string TextureCacheDir = "TextureCache";
    public const string TextureCacheStagingDir = "TextureCache_New";
    public const string HistoricalBakeSpeedsKey = "historicalBakeSpeeds";
    public const string LoadedTypesKey = "loadedTypesByFullNameSinceLastSession";
    public const string TypeCacheAssemblyFingerprintKey = "typeCacheAssemblyFingerprint";
    public const string ModsInLastSessionKey = "modsInLastSession";

    public const string TexturesDirName = "Textures";

    public const int AccessToolsPreloadDelayMs = 50;
    public const int TexturePreloadDelayMs = 150;
}
