using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using RimWorld.IO;
using Verse;

namespace FasterGameLoading
{
    internal static class TranslationInjector
    {
        /// <summary>
        /// 原版 LoadedLanguage.LoadFromFile_Keyed 為 private，直接呼叫會觸發 MethodAccessException，故經由委派呼叫。
        /// </summary>
        private static readonly Action<LoadedLanguage, VirtualFile, string> LoadFromFileKeyed =
            AccessTools.MethodDelegate<Action<LoadedLanguage, VirtualFile, string>>(
                AccessTools.Method(typeof(LoadedLanguage), "LoadFromFile_Keyed"));

        /// <summary>
        /// 手動注入來自 LanguageData/ 資料夾的翻譯 Key。
        ///
        /// 此模組設定為先於 Core 載入；原版 LoadedLanguage.LoadMetadata 會採用載入順序中第一個
        /// 含有該語言資料夾的模組來讀 LanguageInfo.xml。若將翻譯放在原版自動掃描的 Languages/，
        /// 便會讀到本模組沒有 LanguageInfo.xml 的資料夾，退回預設的 LanguageWorker 並破壞大小寫規則。
        ///
        /// 為了避免這個問題，我們將 Languages/ 重新命名為 LanguageData/（RimWorld 不會自動載入此資料夾），
        /// 由 Startup.Postfix 呼叫此方法注入翻譯。其餘行為盡量比照原版：
        /// 目前語言與預設語言（英文）各自注入對應資料夾，缺少的 Key 由原版 Translate() 逐一退回預設語言。
        /// </summary>
        internal static void InjectTranslations()
        {
            try
            {
                var rootDir = LoadedModManager.GetMod<FasterGameLoadingMod>()?.Content?.RootDir;
                if (rootDir == null)
                    return;

                var languageDataDir = Path.Combine(rootDir, "LanguageData");
                if (!Directory.Exists(languageDataDir))
                    return;

                InjectInto(LanguageDatabase.activeLanguage, LanguageDatabase.defaultLanguage, languageDataDir);
            }
            // 刻意拆成兩個 catch 而非 catch-when：Harmony 無法 patch 含例外篩選器的方法（遊戲內 Mono 與測試皆然）
            catch (IOException ex)
            {
                FGLLog.Error("Error injecting translations:", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                FGLLog.Error("Error injecting translations:", ex);
            }
        }

        /// <summary>注入目前語言；預設語言不同時也注入，讓原版 Translate() 能逐一退回。</summary>
        internal static void InjectInto(LoadedLanguage activeLanguage, LoadedLanguage defaultLanguage, string languageDataDir)
        {
            InjectInto(activeLanguage, languageDataDir);
            if (defaultLanguage != activeLanguage)
                InjectInto(defaultLanguage, languageDataDir);
        }

        /// <summary>比照原版 LoadedLanguage.AllDirectories：語言資料夾先找 folderName，再找 legacyFolderName；Keyed 底下的子資料夾一併載入。</summary>
        internal static void InjectInto(LoadedLanguage language, string languageDataDir)
        {
            if (language == null)
                return;

            foreach (var folderName in new[] { language.folderName, language.LegacyFolderName })
            {
                if (folderName.NullOrEmpty())
                    continue;

                var languageDir = Path.Combine(languageDataDir, folderName);
                if (!Directory.Exists(languageDir))
                    continue;

                var keyedDir = Path.Combine(languageDir, "Keyed");
                if (Directory.Exists(keyedDir))
                {
                    foreach (var xmlFile in Directory.GetFiles(keyedDir, "*.xml", SearchOption.AllDirectories))
                    {
                        LoadKeyedTranslationsFromFile(xmlFile, language);
                    }
                }
                return;
            }
        }

        /// <summary>交由原版 LoadedLanguage.LoadFromFile_Keyed 解析（\n 跳脫、佔位值、重複 Key 與解析錯誤皆照原版處理）。</summary>
        internal static void LoadKeyedTranslationsFromFile(string filePath, LoadedLanguage language)
        {
            string contents;
            // 個別捕捉讀取例外，避免單一無法讀取的翻譯檔案中斷整批注入
            try
            {
                contents = File.ReadAllText(filePath);
            }
            catch (Exception ex)
            {
                FGLLog.Warning($"Failed to load translation file (skipped): {filePath}\n{ex.Message}");
                return;
            }

            // 原版以 SetOrAdd 覆寫；注入發生在其他模組載入之後，先載入到暫存字典，只補上不存在的 Key，等同原版「後載入者優先」
            var existing = language.keyedReplacements;
            language.keyedReplacements = new Dictionary<string, LoadedLanguage.KeyedReplacement>(StringComparer.Ordinal);
            try
            {
                var file = AbstractFilesystem.GetDirectory(Path.GetDirectoryName(filePath)).GetFile(Path.GetFileName(filePath));
                LoadFromFileKeyed(language, file, contents);
            }
            finally
            {
                var loaded = language.keyedReplacements;
                language.keyedReplacements = existing;
                foreach (var pair in loaded)
                {
                    if (!existing.ContainsKey(pair.Key))
                        existing[pair.Key] = pair.Value;
                }
            }
        }
    }
}
