using System.Collections.Generic;
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
    }
}

