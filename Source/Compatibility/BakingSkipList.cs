using System;
using System.Collections;
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
            "automatic.bionicicons",
            "Ancot.AncotLibrary",
            "Ancot.KiiroRace"
        };

        private static readonly HashSet<string> targetModRoots = new(StringComparer.OrdinalIgnoreCase);
        private static bool rootsInitialized = false;
        private static bool? isAnyTargetModActive;

        static SmartBakingSkipList()
        {
            CacheResetter.Register(() =>
            {
                targetModRoots.Clear();
                rootsInitialized = false;
                isAnyTargetModActive = null;
            });
        }

        /// <summary>
        /// 取得目標 Mod 是否為啟用狀態（快取判定結果以維護啟動時期的效能）。
        /// </summary>
        private static bool IsAnyTargetModActive
        {
            get
            {
                if (!isAnyTargetModActive.HasValue)
                {
                    var active = false;

                    // 1. 若啟用外星人種族核心，則判定為 active 以利動態排除
                    try
                    {
                        if (ModsConfig.IsActive("erdelf.HumanoidAlienRaces"))
                        {
                            active = true;
                        }
                    }
                    catch
                    {
                        // 忽略初始化時期的錯誤
                    }

                    // 2. 檢查特定的名單
                    if (!active)
                    {
                        foreach (var modId in targetMods)
                        {
                            try
                            {
                                if (ModsConfig.IsActive(modId))
                                {
                                    active = true;
                                    break;
                                }
                            }
                            catch
                            {
                                // 忽略載入順序或初始化時期的錯誤
                            }
                        }
                    }

                    var hasRunningMods = false;
                    if (!active && TryDetectActiveTargetModFromRunningMods(out hasRunningMods))
                    {
                        active = true;
                    }

                    if (active || hasRunningMods)
                    {
                        isAnyTargetModActive = active;
                    }
                    else
                    {
                        return false;
                    }
                }
                return isAnyTargetModActive.Value;
            }
        }

        private static bool TryDetectActiveTargetModFromRunningMods(out bool hasRunningMods)
        {
            hasRunningMods = false;
            var mods = LoadedModManager.RunningMods;
            if (mods == null) return false;

            foreach (var mod in mods)
            {
                hasRunningMods = true;
                string cleanId = mod.PackageId;
                if (cleanId != null && cleanId.EndsWith("_steam", StringComparison.OrdinalIgnoreCase))
                {
                    cleanId = cleanId.Substring(0, cleanId.Length - 6);
                }

                if (cleanId != null && targetMods.Contains(cleanId))
                {
                    return true;
                }

                if (IsAlienRaceMod(mod) || DependsOnMod(mod, "Ancot.AncotLibrary"))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 動態判定一個 Mod 是否依賴於指定的 PackageId。
        /// 採用反射以維護跨 RimWorld 版本的相容性，防止直接欄位取用出錯。
        /// </summary>
        private static bool DependsOnMod(ModContentPack mod, string targetPackageId)
        {
            if (mod == null) return false;
            var metaData = mod.ModMetaData;
            if (metaData == null) return false;

            var depsField = AccessTools.Field(metaData.GetType(), "dependencies") 
                         ?? AccessTools.Field(metaData.GetType(), "Dependencies");
            if (depsField == null) return false;

            var depsList = depsField.GetValue(metaData) as System.Collections.IEnumerable;
            if (depsList == null) return false;

            foreach (var dep in depsList)
            {
                if (dep == null) continue;
                var packageIdField = AccessTools.Field(dep.GetType(), "packageId")
                                  ?? AccessTools.Field(dep.GetType(), "PackageId");
                if (packageIdField == null) continue;

                var packageId = packageIdField.GetValue(dep) as string;
                if (packageId != null && packageId.Equals(targetPackageId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsAlienRaceMod(ModContentPack mod)
        {
            return DependsOnMod(mod, "erdelf.HumanoidAlienRaces");
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

            try
            {
                foreach (var mod in mods)
                {
                    string cleanId = mod.PackageId;
                    if (cleanId != null && cleanId.EndsWith("_steam", StringComparison.OrdinalIgnoreCase))
                    {
                        cleanId = cleanId.Substring(0, cleanId.Length - 6);
                    }

                    bool shouldExclude = false;
                    if (cleanId != null && targetMods.Contains(cleanId))
                    {
                        shouldExclude = true;
                    }
                    else if (IsAlienRaceMod(mod))
                    {
                        shouldExclude = true;
                    }
                    else if (DependsOnMod(mod, "Ancot.AncotLibrary"))
                    {
                        shouldExclude = true;
                    }

                    if (shouldExclude)
                    {
                        string root = mod.RootDir.Replace('\\', '/').TrimEnd('/');
                        targetModRoots.Add(root);
                    }
                }
                // 迴圈順利完成後才標記初始化，避免例外導致半初始化狀態被永久鎖定
                rootsInitialized = true;
            }
            catch (Exception ex)
            {
                FGLLog.Error("FGL_Log_ErrorInitializingTargetModRoots".TranslateWithFallback("Error initializing target mod roots: {0}", ex));
            }
        }

        /// <summary>
        /// 回傳本次啟動已解析完畢的排除 Mod 根目錄集合（排序後），
        /// 供 AtlasHashCalculator 折入快取失效雜湊，確保 BakingSkipList
        /// 結果改變時能正確使舊快取失效。
        /// 若根目錄尚未初始化（RunningMods 尚未就緒），回傳 null 表示不確定。
        /// </summary>
        public static IReadOnlyCollection<string> GetResolvedSkipRootsForHash()
        {
            if (!rootsInitialized) return null;
            // 回傳排序後的快照，確保雜湊結果與插入順序無關
            var sorted = new List<string>(targetModRoots);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            return sorted;
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
            if (!string.IsNullOrEmpty(texture.name) && ModContentLoaderTexture2D_LoadTexture_Patch.skippedBakingTextureNames.ContainsKey(texture.name))
            {
                return true;
            }

            return false;
        }
    }
}
