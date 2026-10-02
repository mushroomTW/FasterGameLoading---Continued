using System.Collections.Generic;
using System.IO;
using RimTestRedux;
using Verse;

namespace FasterGameLoading.InGameTests
{
    /// <summary>
    /// FGL 的翻譯放在原版不會掃描的 LanguageData/，由 TranslationInjector 在 CallAll 後注入目前語言與預設語言（英文）；
    /// 目前語言沒有對應資料夾時不注入，由原版 Translate() 逐一退回預設語言。
    /// 第 1 輪（英文）走直接對應，第 2 輪切換到沒有 FGL 翻譯的語言，走原版英文退回。
    /// </summary>
    [TestSuite]
    internal static class TranslationTests
    {
        [Test]
        public static void FglKeyedStringsAreInjectedForDefaultLanguage()
        {
            AssertInjected(LanguageDatabase.defaultLanguage);
        }

        [Test]
        public static void FglKeyedStringsAreInjectedForActiveLanguageWhenProvided()
        {
            var language = LanguageDatabase.activeLanguage;
            if (KeyedDirectory(language) == null) return;
            AssertInjected(language);
        }

        /// <summary>FGL 沒有提供的語言：目前語言不含 FGL 的 Key，Translate() 交由原版退回英文。</summary>
        [Test]
        public static void UnprovidedLanguageFallsBackToEnglishThroughVanilla()
        {
            var language = LanguageDatabase.activeLanguage;
            if (KeyedDirectory(language) != null) return;

            var failures = new List<string>();
            foreach (var pair in ReadKeyed(KeyedDirectory(LanguageDatabase.defaultLanguage)))
            {
                // 其他翻譯模組提供的值依原版優先，只確認不是 FGL 注入的
                if (language.keyedReplacements.TryGetValue(pair.key, out var replacement))
                {
                    if (IsFromFgl(replacement))
                        failures.Add($"{pair.key} injected into {language.folderName}");
                    continue;
                }
                // 不比對 Translate() 的文字：開發者模式下原版退回路徑會回傳偽翻譯
                if (!LanguageDatabase.defaultLanguage.TryGetTextFromKey(pair.key, out _))
                    failures.Add($"{pair.key}: missing from default language, Translate() cannot fall back");
            }
            FglState.AssertNone(failures, $"FGL keyed strings fallback for {language.folderName}");
        }

        private static void AssertInjected(LoadedLanguage language)
        {
            var keyedDir = KeyedDirectory(language);
            Assert.That(keyedDir != null).Is.True();

            var failures = new List<string>();
            int injectedFromFgl = 0;
            foreach (var pair in ReadKeyed(keyedDir))
            {
                if (!language.keyedReplacements.TryGetValue(pair.key, out var replacement))
                {
                    failures.Add($"{pair.key} missing");
                    continue;
                }
                // 其他來源定義的鍵刻意不覆寫，只檢查由 FGL 檔案注入的值
                if (!IsFromFgl(replacement)) continue;
                injectedFromFgl++;
                var isPlaceholder = pair.value == "TODO";
                if (replacement.isPlaceholder != isPlaceholder || replacement.value != (isPlaceholder ? "" : pair.value))
                {
                    failures.Add($"{pair.key}: '{replacement.value}', file '{pair.value}'");
                }
            }
            FglState.AssertNone(failures, $"FGL keyed strings not injected for {language.folderName}");
            Assert.That(injectedFromFgl).Is.GreaterThan(0);
        }

        private static IEnumerable<DirectXmlLoaderSimple.XmlKeyValuePair> ReadKeyed(string keyedDir)
        {
            foreach (var file in Directory.GetFiles(keyedDir, "*.xml", SearchOption.AllDirectories))
            {
                foreach (var pair in DirectXmlLoaderSimple.ValuesFromXmlFile(File.ReadAllText(file)))
                    yield return pair;
            }
        }

        /// <summary>路徑正規化後比對，不依賴注入端的字串格式。</summary>
        private static bool IsFromFgl(LoadedLanguage.KeyedReplacement replacement)
            => replacement.fileSourceFullPath != null
               && NormalizedFullPath(replacement.fileSourceFullPath).StartsWith(NormalizedFullPath(LanguageDataDirectory()), System.StringComparison.Ordinal);

        private static string NormalizedFullPath(string path)
            => Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant();

        private static string LanguageDataDirectory()
            => Path.Combine(FasterGameLoadingMod.Instance.Content.RootDir, "LanguageData");

        /// <summary>與 TranslationInjector 相同的選擇規則：語言資料夾先找 folderName，再找 legacyFolderName；沒有語言資料夾或 Keyed 時回傳 null。</summary>
        private static string KeyedDirectory(LoadedLanguage language)
        {
            foreach (var folderName in new[] { language.folderName, language.LegacyFolderName })
            {
                if (folderName.NullOrEmpty()) continue;
                var languageDir = Path.Combine(LanguageDataDirectory(), folderName);
                if (!Directory.Exists(languageDir)) continue;
                var keyedDir = Path.Combine(languageDir, "Keyed");
                return Directory.Exists(keyedDir) ? keyedDir : null;
            }
            return null;
        }
    }
}
