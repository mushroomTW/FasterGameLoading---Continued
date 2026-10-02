using System;
using System.Collections.Generic;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// FGL 必須讓開的 Mod，分兩種保護：
    /// - 貼圖保護：外星人種族（HAR）、Ancot 函式庫及其衍生，加上 bionic icons。
    ///   它們的 bodyAddon、頭髮、耳朵與多遮罩貼圖不降質、不進 registry，開啟自適應烘焙時也不進靜態圖集。
    /// - 提早載入保護：Ayameduki 與 AyaTweaks（WRK.）系列；HAR 及其衍生只在無法延後 HAR 的貼圖變體掃描時
    ///   （見 <see cref="AlienRaceGraphicsHookGate"/>）才列入。它們的內容不提早載入，XML 也不平行解析。
    /// 判斷結果依 mod 快取，語言切換時重算。
    /// </summary>
    public static class ProtectedMods
    {
        private const string AncotLibraryPackageId = "Ancot.AncotLibrary";

        private static readonly HashSet<string> textureProtectedPackageIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "automatic.bionicicons",
            ModDependencyReflection.AlienRacesPackageId,
            ModDependencyReflection.AlienRacesDevPackageId,
            AncotLibraryPackageId,
        };

        // ── 貼圖保護 ──
        private static readonly object rootsLock = new object();
        private static readonly HashSet<string> protectedTextureRoots = new(StringComparer.OrdinalIgnoreCase);
        private static volatile bool rootsInitialized;

        // ── 提早載入保護 ──
        private static readonly Dictionary<ModContentPack, bool> skipEarlyLoadCache = new();
        private static readonly object skipEarlyLoadCacheLock = new();

        static ProtectedMods()
        {
            SessionLifecycle.On(LifecyclePhase.LanguageReloading, static () =>
            {
                lock (rootsLock)
                {
                    protectedTextureRoots.Clear();
                    rootsInitialized = false;
                }
                lock (skipEarlyLoadCacheLock)
                {
                    skipEarlyLoadCache.Clear();
                }
            });
        }

        /// <summary>貼圖路徑是否位於受貼圖保護的 Mod 資料夾內（不降質、不進 registry）。</summary>
        public static bool IsProtectedTexturePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            InitializeTextureRoots();

            string normalizedPath = path.Replace('\\', '/');
            // 直接在鎖內走訪，不另外複製一份清單：本方法是每張貼圖都會走的熱路徑，
            // 而下方只做純字串比對，持鎖期間不會回呼外部程式碼。
            lock (rootsLock)
            {
                foreach (var root in protectedTextureRoots)
                {
                    if (normalizedPath.Equals(root, StringComparison.OrdinalIgnoreCase)
                        || normalizedPath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>貼圖是否要排除在靜態圖集之外：只在本次啟用自適應烘焙時，對受貼圖保護的 Mod 生效。</summary>
        public static bool ShouldSkipBaking(string path)
        {
            return DelayedActions.AdaptiveBakingEnabled && IsProtectedTexturePath(path);
        }

        /// <summary>Mod 是否不提早載入、XML 不平行解析。</summary>
        public static bool ShouldSkipEarlyLoad(ModContentPack mod)
        {
            if (mod == null) return false;
            lock (skipEarlyLoadCacheLock)
            {
                if (skipEarlyLoadCache.TryGetValue(mod, out var cached)) return cached;
            }

            var shouldSkip = ShouldSkipEarlyLoad(mod.PackageIdPlayerFacing, mod.ModMetaData)
                || (!string.Equals(mod.PackageIdPlayerFacing, mod.PackageId, StringComparison.OrdinalIgnoreCase) && ShouldSkipEarlyLoad(mod.PackageId, mod.ModMetaData));

            lock (skipEarlyLoadCacheLock)
            {
                skipEarlyLoadCache[mod] = shouldSkip;
            }

            return shouldSkip;
        }

        /// <summary>依 packageId 與相依清單判斷是否不提早載入；<paramref name="metaData"/> 為 ModMetaData 或同形物件。</summary>
        public static bool ShouldSkipEarlyLoad(string packageId, object metaData = null)
        {
            if (string.IsNullOrEmpty(packageId)) return false;
            if (packageId.StartsWith("Ayameduki.", StringComparison.OrdinalIgnoreCase)) return true;
            if (packageId.StartsWith("WRK.", StringComparison.OrdinalIgnoreCase)) return true;
            // HAR 的變體掃描已延後到所有內容載入完成，HAR 與其衍生不必再讓開。
            if (AlienRaceGraphicsHookGate.DefersGraphicsHook) return false;
            return ModDependencyReflection.IsAlienRaces(packageId) || ModDependencyReflection.DependsOnAlienRaces(metaData);
        }

        /// <summary>
        /// 單一 Mod 是否受貼圖保護：命中名單、為外星人種族衍生、或依賴 Ancot 函式庫。
        /// 同一個 mod 同時有本機與 Workshop 副本時，Workshop 版的 PackageId 會帶 "_steam" 後綴，
        /// 故同時比對不帶後綴的 PackageIdPlayerFacing。
        /// </summary>
        private static bool IsTextureProtected(ModContentPack mod)
        {
            if (mod == null) return false;

            if (IsListedTextureProtected(mod.PackageIdPlayerFacing) || IsListedTextureProtected(mod.PackageId)) return true;

            return ModDependencyReflection.DependsOnAlienRaces(mod.ModMetaData)
                || ModDependencyReflection.DependsOnMod(mod.ModMetaData, AncotLibraryPackageId);
        }

        private static bool IsListedTextureProtected(string packageId)
            => packageId != null && textureProtectedPackageIds.Contains(packageId);

        /// <summary>從執行中的 mod 清單建立受貼圖保護的根目錄；清單尚未建立時下次再試。</summary>
        internal static void InitializeTextureRoots()
        {
            if (rootsInitialized) return;
            var mods = LoadedModManager.RunningMods;
            if (mods == null) return;

            lock (rootsLock)
            {
                if (rootsInitialized) return;
                try
                {
                    bool hasAny = false;
                    foreach (var mod in mods)
                    {
                        hasAny = true;
                        if (IsTextureProtected(mod))
                        {
                            if (string.IsNullOrEmpty(mod.RootDir)) continue;
                            protectedTextureRoots.Add(mod.RootDir.Replace('\\', '/').TrimEnd('/'));
                        }
                    }

                    if (!hasAny) return; // 載入列表尚未初始化完畢（空集合），下次再來

                    // 迴圈順利完成後才標記初始化，避免例外導致半初始化狀態被永久鎖定
                    rootsInitialized = true;
                }
                catch (Exception ex)
                {
                    FGLLog.Error("Error initializing target mod roots:", ex);
                }
            }
        }

        /// <summary>目前已建立的受貼圖保護根目錄快照（正斜線、無結尾斜線）。</summary>
        internal static IReadOnlyCollection<string> GetProtectedTextureRoots()
        {
            lock (rootsLock) return new List<string>(protectedTextureRoots);
        }

        /// <summary>測試用：直接指定受貼圖保護的根目錄並視為已初始化，繞過需要 RunningMods 的探測。</summary>
        internal static void SetProtectedTextureRootsForTests(params string[] roots)
        {
            lock (rootsLock)
            {
                protectedTextureRoots.Clear();
                protectedTextureRoots.UnionWith(roots);
                rootsInitialized = true;
            }
        }
    }
}
