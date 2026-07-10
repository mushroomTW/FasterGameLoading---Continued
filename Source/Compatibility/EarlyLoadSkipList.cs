using System;
using System.Collections.Generic;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 提早載入排除名單：管理不適合 Early Loading 或重複載入的 Mod。
    /// </summary>
    public static class EarlyLoadSkipList
    {
        private const string AlienRacesPackageId = "erdelf.HumanoidAlienRaces";

        private static readonly Dictionary<ModContentPack, bool> shouldSkipCache = new();
        private static readonly object shouldSkipCacheLock = new();

        public static bool ShouldSkip(string packageId)
        {
            return ShouldSkip(packageId, null);
        }

        public static bool ShouldSkip(ModContentPack mod)
        {
            if (mod == null) return false;
            lock (shouldSkipCacheLock)
            {
                if (shouldSkipCache.TryGetValue(mod, out var cached)) return cached;
            }

            var shouldSkip = ShouldSkip(mod.PackageIdPlayerFacing, mod.ModMetaData)
                || ShouldSkip(mod.PackageId, mod.ModMetaData);

            lock (shouldSkipCacheLock)
            {
                shouldSkipCache[mod] = shouldSkip;
            }

            return shouldSkip;
        }

        public static bool ShouldSkip(string packageId, object metaData)
        {
            if (string.IsNullOrEmpty(packageId)) return false;
            if (packageId.StartsWith("Ayameduki.", StringComparison.OrdinalIgnoreCase)) return true;
            if (packageId.StartsWith("WRK.", StringComparison.OrdinalIgnoreCase)) return true;
            if (packageId.Equals(AlienRacesPackageId, StringComparison.OrdinalIgnoreCase)) return true;
            return ModDependencyReflection.DependsOnMod(metaData, AlienRacesPackageId);
        }
    }
}
