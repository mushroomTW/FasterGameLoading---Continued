using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimTestRedux;
using Verse;

namespace FasterGameLoading.InGameTests
{
    /// <summary>
    /// 型別快取的每個結果都要與原版（未套用 patch 的）GetTypeInAnyAssemblyInt 相同。
    /// 以 Harmony reverse patch 取得原始 IL 的副本當作對照組，在真實的 mod 組件組合上比對。
    /// </summary>
    [TestSuite]
    internal static class TypeLookupCacheTests
    {
        private const int WarmupSampleSize = 500;

        [Test]
        public static void ResolvedNamesMatchVanilla()
        {
            if (!FasterGameLoadingSettings.TypeLookupCache) return;

            // 預熱只寫入 FullName；經命名空間探測或短名稱解析出來的項目（鍵 ≠ FullName）風險最高，全部比對。
            var failures = new List<string>();
            foreach (var entry in GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults.ToArray())
            {
                if (IsPlainFullNameEntry(entry.Key, entry.Value)) continue;
                CompareWithVanilla(entry.Key, entry.Value, failures);
            }
            FglState.AssertNone(failures, "cached type lookups differing from vanilla");
        }

        /// <summary>
        /// 原版以 ignoreCase 依 GenTypes.AllActiveAssemblies 順序查詢；預熱則是區分大小寫、依 RunningMods 順序先到先得。
        /// 只有 FullName（忽略大小寫後）在多個型別間撞名時兩者才可能不同——含大小寫不同，以及同名型別出現在多個組件；
        /// 這些名稱從所有搜尋組件的型別重新找出並全部比對，其餘抽樣。
        /// </summary>
        [Test]
        public static void WarmedFullNamesMatchVanilla()
        {
            if (!FasterGameLoadingSettings.TypeLookupCache) return;

            var warmed = GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults.ToArray()
                .Where(static e => IsPlainFullNameEntry(e.Key, e.Value))
                .ToList();
            // 快取裡同名只留一個鍵，無法看出撞名；必須回到組件本身列舉。
            var collidingNames = new HashSet<string>(
                TypeLookupCache.SearchAssemblies()
                    .SelectMany(static a => AccessTools.GetTypesFromAssembly(a))
                    .Where(static t => !string.IsNullOrEmpty(t?.FullName))
                    .GroupBy(static t => t.FullName, StringComparer.OrdinalIgnoreCase)
                    .Where(static g => g.Count() > 1)
                    .SelectMany(static g => g.Select(static t => t.FullName)),
                StringComparer.Ordinal);

            int step = Math.Max(1, warmed.Count / WarmupSampleSize);
            var failures = new List<string>();
            for (int i = 0; i < warmed.Count; i++)
            {
                var entry = warmed[i];
                if (i % step == 0 || collidingNames.Contains(entry.Key))
                {
                    CompareWithVanilla(entry.Key, entry.Value, failures);
                }
            }
            FglState.AssertNone(failures, "warmed full-name lookups differing from vanilla");
        }

        /// <summary>跨 session 名稱對照（短名稱 → FullName）必須指向原版會解析到的同一個型別。</summary>
        [Test]
        public static void SessionMappingsMatchVanilla()
        {
            if (!FasterGameLoadingSettings.TypeLookupCache) return;

            var failures = new List<string>();
            foreach (var entry in GenTypes_GetTypeInAnyAssemblyInt_Patch.loadedTypesThisSession.ToArray())
            {
                var (typeName, ns) = SplitKey(entry.Key);
                var vanilla = VanillaLookup.GetTypeInAnyAssemblyInt(typeName, ns);
                if (vanilla?.FullName != entry.Value)
                {
                    failures.Add($"{entry.Key}: mapped '{entry.Value}', vanilla '{vanilla?.FullName ?? "null"}'");
                }
            }
            FglState.AssertNone(failures, "session type mappings differing from vanilla");
        }

        /// <summary>
        /// 上次 session 的對照已失效（例如 mod 把類別改名）時，Postfix 必須丟掉該對照並以原名重查。
        /// 隔離的測試 session 沒有上次的資料，這條路徑不會自然發生，因此直接植入一筆過期對照來觸發。
        /// </summary>
        [Test]
        public static void StaleSessionMappingFallsBackToVanilla()
        {
            if (!FasterGameLoadingSettings.TypeLookupCache) return;

            const string typeName = "CompProperties_Glower";
            var key = GenTypes_GetTypeInAnyAssemblyInt_Patch.MakeCacheKey(typeName);
            GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults.TryRemove(key, out _);
            TypeLookupCache.FullNamesFromLastSession[key] = "FasterGameLoading.InGameTests.Stale." + typeName;

            var resolved = GenTypes.GetTypeInAnyAssemblyInt(typeName, null);

            // Assert.That 只接受 IComparable，Type 改以布林比對。
            Assert.That(resolved != null).Is.True();
            Assert.That(resolved == VanillaLookup.GetTypeInAnyAssemblyInt(typeName, null)).Is.True();
            Assert.That(TypeLookupCache.FullNamesFromLastSession.ContainsKey(key)).Is.False();
        }

