using System;
using System.IO;
using System.Xml;
using Verse;

namespace FasterGameLoading
{
    internal static class TranslationInjector
    {
        /// <summary>
        /// Manually injects translation keys from LanguageData/ folder.
        /// 
        /// Because this mod loads before Core (loadBefore: Ludeon.RimWorld),
        /// RimWorld's language system would process our Languages/ folder before
        /// Core's WordInfo/grammar/capitalization rules are loaded, corrupting
        /// the title-casing system and causing all generated phrases to lose
        /// proper capitalization (e.g. "Alex danvers" instead of "Alex Danvers").
        /// 
        /// To avoid this, we renamed Languages/ to LanguageData/ (which RimWorld
        /// won't auto-load) and inject translations here, after Core is fully loaded.
        /// </summary>
        internal static void InjectTranslations()
        {
            try
            {
                var modContentPack = LoadedModManager.GetMod<FasterGameLoadingMod>().Content;
                var languageDataDir = Path.Combine(modContentPack.RootDir, "LanguageData");
                if (!Directory.Exists(languageDataDir))
                    return;

                var activeLanguage = LanguageDatabase.activeLanguage;
                if (activeLanguage == null)
                    return;

                string langFolderPath = null;
                var langFolderName = activeLanguage.folderName;

                var candidatePath = Path.Combine(languageDataDir, langFolderName);
                if (Directory.Exists(candidatePath))
                {
                    langFolderPath = candidatePath;
                }
                else
                {
                    var englishPath = Path.Combine(languageDataDir, "English");
                    if (Directory.Exists(englishPath))
                    {
                        langFolderPath = englishPath;
                    }
                }

                if (langFolderPath == null)
                    return;

                var keyedDir = Path.Combine(langFolderPath, "Keyed");
                if (!Directory.Exists(keyedDir))
                    return;

                foreach (var xmlFile in Directory.GetFiles(keyedDir, "*.xml"))
                {
                    LoadKeyedTranslationsFromFile(xmlFile, activeLanguage);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[FasterGameLoading] Error injecting translations: " + ex);
            }
        }

        internal static void LoadKeyedTranslationsFromFile(string filePath, LoadedLanguage language)
        {
            var doc = new XmlDocument();
            doc.Load(filePath);
            var root = doc.DocumentElement;
            if (root == null)
                return;

            foreach (XmlNode node in root.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element)
                    continue;

                var key = node.Name;
                var value = node.InnerText;

                if (!language.keyedReplacements.ContainsKey(key))
                {
                    language.keyedReplacements[key] = new LoadedLanguage.KeyedReplacement
                    {
                        key = key,
                        value = value,
                        fileSource = filePath,
                        fileSourceFullPath = filePath,
                        isPlaceholder = false
                    };
                }
            }
        }
    }
}
