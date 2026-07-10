using System;
using System.Collections;
using HarmonyLib;

namespace FasterGameLoading
{
    /// <summary>
    /// 透過反射跨版本讀取 ModMetaData 的相依清單與各相依項的 packageId。
    /// RimWorld 跨版本欄位名稱不一（modDependencies/dependencies/Dependencies、
    /// packageId/PackageId/PackageID），故嘗試多個成員名稱；AccessTools 已處理
    /// public/nonpublic 與跨版本相容，優於手動 BindingFlags + GetField。
    /// 以 string 而非 PackageId 型別回傳，避免對 RimWorld 內部型別產生編譯期依賴。
    /// </summary>
    internal static class ModDependencyReflection
    {
        private static readonly string[] DependencyListMemberNames =
        {
            "modDependencies",
            "dependencies",
            "Dependencies"
        };

        private static readonly string[] DependencyPackageIdMemberNames =
        {
            "packageId",
            "PackageId",
            "PackageID"
        };

        /// <summary>
        /// 判定 <paramref name="metaData"/> 是否相依於 <paramref name="targetPackageId"/>。
        /// 中途任一環節無法解析即回 false（與逐欄位 try/catch 等價的保守語義）。
        /// </summary>
        internal static bool DependsOnMod(object metaData, string targetPackageId)
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

            foreach (var memberName in DependencyListMemberNames)
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
            foreach (var memberName in DependencyPackageIdMemberNames)
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

            var type = instance.GetType();
            try
            {
                var field = AccessTools.Field(type, memberName);
                if (field != null)
                {
                    value = field.GetValue(instance);
                    return true;
                }

                var property = AccessTools.Property(type, memberName);
                if (property != null)
                {
                    value = property.GetValue(instance, null);
                    return true;
                }
            }
            catch (Exception ex)
            {
                // 反射探測失敗是預期行為（跨版本欄位/屬性不存在），但在 Verbose 模式下記錄以利診斷
                if (FasterGameLoadingSettings.VerboseLogging)
                {
                    FGLLog.Warning($"Reflection probe failed for {type.Name}.{memberName}", ex);
                }
                return false;
            }

            return false;
        }
    }
}