        private static bool IsPlainFullNameEntry(string key, Type type)
            => string.Equals(key, type?.FullName, StringComparison.Ordinal);

        private static void CompareWithVanilla(string key, Type cached, List<string> failures)
        {
            var (typeName, ns) = SplitKey(key);
            var vanilla = VanillaLookup.GetTypeInAnyAssemblyInt(typeName, ns);
            if (vanilla != cached)
            {
                failures.Add($"{key}: cached {Describe(cached)}, vanilla {Describe(vanilla)}");
            }
        }

        private static string Describe(Type type)
            => type == null ? "null" : $"{type.FullName} [{type.Assembly.GetName().Name}]";

        private static (string typeName, string ns) SplitKey(string key)
        {
            const string separator = "|ns|";
            int index = key.IndexOf(separator, StringComparison.Ordinal);
            return index < 0 ? (key, null) : (key.Substring(0, index), key.Substring(index + separator.Length));
        }
    }

    /// <summary>未套用任何 patch 的原版 GetTypeInAnyAssemblyInt 副本。</summary>
    [HarmonyPatch]
    internal static class VanillaLookup
    {
        [HarmonyReversePatch]
        [HarmonyPatch(typeof(GenTypes), nameof(GenTypes.GetTypeInAnyAssemblyInt))]
        public static Type GetTypeInAnyAssemblyInt(string typeName, string namespaceIfAmbiguous)
            => throw new NotImplementedException("Replaced by Harmony reverse patch");
    }

    /// <summary>
    /// 記錄每次實際執行的 ReloadContentInt 當下，FGL 的內容載入相關 patch 是否已經套用。
    /// 本測試 mod 刻意排在 FGL 之前載入，讓這個探針在 FGL 建構子（以及它建立的提早載入元件）之前就位。
    /// </summary>
    [HarmonyPatch(typeof(ModContentPack), nameof(ModContentPack.ReloadContentInt))]
    internal static class ContentLoadProbe
    {
        public static int Loads;

        /// <summary>由 FGL 提早載入（而非原版 ExecuteWhenFinished）觸發的載入次數。</summary>
        public static int EarlyLoads;

        /// <summary>載入當下缺少任一 FGL patch 的紀錄（已格式化成失敗訊息）。</summary>
        public static readonly List<string> LoadedBeforePatches = new List<string>();

        /// <summary>由 FGL 提早載入的 mod，供相容性測試確認排除名單內的 mod 沒被提早載入。</summary>
        public static readonly List<ModContentPack> EarlyLoadedMods = new List<ModContentPack>();

        [HarmonyPriority(Priority.First)]
        public static void Prefix(ModContentPack __instance, bool hotReload)
        {
            // 已載入的 mod 會被 FGL 的 prefix 略過，不算一次實際載入。
            if (hotReload || ModContentPack_ReloadContentInt_Patch.loadedMods.Contains(__instance)) return;
            bool reloadContentInt = FglState.HasFglPatch(AccessTools.Method(typeof(ModContentPack), nameof(ModContentPack.ReloadContentInt)));
            bool reloadAll = FglState.HasFglPatch(AccessTools.Method(typeof(ModAssetBundlesHandler), nameof(ModAssetBundlesHandler.ReloadAll)));
            bool loadTexture = FglState.HasFglPatch(AccessTools.Method(typeof(ModContentLoader<UnityEngine.Texture2D>), nameof(ModContentLoader<UnityEngine.Texture2D>.LoadTexture)));
            lock (LoadedBeforePatches)
            {
                Loads++;
                if (EarlyLoadMarker.Active)
                {
                    EarlyLoads++;
                    EarlyLoadedMods.Add(__instance);
                }
                if (!reloadContentInt || !reloadAll || !loadTexture)
                {
                    LoadedBeforePatches.Add($"{__instance.PackageIdPlayerFacing} (mainThread={UnityData.IsInMainThread}, ReloadContentInt={reloadContentInt}, ReloadAll={reloadAll}, LoadTexture={loadTexture})");
                }
            }
        }
    }

