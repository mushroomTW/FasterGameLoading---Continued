using System.Collections.Generic;
using RimWorld;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 通用擴充方法與輔助工具。
    /// </summary>
    public static class Utils
    {
        static Utils()
        {
            CacheResetter.Register(() => isMissileGirlActive = null);
        }

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
        public static List<Thing> ThingsOfDefs(this ListerThings listerThings, IEnumerable<ThingDef> defs)
        {
            List<Thing> outThings = new List<Thing>();
            foreach (var def in defs)
            {
                if (listerThings.listsByDef.TryGetValue(def, out var things))
                {
                    outThings.AddRange(things);
                }
            }
            return outThings;
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
        /// </summary>
        public static bool ShouldBeLoadedImmediately(this ThingDef thingDef)
        {
            // 基礎建築和藍圖必須立即載入
            if (thingDef.designationCategory != null || !thingDef.uiIconPath.NullOrEmpty())
                return true;

            if (thingDef.IsBlueprint || thingDef.IsFrame)
                return true;

            if (thingDef.graphicData != null && thingDef.graphicData.Linked)
                return true;

            if (thingDef.thingClass != null && thingDef.thingClass.Name == FGLConsts.BuildingPipe)
                return true;

            // 醫療用品
            if (typeof(Medicine).IsAssignableFrom(thingDef.thingClass)
                || thingDef.orderedTakeGroup?.defName == FGLConsts.MedicineDefName)
                return true;

            // 武器和裝備 - 殖民者常用物品
            if (thingDef.IsWeapon || thingDef.IsApparel)
                return true;

            // 食物 - 使用 ingestible 屬性檢查（避免在 PostLoad 階段訪問 StatDef）
            if (thingDef.ingestible != null || thingDef.IsStuff)
                return true;

            // 殖民者和動物
            if (thingDef.race != null)
                return true;

            // 常見家具和工作台
            if (thingDef.thingCategories != null)
            {
                for (int i = 0; i < thingDef.thingCategories.Count; i++)
                {
                    var catDefName = thingDef.thingCategories[i].defName;
                    for (int j = 0; j < FGLConsts.FurnitureKeywords.Length; j++)
                    {
                        if (catDefName.Contains(FGLConsts.FurnitureKeywords[j]))
                            return true;
                    }
                }
            }

            return false;
        }

        private static readonly object missileGirlLock = new object();
        private static bool? isMissileGirlActive;
        /// <summary>
        /// 檢測目前是否啟用了 MissileGirl。
        /// </summary>
        public static bool IsMissileGirlActive
        {
            get
            {
                if (!isMissileGirlActive.HasValue)
                {
                    lock (missileGirlLock)
                    {
                        if (!isMissileGirlActive.HasValue)
                        {
                            try
                            {
                                isMissileGirlActive = ModsConfig.IsActive("vr.missilegirl");
                            }
                            catch
                            {
                                isMissileGirlActive = false;
                            }
                        }
                    }
                }
                return isMissileGirlActive.Value;
            }
        }
    }
}


