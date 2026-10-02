using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimTestRedux;
using Verse;

namespace FasterGameLoading.InGameTests
{
    /// <summary>
    /// 切換語言會跑 ClearAllPlayData + LoadAllPlayData：所有 ModContentPack、Def 與貼圖重建，CallAll 與 FGL 的延遲管線再跑一次，
    /// 但 Mod 類別不會重建（FGL 建構子不再執行）。FGL 靠 SessionLifecycle 的 LanguageReloading 在 SelectLanguage 時清掉所有快取。
    /// 第 1 輪結束後由 <see cref="TestRunDriver"/> 呼叫 <see cref="TryStart"/>，第 2 輪重跑全部測試並加跑本 suite。
    /// </summary>
    internal static class LanguageReload
    {
        /// <summary>選一個 FGL 沒有提供翻譯的語言，讓第 2 輪同時走原版 Translate() 的英文退回。</summary>
        private const string TargetLanguagePrefix = "Japanese";

        public static string FromLanguage { get; private set; }

        public static string ToLanguage { get; private set; }

        /// <summary>切換前 FGL 提早載入的次數；重載期間提早載入不應重啟。</summary>
        public static int EarlyLoadsBeforeReload { get; private set; }

        /// <summary>
        /// 切換前已存在的靜態圖集。原版重載不會清空 staticTextureAtlases（只清 buildQueue），
        /// 舊圖集會一直留著，而 Core 貼圖來自 Resources、重載後仍是同一批實體，因此新舊圖集必然重複；
        /// 第 2 輪的圖集檢查排除這些實體，只看本次載入烘焙的圖集（以參考比對，不假設清單只會往後附加）。
        /// </summary>
        public static HashSet<StaticTextureAtlas> AtlasesBeforeReload { get; } = new HashSet<StaticTextureAtlas>();

        public static bool TryStart()
        {
            // 遊戲中切換語言會清掉 Current.Game；quicktest 回合只跑一輪。
            if (FglState.Quicktest) return false;

            // 例如 HugsLib 會把切換語言改成重新啟動整個遊戲：新語言已寫入設定，重啟後又會再切換、再重啟，形成無限循環。
            var selectLanguage = AccessTools.Method(typeof(LanguageDatabase), nameof(LanguageDatabase.SelectLanguage));
            var otherOwners = Harmony.GetPatchInfo(selectLanguage)?.Owners.Where(static o => o != FglState.HarmonyId).ToList();
            if (otherOwners?.Count > 0)
            {
                Log.Warning($"[FGL InGameTests] LanguageDatabase.SelectLanguage is also patched by {string.Join(", ", otherOwners)}; skipping the language reload round.");
                return false;
            }
            // 萬一仍有其他 mod 把重載變成重啟，第 1 輪時語言就已經是目標語言；不再切換，避免循環。
            if (LanguageDatabase.activeLanguage.folderName.StartsWith(TargetLanguagePrefix, StringComparison.Ordinal))
            {
                Log.Error($"[FGL InGameTests] Active language is already {LanguageDatabase.activeLanguage.folderName} before the reload round (did a previous switch restart the game?); skipping the language reload round.");
                return false;
            }

            var target = LanguageDatabase.AllLoadedLanguages.FirstOrDefault(static l => l.folderName.StartsWith(TargetLanguagePrefix, StringComparison.Ordinal));
            if (target == null)
            {
                Log.Error($"[FGL InGameTests] No '{TargetLanguagePrefix}' language found; skipping the language reload round.");
                return false;
            }

            FromLanguage = LanguageDatabase.activeLanguage.folderName;
            ToLanguage = target.folderName;
            lock (ContentLoadProbe.LoadedBeforePatches) EarlyLoadsBeforeReload = ContentLoadProbe.EarlyLoads;
            AtlasesBeforeReload.UnionWith(GlobalTextureAtlasManager.staticTextureAtlases);
            Log.Message($"[FGL InGameTests] Switching language {FromLanguage} -> {ToLanguage} to exercise the full reload path.");
            LanguageDatabase.SelectLanguage(target);
            return true;
        }
    }

    /// <summary>只在第 2 輪（語言重載後）才有意義的檢查；第 1 輪直接略過。</summary>
    [TestSuite]
    internal static class LanguageReloadTests
    {
        private static bool AfterReload => TestRunDriver.Round == 2;

        [Test]
        public static void LanguageWasSwitched()
        {
            if (!AfterReload) return;
            Assert.That(LanguageDatabase.activeLanguage.folderName).Is.EqualTo(LanguageReload.ToLanguage);
        }

        /// <summary>第 2 輪的語言沒有 FGL 翻譯資料夾，TranslationTests 才真的走到原版英文退回。</summary>
        [Test]
        public static void ReloadRoundExercisesEnglishFallback()
        {
            if (!AfterReload) return;
            var candidate = Path.Combine(FasterGameLoadingMod.Instance.Content.RootDir, "LanguageData", LanguageDatabase.activeLanguage.folderName);
            Assert.That(Directory.Exists(candidate)).Is.False();
        }

        /// <summary>重載時 RunningMods 是一批新的 ModContentPack；FGL 不重啟提早載入，全部交給原版流程。</summary>
        [Test]
        public static void EarlyLoadingDidNotRestart()
        {
            if (!AfterReload) return;
            lock (ContentLoadProbe.LoadedBeforePatches)
            {
                Assert.That(ContentLoadProbe.EarlyLoads).Is.EqualTo(LanguageReload.EarlyLoadsBeforeReload);
            }
        }

        /// <summary>LanguageReloading 沒清掉的話，舊 ModContentPack／handler 會留在集合裡，新的同名 mod 則可能被誤判為已載入。</summary>
        [Test]
        public static void ContentTrackingOnlyHoldsCurrentModPacks()
        {
            if (!AfterReload) return;
            var running = LoadedModManager.RunningMods.ToHashSet();
            var handlers = running.Select(static m => m.assetBundles).ToHashSet();
            var failures = ModContentPack_ReloadContentInt_Patch.loadedMods
                .Where(m => !running.Contains(m))
                .Select(static m => $"stale content pack {m.PackageIdPlayerFacing}")
                .Concat(ModAssetBundlesHandler_ReloadAll_Patch.reloadedHandlers
                    .Where(h => !handlers.Contains(h))
                    .Select(static _ => "stale asset bundle handler"))
                .ToList();
            FglState.AssertNone(failures, "entries from before the reload");
        }

        /// <summary>重載會銷毀所有舊貼圖；FGL 的貼圖快取若還留著它們，之後命中就會拿到已銷毀的貼圖。</summary>
        [Test]
        public static void TextureCacheHoldsNoDestroyedTextures()
        {
            if (!AfterReload) return;
            var failures = LoadedTextureRegistry.Snapshot()
                .Where(static e => e.Key == null)
                .Select(static e => e.Value)
                .ToList();
            FglState.AssertNone(failures, "destroyed textures still cached");
        }

        [Test]
        public static void EarlyLoadGateReopenedAfterReload()
        {
            if (!AfterReload) return;
            Assert.That(EarlyModContentLoader.ModClassesCreated).Is.True();
        }
    }
}