    /// <summary>標記目前執行緒正處於 FGL 提早載入的 ReloadContentInt 呼叫中，供 <see cref="ContentLoadProbe"/> 分類。</summary>
    // 掛在 LoadOneModContent：它自行攔下所有例外，postfix 一定會執行；
    // 不掛 InvokeReloadContentInt，Harmony 無法替含 exception filter（catch when）的方法產生 finalizer。
    [HarmonyPatch(typeof(EarlyModContentLoader), nameof(EarlyModContentLoader.LoadOneModContent))]
    internal static class EarlyLoadMarker
    {
        [ThreadStatic]
        public static bool Active;

        public static void Prefix() => Active = true;

        public static void Postfix() => Active = false;
    }

    /// <summary>
    /// 攔截原版在重複載入時才會發出的錯誤：DefDatabase 遇到同名 Def 會改名後照樣加入、
    /// 同一個 AssetBundle 第二次載入會失敗並留下錯誤，兩者都無法從最終狀態看出，只能從錯誤訊息得知。
    /// </summary>
    [HarmonyPatch(typeof(Log), nameof(Log.Error), typeof(string))]
    internal static class DuplicateLoadErrorProbe
    {
        public static readonly List<string> DuplicateDefs = new List<string>();
        public static readonly List<string> FailedBundles = new List<string>();

        public static void Prefix(string text)
        {
            if (text == null) return;
            if (text.StartsWith("Adding duplicate ", StringComparison.Ordinal))
            {
                lock (DuplicateDefs) DuplicateDefs.Add(text);
            }
            else if (text.StartsWith("Could not load asset bundle at ", StringComparison.Ordinal))
            {
                lock (FailedBundles) FailedBundles.Add(text);
            }
        }
    }

    /// <summary>
    /// 攔截原版 ModContentHolder.ReloadAll 的「Tried to load duplicate」：同一個內容路徑第二次載入時，
    /// 原版只記一筆 Warning（不是 Error，<see cref="DuplicateLoadErrorProbe"/> 看不到）；
    /// 第二份貼圖、音效或字串已從磁碟讀進記憶體，原版既不把它加入 contentList，也不釋放它，最終狀態看不出來。
    ///
    /// Catches vanilla ModContentHolder.ReloadAll's "Tried to load duplicate": when a content path is loaded
    /// a second time, vanilla logs one Warning (not an Error, which <see cref="DuplicateLoadErrorProbe"/> would
    /// see). The second texture, sound or string has already been read from disk, and vanilla neither adds it
    /// to contentList nor disposes it, so the final state shows nothing.
    /// </summary>
    [HarmonyPatch(typeof(Log), nameof(Log.Warning), typeof(string))]
    internal static class DuplicateContentWarningProbe
    {
        private const string DuplicateWarning = "Tried to load duplicate ";

        public static readonly List<string> DuplicateContent = new List<string>();

        public static void Prefix(string text)
        {
            if (text != null && text.StartsWith(DuplicateWarning, StringComparison.Ordinal))
            {
                lock (DuplicateContent) DuplicateContent.Add(text.Substring(DuplicateWarning.Length));
            }
        }
    }

    /// <summary>提早載入與重複載入防護的最終狀態。</summary>
    [TestSuite]
    internal static class EarlyLoadingTests
    {
        [Test]
        public static void EarlyLoadingCompleted()
        {
            Assert.That(FasterGameLoadingMod.delayedActions.earlyLoadingComplete).Is.True();
        }

        /// <summary>每個執行中的 mod 都要經過（提早或原版的）ReloadContentInt，漏掉的 mod 沒有貼圖與音效。</summary>
        [Test]
        public static void EveryRunningModContentWasLoaded()
        {
            var failures = LoadedModManager.RunningMods
                .Where(static mod => !ModContentPack_ReloadContentInt_Patch.loadedMods.Contains(mod))
                .Select(static mod => mod.PackageIdPlayerFacing)
                .ToList();
            FglState.AssertNone(failures, "running mods whose content was never loaded");
        }

        /// <summary>
        /// Mod 建構子在事件緒執行，FGL 的提早載入卻在主執行緒的 LateUpdate 進行；
        /// 若它在 FGL（或其他 mod）的 Harmony patch 套用完成前就開始載入內容，這些 patch 對已載入的 mod 全部無效。
        /// </summary>
        [Test]
        public static void ContentIsOnlyLoadedAfterFglPatchesAreApplied()
        {
            List<string> failures;
            lock (ContentLoadProbe.LoadedBeforePatches)
            {
                Assert.That(ContentLoadProbe.Loads).Is.GreaterThan(0);
                failures = ContentLoadProbe.LoadedBeforePatches.ToList();
            }
            FglState.AssertNone(failures, "mod contents loaded before FGL's patches were applied");
        }

