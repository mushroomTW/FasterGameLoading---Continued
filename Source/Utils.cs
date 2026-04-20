using System.Collections.Generic;
using Verse;

namespace FasterGameLoading
{
    public static class Utils
    {

        public static List<Thing> ThingsOfDefs(this ListerThings listerThings,IEnumerable<ThingDef> defs) {
            List<Thing> outThings = [];
            foreach (var def in defs) {
                if (listerThings.listsByDef.TryGetValue(def, out var things))
                {
                    outThings.AddRange(things);
                }
                
            }
            return outThings;
        }
        public static int FloorToPowerOfTwo(this int i)
        {
            int closest = UnityEngine.Mathf.ClosestPowerOfTwo(i);
            return closest <= i ? closest : closest >> 1;

        }
    }
}

