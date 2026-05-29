using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 管理所有待按需加載材質 (Lazy Load Textures on Demand) 的映射與補加載。
    /// </summary>
    public static class LazyTextureLoader
    {
        /// <summary>待加載材質的映射對照表：鍵為 2x2 佔位貼圖實例，值為真實檔案路徑。</summary>
        public static readonly ConcurrentDictionary<Texture2D, string> pendingLazyTextures = new ConcurrentDictionary<Texture2D, string>();
        /// <summary>目前是否有等待按需加載的貼圖。提供給 Draw 的 Harmony Prefix 以免去熱路徑上的字典查詢開銷。</summary>
        public static volatile bool hasPendingTextures = false;

        /// <summary>排除延遲加載的材質路徑關鍵字清單。</summary>
        public static readonly HashSet<string> ExcludePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>所有依賴於 Humanoid Alien Races 的外星人 Mod 的根目錄排除清單。</summary>
        public static readonly HashSet<string> AlienRaceModRootDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static LazyTextureLoader()
        {
            // 載入預設排除路徑
            ExcludePaths.Add("UI/");
            ExcludePaths.Add("Icon");

            // 檢測並收集所有依賴於 Humanoid Alien Races 的 Mod 根目錄
            InitializeAlienRaceMods();

            CacheResetter.Register(() =>
            {
                pendingLazyTextures.Clear();
                hasPendingTextures = false;
            });
        }

        private static void InitializeAlienRaceMods()
        {
            try
            {
                var alienAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == FGLConsts.AlienRaceAssemblyName);
                if (alienAssembly == null) return;

                foreach (var mod in LoadedModManager.RunningMods)
                {
                    var rootDir = mod.RootDir;
                    if (string.IsNullOrEmpty(rootDir)) continue;

                    var aboutPath = Path.Combine(rootDir, "About", "About.xml");
                    if (!File.Exists(aboutPath)) continue;

                    try
                    {
                        string content = File.ReadAllText(aboutPath);
                        if (content.IndexOf("erdelf.humanoidalienraces", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var normalizedRoot = rootDir.Replace('\\', '/');
                            if (!normalizedRoot.EndsWith("/"))
                            {
                                normalizedRoot += "/";
                            }
                            AlienRaceModRootDirs.Add(normalizedRoot);
                            FGLLog.Message($"Detected Alien Race Mod (excluding from lazy loading): {mod.Name} ({normalizedRoot})");
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                FGLLog.Warning("Failed to scan Alien Race mods: ", ex);
            }
        }

        /// <summary>
        /// 註冊一個待延遲加載的貼圖實例。
        /// </summary>
        public static void RegisterLazyTexture(Texture2D tex, string originalPath)
        {
            if (tex == null || string.IsNullOrEmpty(originalPath)) return;
            pendingLazyTextures[tex] = originalPath;
            hasPendingTextures = true;
        }

        /// <summary>
        /// 動態註冊排除延遲加載的路徑關鍵字。
        /// </summary>
        public static void RegisterExcludePath(string path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                ExcludePaths.Add(path);
            }
        }

        /// <summary>
        /// 若指定的貼圖是延遲加載的佔位貼圖，立即在主執行緒中從磁碟讀取真實像素資料補加載。
        /// </summary>
        public static void TriggerLazyLoadIfPending(Texture texture)
        {
            if (texture is Texture2D tex2D && pendingLazyTextures.TryRemove(tex2D, out var filePath))
            {
                if (pendingLazyTextures.IsEmpty)
                {
                    hasPendingTextures = false;
                }
                try
                {
                    if (File.Exists(filePath))
                    {
                        var data = File.ReadAllBytes(filePath);
                        // 在原 Texture2D 實例上載入像素數據，這會自動覆蓋佔位數據
                        if (tex2D.LoadImage(data))
                        {
                            bool useMipmaps = !filePath.NormalizePath().Contains(FGLConsts.UIDirSlash);
                            tex2D.Compress(true);
                            tex2D.Apply(useMipmaps, true);

                            if (FasterGameLoadingSettings.VerboseLogging)
                            {
                                FGLLog.Message($"Successfully lazy-loaded texture on demand: {filePath}");
                            }
                        }
                    }
                    else
                    {
                        FGLLog.Warning($"Lazy-loaded texture file missing from disk: {filePath}");
                    }
                }
                catch (Exception ex)
                {
                    FGLLog.Warning($"Failed to lazy-load texture {filePath}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 判定指定路徑的材質貼圖是否應該被延遲加載。
        /// 排除 UI/Icon/手術圖標等核心貼圖，僅對 Def 物件常見路徑（Things, Pawn, Terrain 等）進行延遲，保證極限啟動速度與完美 UI 展示。
        /// </summary>
        public static bool ShouldLazyLoad(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            var normalized = path.Replace('\\', '/');

            // 1. 優先排除所有外星人 Mod 的貼圖
            foreach (var rootDir in AlienRaceModRootDirs)
            {
                if (normalized.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // 2. 遍歷排除清單，比對是否包含排除的關鍵字
            foreach (var exclude in ExcludePaths)
            {
                if (normalized.IndexOf(exclude, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }

            // 3. 僅對遊戲世界中的 Def 物件貼圖進行延遲加載
            if (normalized.IndexOf("/Things/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("/Pawn/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("/Apparel/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("/Weapon/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("/Item/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("/Plant/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("/Terrain/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }
    }
}
