using HarmonyLib;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Verse;

namespace FasterGameLoading
{
    [HarmonyPatch(typeof(GraphicData), "Init")]
    public static class GraphicData_Init_Patch
    {
        public static ConcurrentDictionary<string, List<GraphicData>> savedGraphics = new ConcurrentDictionary<string, List<GraphicData>>();

        static GraphicData_Init_Patch()
        {
            CacheResetter.Register(() => savedGraphics.Clear());
        }
        public static bool Prefix(GraphicData __instance, out bool __state)
        {
            __state = false;
            if (__instance.texPath.NullOrEmpty() is false)
            {
                var graphicDatas = savedGraphics.GetOrAdd(__instance.texPath, _ => new List<GraphicData>());
                foreach (var item in graphicDatas)
                {
                    if (IsSameGraphicData(__instance, item) && item.cachedGraphic != null)
                    {
                        __instance.cachedGraphic = item.cachedGraphic;
                        return false;
                    }
                }
                __state = true;
            }
            return true;
        }

        public static void Postfix(GraphicData __instance, bool __state)
        {
            if (__state && __instance.cachedGraphic != null)
            {
                if (savedGraphics.TryGetValue(__instance.texPath, out var graphicDatas))
                {
                    graphicDatas.Add(__instance);
                }
            }
        }

        public static bool IsSameGraphicData(GraphicData current, GraphicData other)
        {
            if (current.shaderParameters is null && other.shaderParameters is null
                && current.asymmetricLink is null && other.asymmetricLink is null)
            {
                if (current.color == other.color &&
                    current.colorTwo == other.colorTwo &&
                    current.graphicClass == other.graphicClass &&
                    current.drawSize == other.drawSize &&
                    current.linkType == other.linkType &&
                    current.linkFlags == other.linkFlags &&
                    current.shaderType == other.shaderType &&
                    current.drawOffset == other.drawOffset &&
                    current.drawOffsetEast == other.drawOffsetEast &&
                    current.drawOffsetNorth == other.drawOffsetNorth &&
                    current.drawOffsetSouth == other.drawOffsetSouth &&
                    current.drawOffsetWest == other.drawOffsetWest &&
                    current.allowAtlasing == other.allowAtlasing &&
                    current.allowFlip == other.allowFlip &&
                    current.drawRotated == other.drawRotated &&
                    current.renderInstanced == other.renderInstanced &&
                    current.flipExtraRotation == other.flipExtraRotation &&
                    current.onGroundRandomRotateAngle == other.onGroundRandomRotateAngle &&
                    current.overlayOpacity == other.overlayOpacity &&
                    current.renderQueue == other.renderQueue &&
                    current.maskPath == other.maskPath)
                {
                    //current.damageData == other.damageData &&
                    //current.shadowData == other.shadowData;
                    return true;
                }
            }
            return false;
        } 
    }
}
