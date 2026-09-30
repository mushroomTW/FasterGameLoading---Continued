using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 通用擴充方法與輔助工具。
    /// </summary>
    public static class Utils
    {
        /// <summary>
        /// 將路徑中的反斜線統一替換為正斜線，確保跨平台相容性。
        /// </summary>
        public static string NormalizePath(this string path)
        {
            if (path == null) return null;
            return path.Replace('\\', '/');
        }
        /// <summary>
        /// 根據指定的 ThingDef 集合，從 ListerThings 中取出所有對應的 Thing。
        /// </summary>
        public static IReadOnlyList<Thing> ThingsOfDefs(this ListerThings listerThings, IEnumerable<ThingDef> defs)
        {
            return defs.SelectMany(def => listerThings.ThingsOfDef(def) ?? Enumerable.Empty<Thing>()).ToList();
        }

        /// <summary>
        /// 回傳不大於輸入值的最大 2 的冪次。
        /// 例如：輸入 1000 → 512，輸入 2048 → 2048。
        /// </summary>
        public static int FloorToPowerOfTwo(this int i)
        {
            if (i <= 0) return 0;
            i |= i >> 1;
            i |= i >> 2;
            i |= i >> 4;
            i |= i >> 8;
            i |= i >> 16;
            return i - (i >> 1);
        }

        /// <summary>
        /// 判斷此 ThingDef 的圖示是否需要立即載入。
        /// 武器、裝備、食物、建築、殖民者等常用類型立即載入，
        /// 其餘（如背景裝飾物）則延遲載入。
        /// 必須在交叉參照解析後呼叫（例如 ExecuteWhenFinished 回呼內）：
        /// PostLoad 當下 designationCategory、thingCategories、orderedTakeGroup 都還沒有值。
        /// </summary>
        public static bool ShouldBeLoadedImmediately(this ThingDef thingDef)
        {
            return IsBuildingOrBlueprint(thingDef)
                || IsMedicine(thingDef)
                || IsColonistGear(thingDef)
                || thingDef.race != null
                || IsCommonFurniture(thingDef);
        }

        /// <summary>基礎建築、藍圖、框架，以及有明確 UI 圖示或連結式圖形的定義。</summary>
        private static bool IsBuildingOrBlueprint(ThingDef thingDef)
        {
            return thingDef.designationCategory != null
                || !thingDef.uiIconPath.NullOrEmpty()
                || thingDef.IsBlueprint
                || thingDef.IsFrame
                || (thingDef.graphicData != null && thingDef.graphicData.Linked)
                || (thingDef.thingClass != null && string.Equals(thingDef.thingClass.Name, FGLConsts.BuildingPipe, StringComparison.Ordinal));
        }

        /// <summary>醫療用品。</summary>
        private static bool IsMedicine(ThingDef thingDef)
        {
            return typeof(Medicine).IsAssignableFrom(thingDef.thingClass)
                || string.Equals(thingDef.orderedTakeGroup?.defName, FGLConsts.MedicineDefName, StringComparison.Ordinal);
        }

        /// <summary>武器、裝備、食物與材料等殖民者常用物品。</summary>
        private static bool IsColonistGear(ThingDef thingDef)
        {
            // 食物以 ingestible 屬性檢查，避免在 PostLoad 階段訪問 StatDef
            return thingDef.IsWeapon
                || thingDef.IsApparel
                || thingDef.ingestible != null
                || thingDef.IsStuff;
        }

        /// <summary>分類名稱命中家具／工作台關鍵字的定義。</summary>
        private static bool IsCommonFurniture(ThingDef thingDef)
        {
            if (thingDef.thingCategories == null)
                return false;

            for (int i = 0; i < thingDef.thingCategories.Count; i++)
            {
                var catDef = thingDef.thingCategories[i];
                var catDefName = catDef?.defName;
                if (string.IsNullOrEmpty(catDefName))
                    continue;

                for (int j = 0; j < FGLConsts.FurnitureKeywords.Length; j++)
                {
                    if (catDefName.IndexOf(FGLConsts.FurnitureKeywords[j], StringComparison.Ordinal) >= 0)
                        return true;
                }
            }

            return false;
        }
    }
}



