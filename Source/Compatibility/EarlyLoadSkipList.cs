using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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

        private static readonly string[] dependencyListMemberNames =
        {
            "modDependencies",
            "dependencies",
            "Dependencies"
        };

        private static readonly string[] dependencyPackageIdMemberNames =
        {
            "packageId",
            "PackageId",
            "PackageID"
        };

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
            return DependsOnMod(metaData, AlienRacesPackageId);
        }

        private static bool DependsOnMod(object metaData, string targetPackageId)
        {
            var depsList = GetDependencyList(metaData);
            if (depsList == null) return false;

            foreach (var dep in depsList)
            {
                if (dep == null) continue;
                var packageId = GetDependencyPackageId(dep);
                if (packageId != null && packageId.Equals(targetPackageId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static IEnumerable GetDependencyList(object metaData)
        {
            if (metaData == null) return null;

            foreach (var memberName in dependencyListMemberNames)
            {
                if (TryGetMemberValue(metaData, memberName, out var value) && value is IEnumerable dependencies && value is not string)
                {
                    return dependencies;
                }
            }
            return null;
        }

        private static string GetDependencyPackageId(object dependency)
        {
            foreach (var memberName in dependencyPackageIdMemberNames)
            {
                if (TryGetMemberValue(dependency, memberName, out var value) && value is string packageId)
                {
                    return packageId;
                }
            }
            return null;
        }

        private static bool TryGetMemberValue(object instance, string memberName, out object value)
        {
            value = null;
            if (instance == null) return false;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = instance.GetType();
            try
            {
                var field = type.GetField(memberName, flags);
                if (field != null)
                {
                    value = field.GetValue(instance);
                    return true;
                }

                var property = type.GetProperty(memberName, flags);
                if (property != null)
                {
                    value = property.GetValue(instance, null);
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }
    }
}
