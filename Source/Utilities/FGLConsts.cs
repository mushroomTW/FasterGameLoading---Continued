namespace FasterGameLoading
{
    /// <summary>
    /// 集中宣告 FasterGameLoading 中所有的字串與反射常數，以消除程式碼中的魔法字串。
    /// </summary>
    public static class FGLConsts
    {
        // ── 專案與類群名稱常數 ──
        public const string LogPrefix = "[FasterGameLoading] ";
        public const string ModName = "FasterGameLoading";
        public const string SoundStarterCategory = "SoundStarter";
        public const int PlaceholderTextureSize = 2;

        // ── Def 與型別匹配關鍵字 ──
        public const string BuildingPipe = "Building_Pipe";
        public const string MedicineDefName = "Medicine";
        public const string FurnitureKeyword = "Furniture";
        public const string ProductionKeyword = "Production";
        public const string SecurityKeyword = "Security";

        public static readonly string[] FurnitureKeywords = new[]
        {
            FurnitureKeyword,
            ProductionKeyword,
            SecurityKeyword
        };



        // ── 快取目錄與序列化標記鍵值 ──
        public const string TextureCacheDir = "TextureCache";
        public const string TextureCacheStagingDir = "TextureCache_New";
        public const string HistoricalBakeSpeedsKey = "historicalBakeSpeeds";
        public const string LoadedTexturesKey = "loadedTexturesSinceLastSession";
        public const string LoadedTypesKey = "loadedTypesByFullNameSinceLastSession";
        public const string XmlPathsKey = "xmlPathsSinceLastSession";
        public const string ModsInLastSessionKey = "modsInLastSession";

        // ── Alien Races 反射字串常數 ──
        public const string AlienRaceAssemblyName = "AlienRace";
        public const string AlienPartGeneratorTypeName = "AlienRace.AlienPartGenerator";
        public const string GraphicsQueueFieldName = "graphicsQueue";
        public const string CountPropertyName = "Count";
        public const string ThingDefAlienRaceTypeName = "AlienRace.ThingDef_AlienRace";
        public const string AddMethodName = "Add";
        public const string AlienRaceFieldName = "alienRace";
        public const string GeneralSettingsPropertyName = "generalSettings";
        public const string AlienPartGeneratorPropertyName = "alienPartGenerator";
        public const string LoadGraphicsHookMethodName = "LoadGraphicsHook";

        // ── 紋理路徑字首與目錄匹配 ──
        public const string UIDirSlash = "/UI/";
        public const string TexturesDirSlash = "Textures/";

        // ── JIT 排除 Assembly 常數 ──
        public static readonly string[] IgnoredAssemblyPrefixes = new[]
        {
            "System",
            "Microsoft",
            "Unity"
        };

        public static readonly System.Collections.Generic.HashSet<string> IgnoredAssemblyExactNames = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            "mscorlib",
            "Assembly-CSharp",
            "0Harmony",
            "Mono.Cecil",
            "Anonymously Hosted DynamicMethods Assembly",
            "AlteredCarbon",
            "AlteredCarbonExtra",
            "0ModSettingsFramework"
        };

        // ── 背景延遲時間常數 ──
        public const int AccessToolsPreloadDelayMs = 50;
        public const int TexturePreloadDelayMs = 150;

        // ── 外部 Mod 相容與 XML 目錄常數 ──
        public const string BionicIconsModId = "bionicicons";
        public const string DefsDirName = "Defs";
        public const string PatchesDirName = "Patches";
    }
}