        /// <summary>
        /// 提早載入的閘門（LoadModXML prefix）由 HyperdriveCompat 手動套用，可能延到 Hyperdrive 建構後才掛上；
        /// 實機上仍須由 FGL 持有且最先執行，否則其他 mod 的 prefix 回傳 false 時閘門不會開。
        /// </summary>
        [Test]
        public static void LoadModXMLGateIsPatchedFirst()
        {
            var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.LoadModXML)));
            var priorities = info?.Prefixes
                .Where(static p => string.Equals(p.owner, FglState.HarmonyId, StringComparison.Ordinal))
                .Select(static p => p.priority)
                .ToList() ?? new List<int>();
            Assert.That(priorities.Count).Is.EqualTo(1);
            Assert.That(priorities.FirstOrDefault()).Is.EqualTo(Priority.First);
            Assert.That(FglState.LoadModXMLPrefixOwnersInRunOrder().FirstOrDefault()).Is.EqualTo(FglState.HarmonyId);
            // 讓出 Defs/ 只限 LoadModXML 期間：載入完成後必須已由 LoadModXML 的 finalizer 歸零，不影響之後的呼叫。
            Assert.That(HyperdriveCompat.ParallelizesModDefs).Is.False();
        }

        /// <summary>提早載入延到所有 Mod 建構子之後才開始，但仍須在原版載入前實際載入內容，否則加速效果消失。</summary>
        [Test]
        public static void EarlyLoadingActuallyLoadsModContent()
        {
            if (!FasterGameLoadingSettings.earlyModContentLoading) return;
            lock (ContentLoadProbe.LoadedBeforePatches)
            {
                Assert.That(ContentLoadProbe.EarlyLoads).Is.GreaterThan(0);
            }
        }

        /// <summary>
        /// 每個 handler 都要經過 FGL 的 ReloadAll 防重複機制；重複呼叫 ReloadAll 會讓原版記下「Could not load asset bundle」。
        /// </summary>
        [Test]
        public static void EveryAssetBundleHandlerReloadedWithoutReloadErrors()
        {
            var failures = LoadedModManager.RunningMods
                .Where(static mod => !ModAssetBundlesHandler_ReloadAll_Patch.reloadedHandlers.Contains(mod.assetBundles))
                .Select(static mod => $"{mod.PackageIdPlayerFacing}: handler never reloaded")
                .ToList();
            lock (DuplicateLoadErrorProbe.FailedBundles) failures.AddRange(DuplicateLoadErrorProbe.FailedBundles);
            FglState.AssertNone(failures, "asset bundle handler problems");
        }

        /// <summary>原版遇到同名 Def 會改名後照樣加入資料庫，重複載入只能從「Adding duplicate」錯誤得知。</summary>
        [Test]
        public static void NoDuplicateDefsWereAdded()
        {
            List<string> failures;
            lock (DuplicateLoadErrorProbe.DuplicateDefs) failures = DuplicateLoadErrorProbe.DuplicateDefs.ToList();
            FglState.AssertNone(failures, "duplicate defs");
        }

        /// <summary>
        /// 同一個 mod 的內容被載入兩次時，原版只記 Warning，第二份既不加入 contentList 也不釋放，最終狀態看不出來。
        /// 例如 Loading Progress 沒偵測到 FGL 時，會自己再載入一次 FGL 已提早載入的每個 mod 的內容：
        /// 228 個 mod 的清單上有 24,683 筆，全都從磁碟多讀了一次（log 在上限前只記得下其中約一萬筆）。
        /// 例外：原版列檔時以含副檔名的路徑去重，ReloadAll 比對時卻去掉副檔名，
        /// 因此 mod 自帶同名不同副檔名的檔案（如 Foo.png 與 Foo.jpg）即使只載入一次也會觸發此警告，與 FGL 無關。
        ///
        /// When a mod's content is loaded twice, vanilla only logs a Warning, and the second copy is neither added
        /// to contentList nor disposed, so the final state shows nothing. Loading Progress, for one, reloads the
        /// content of every mod that FGL's early loading already loaded when it does not detect FGL: 24,683 files
        /// on a 228-mod list, each read from disk a second time (the log keeps only about 10,000 of them before its cap).
        /// Exception: vanilla lists files by path with extension but ReloadAll compares without it, so a mod that
        /// ships files differing only in extension (such as Foo.png and Foo.jpg) triggers this warning even though
        /// each is loaded once; that is not FGL's doing.
        /// </summary>
        [Test]
        public static void NoModContentWasLoadedTwice()
        {
            List<string> failures;
            lock (DuplicateContentWarningProbe.DuplicateContent) failures = DuplicateContentWarningProbe.DuplicateContent.ToList();
            FglState.AssertNone(failures, "mod content files loaded twice", maxListed: 5);
        }
    }
}
