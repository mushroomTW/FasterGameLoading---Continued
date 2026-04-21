using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    [HarmonyPatch(typeof(StaticConstructorOnStartupUtility), nameof(StaticConstructorOnStartupUtility.CallAll))]
    public static class Startup
    {

        public static void Postfix()
        {
            FasterGameLoadingSettings.modsInLastSession = ModsConfig.ActiveModsInLoadOrder.Select(x => x.packageIdLowerCase).ToList();
            FasterGameLoadingSettings.loadedTexturesSinceLastSession = new Dictionary<string, string>(ModContentLoaderTexture2D_LoadTexture_Patch.loadedTexturesThisSession);
            FasterGameLoadingSettings.loadedTypesByFullNameSinceLastSession = new Dictionary<string, string>(GenTypes_GetTypeInAnyAssemblyInt_Patch.loadedTypesThisSession);
            FasterGameLoadingSettings.successfulXMLPathsSinceLastSession = XmlNode_SelectSingleNode_Patch.successfulXMLPathsThisSession;
            FasterGameLoadingSettings.failedXMLPathsSinceLastSession = XmlNode_SelectSingleNode_Patch.failedXMLPathsThisSession;
            LoadedModManager.GetMod<FasterGameLoadingMod>().WriteSettings();
            InjectTranslations();
            LongEventHandler.toExecuteWhenFinished.Add(delegate
            {
                FasterGameLoadingMod.delayedActions.StartCoroutine(FasterGameLoadingMod.delayedActions.PerformActions());
            });
        }

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

                // Try to find the matching language folder, fall back to English
                string langFolderPath = null;
                var langFolderName = activeLanguage.folderName;

                // Check for exact match first
                var candidatePath = Path.Combine(languageDataDir, langFolderName);
                if (Directory.Exists(candidatePath))
                {
                    langFolderPath = candidatePath;
                }
                else
                {
                    // Fall back to English
                    var englishPath = Path.Combine(languageDataDir, "English");
                    if (Directory.Exists(englishPath))
                    {
                        langFolderPath = englishPath;
                    }
                }

                if (langFolderPath == null)
                    return;

                // Load keyed translations
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

                // Only inject if the key doesn't already exist (don't override other mods)
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
