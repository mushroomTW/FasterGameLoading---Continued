namespace FasterGameLoading
{
    /// <summary>
    /// FasterGameLoading 內部共用常數。
    /// </summary>
    public static class FGLConsts
    {
        public const string ModName = "FasterGameLoading";
        public const int PlaceholderTextureSize = 2;

        public const string BuildingPipe = "Building_Pipe";
        public const string MedicineDefName = "Medicine";
        public static readonly string[] FurnitureKeywords = { "Furniture", "Production", "Security" };

        public const string TextureCacheDir = "TextureCache";
        public const string TextureCacheStagingDir = "TextureCache_New";
        public const string HistoricalBakeSpeedsKey = "historicalBakeSpeeds";
        public const string LoadedTexturesKey = "loadedTexturesSinceLastSession";
        public const string LoadedTypesKey = "loadedTypesByFullNameSinceLastSession";
        public const string XmlPathsKey = "xmlPathsSinceLastSession";
        public const string ModsInLastSessionKey = "modsInLastSession";

        public const string UIDirSlash = "/UI/";
        public const string TexturesDirSlash = "Textures/";

        public const int AccessToolsPreloadDelayMs = 50;
        public const int TexturePreloadDelayMs = 150;

        public const string DefsDirName = "Defs";
        public const string PatchesDirName = "Patches";
    }
}
