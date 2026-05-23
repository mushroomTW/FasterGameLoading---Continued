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
            int closest = UnityEngine.Mathf.ClosestPowerOfTwo(i);
            return closest <= i ? closest : closest >> 1;
        }
    }
}

