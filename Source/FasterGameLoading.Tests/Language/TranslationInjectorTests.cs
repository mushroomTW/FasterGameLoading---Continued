using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;
using Verse;

namespace FasterGameLoading.Tests.Language
{
    [TestFixture]
    public class TranslationInjectorTests
    {
        private string tempDir;

        [SetUp]
        public void SetUp()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "FGL_Translations_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex)
            {
                Assert.Fail("清理翻譯測試暫存目錄失敗：" + tempDir + Environment.NewLine + ex);
            }
        }

        [Test]
        public void LoadKeyedTranslationsFromFile_LoadsElementsAndPreservesExistingKeys()
        {
            var path = Path.Combine(tempDir, "Keyed.xml");
            File.WriteAllText(path, "<LanguageData><Greeting>Hello</Greeting><Farewell>Bye <b>friend</b></Farewell></LanguageData>");
            var language = CreateLanguage();
            language.keyedReplacements["Greeting"] = new LoadedLanguage.KeyedReplacement
            {
                key = "Greeting",
                value = "Existing",
            };

            TranslationInjector.LoadKeyedTranslationsFromFile(path, language);

            Assert.That(language.keyedReplacements["Greeting"].value, Is.EqualTo("Existing"));
            Assert.That(language.keyedReplacements["Farewell"].value, Is.EqualTo("Bye friend"));
            Assert.That(language.keyedReplacements["Farewell"].fileSource, Is.EqualTo("Keyed.xml"));
            Assert.That(language.keyedReplacements["Farewell"].fileSourceFullPath, Is.EqualTo(path));
        }

        [Test]
        public void LoadKeyedTranslationsFromFile_FollowsVanillaKeyedRules()
        {
            var path = Path.Combine(tempDir, "Vanilla.xml");
            File.WriteAllText(path, "<LanguageData>\n  <Multi>Line1\\nLine2</Multi>\n  <Pending>TODO</Pending>\n  <Multi>Duplicate</Multi>\n</LanguageData>");
            var language = CreateLanguage();

            TranslationInjector.LoadKeyedTranslationsFromFile(path, language);

            Assert.That(language.keyedReplacements["Multi"].value, Is.EqualTo("Line1\nLine2"));
            Assert.That(language.keyedReplacements["Multi"].fileSourceLine, Is.EqualTo(2));
            Assert.That(language.keyedReplacements["Pending"].isPlaceholder, Is.True);
            Assert.That(language.keyedReplacements["Pending"].value, Is.Empty);
        }

        [Test]
        public void InjectInto_UsesMatchingFolderAndDoesNotFallBackToEnglish()
        {
            WriteKeyed("English", "Root.xml", "<LanguageData><OnlyEnglish>English</OnlyEnglish></LanguageData>");
            WriteKeyed("German", Path.Combine("Sub", "Nested.xml"), "<LanguageData><GermanKey>Deutsch</GermanKey></LanguageData>");
            var german = CreateLanguage("German (Deutsch)", "German");
            var japanese = CreateLanguage("Japanese (日本語)", "Japanese");

            TranslationInjector.InjectInto(german, tempDir);
            TranslationInjector.InjectInto(japanese, tempDir);

            Assert.That(german.keyedReplacements["GermanKey"].value, Is.EqualTo("Deutsch"));
            Assert.That(german.keyedReplacements.ContainsKey("OnlyEnglish"), Is.False);
            Assert.That(japanese.keyedReplacements, Is.Empty);
        }

        [Test]
        public void InjectInto_WhenFolderNameDirectoryExistsWithoutKeyed_DoesNotUseLegacyFolder()
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "German (Deutsch)"));
            WriteKeyed("German", "Legacy.xml", "<LanguageData><LegacyKey>Legacy</LegacyKey></LanguageData>");
            var german = CreateLanguage("German (Deutsch)", "German");

            TranslationInjector.InjectInto(german, tempDir);

            Assert.That(german.keyedReplacements, Is.Empty);
        }

        [Test]
        public void InjectInto_WithDifferentDefaultLanguage_InjectsEnglishIntoDefaultOnly()
        {
            WriteKeyed("English", "Root.xml", "<LanguageData><OnlyEnglish>English</OnlyEnglish></LanguageData>");
            var japanese = CreateLanguage("Japanese (日本語)", "Japanese");
            var english = CreateLanguage("English", "English");

            TranslationInjector.InjectInto(japanese, english, tempDir);

            Assert.That(english.keyedReplacements["OnlyEnglish"].value, Is.EqualTo("English"));
            Assert.That(japanese.keyedReplacements, Is.Empty);
        }

        [Test]
        public void LoadKeyedTranslationsFromFile_WithCorruptedXml_IsSafelyIgnoredAndDoesNotThrow()
        {
            var path = Path.Combine(tempDir, "Corrupt.xml");
            File.WriteAllText(path, "<LanguageData><UnclosedTag>Content</LanguageData>");
            var language = CreateLanguage();

            Assert.DoesNotThrow(() => TranslationInjector.LoadKeyedTranslationsFromFile(path, language));
            Assert.That(language.keyedReplacements, Is.Empty);
        }

        [Test]
        public void LoadKeyedTranslationsFromFile_WithNonExistentFile_IsSafelyIgnored()
        {
            var path = Path.Combine(tempDir, "DoesNotExist.xml");
            var language = CreateLanguage();

            Assert.DoesNotThrow(() => TranslationInjector.LoadKeyedTranslationsFromFile(path, language));
            Assert.That(language.keyedReplacements, Is.Empty);
        }

        [Test]
        public void LoadKeyedTranslationsFromFile_WithCommentsAndWhitespace_OnlyExtractsElementNodes()
        {
            var path = Path.Combine(tempDir, "WithComments.xml");
            File.WriteAllText(path, "<LanguageData>\n  <!-- Comment line -->\n  <OptionA>Alpha</OptionA>\n  <!-- Another comment -->\n</LanguageData>");
            var language = CreateLanguage();

            TranslationInjector.LoadKeyedTranslationsFromFile(path, language);

            Assert.That(language.keyedReplacements, Has.Count.EqualTo(1));
            Assert.That(language.keyedReplacements["OptionA"].value, Is.EqualTo("Alpha"));
        }

        [Test]
        public void InjectTranslations_WhenLanguageDataMissing_ReturnsSafelyWithoutThrowing()
        {
            Assert.DoesNotThrow(() => TranslationInjector.InjectTranslations());
        }

        private void WriteKeyed(string folderName, string relativePath, string content)
        {
            var path = Path.Combine(tempDir, folderName, "Keyed", relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
        }

        private static LoadedLanguage CreateLanguage(string folderName = null, string legacyFolderName = null)
        {
            var language = (LoadedLanguage)FormatterServices.GetUninitializedObject(typeof(LoadedLanguage));
            language.keyedReplacements = new Dictionary<string, LoadedLanguage.KeyedReplacement>();
            language.loadErrors = new List<string>();
            language.folderName = folderName;
            Traverse.Create(language).Field("legacyFolderName").SetValue(legacyFolderName);
            return language;
        }
    }
}
