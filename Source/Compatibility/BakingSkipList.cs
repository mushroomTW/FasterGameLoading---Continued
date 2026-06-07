using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 靜態圖集（Static Atlas）烘焙排除名單。
    /// 攔截 GlobalTextureAtlasManager.TryInsertStatic，阻止排除名單中 Mod 的紋理進入靜態圖集，避免因多遮罩（multi-mask）造成圖案衝突與載入不全。
    /// </summary>
    [HarmonyPatch(typeof(GlobalTextureAtlasManager), "TryInsertStatic")]
    public static class SmartBakingSkipList
    {
        // ── 針對的 Mod 名單 ──
        private static readonly HashSet<string> targetMods = new(StringComparer.OrdinalIgnoreCase)
        {
            "automatic.bionicicons"
        };

        private static readonly HashSet<string> targetModRoots = new(StringComparer.OrdinalIgnoreCase);
        private static bool rootsInitialized = false;
        private static bool? isAnyTargetModActive;

        /// <summary>
        /// 取得目標 Mod 是否為啟用狀態（快取判定結果以維護啟動時期的效能）。
        /// </summary>
        private static bool IsAnyTargetModActive
        {
            get
            {
                if (!isAnyTargetModActive.HasValue)
                {
                    isAnyTargetModActive = false;
                    foreach (var modId in targetMods)
                    {
                        try
                        {
                            if (ModsConfig.IsActive(modId))
                            {
                                isAnyTargetModActive = true;
                                break;
                            }
                        }
                        catch
                        {
                            // 忽略載入順序或初始化時期的錯誤
                        }
                    }
                }
                return isAnyTargetModActive.Value;
            }
        }

        private static void InitializeModRoots()
        {
            if (rootsInitialized) return;
            var mods = LoadedModManager.RunningMods;
            if (mods == null) return;

            bool hasAny = false;
            foreach (var mod in mods)
            {
                hasAny = true;
                break;
            }
            if (!hasAny) return; // 載入列表尚未初始化完畢，下次再來

            rootsInitialized = true;
            try
            {
                foreach (var mod in mods)
                {
                    string cleanId = mod.PackageId;
                    if (cleanId != null && cleanId.EndsWith("_steam", StringComparison.OrdinalIgnoreCase))
                    {
                        cleanId = cleanId.Substring(0, cleanId.Length - 6);
                    }

                    if (cleanId != null && targetMods.Contains(cleanId))
                    {
                        string root = mod.RootDir.Replace('\\', '/').TrimEnd('/');
                        targetModRoots.Add(root);
                    }
                }
            }
            catch (Exception ex)
            {
                FGLLog.Error("Error initializing target mod roots: " + ex);
            }
        }

        /// <summary>
        /// 判斷指定紋理路徑是否屬於需要排除烘焙的目標 Mod。
        /// </summary>
        public static bool ShouldSkipBaking(string path)
        {
            if (!FasterGameLoadingSettings.StaticAtlasesBaking) return false;
            if (!IsAnyTargetModActive) return false;
            if (string.IsNullOrEmpty(path)) return false;

            InitializeModRoots();

            string normalizedPath = path.Replace('\\', '/');
            foreach (var root in targetModRoots)
            {
                if (normalizedPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool Prepare() => FasterGameLoadingSettings.StaticAtlasesBaking;

        /// <summary>
        /// 在將主紋理與遮罩紋理寫入靜態圖集前進行攔截。
        /// 如果該紋理屬於目標排除 Mod，則回傳 false 跳過原方法。
        /// </summary>
        public static bool Prefix(TextureAtlasGroup group, Texture2D texture, Texture2D mask)
        {
            bool skipTex = IsTargetModTexture(texture);
            bool skipMask = IsTargetModTexture(mask);

            if (skipTex || skipMask)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// 輔助方法：判定 Texture2D 實體是否來自排除名單中的 Mod。
        /// </summary>
        private static bool IsTargetModTexture(Texture2D texture)
        {
            if (texture == null) return false;
            
            // 1. 優先比對實體
            if (ModContentLoaderTexture2D_LoadTexture_Patch.skippedBakingTextures.ContainsKey(texture))
            {
                return true;
            }

            // 2. 實體不同時比對檔名
            if (!string.IsNullOrEmpty(texture.name) && ModContentLoaderTexture2D_LoadTexture_Patch.skippedBakingTextureNames.Contains(texture.name))
            {
                return true;
            }

            return false;
        }
    }
}
