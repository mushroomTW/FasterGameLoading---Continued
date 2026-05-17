using HarmonyLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Verse;

namespace FasterGameLoading
{
    [HarmonyPatch(typeof(GraphicData), "Init")]
    public static class GraphicData_Init_Patch
    {
        public static ConcurrentDictionary<string, List<GraphicData>> savedGraphics = new ConcurrentDictionary<string, List<GraphicData>>();
        public static bool Prefix(GraphicData __instance, out bool __state)
        {
            __state = false;
            if (__instance.texPath.NullOrEmpty() is false)
            {
                // 身體貼圖會因不同身體類型（嬰兒/兒童/成人）而有不同的渲染上下文，
                // 快取它們的 Graphic 可能導致不同身體類型共用同一個 cachedGraphic，
                // 造成「嬰兒身體貼圖使用兒童身體貼圖 → 頭長在胸上」的 bug。
                // 跳過快取以確保每個 GraphicData 都能正確獨立初始化。
                if (__instance.texPath.IndexOf("Bodies/", StringComparison.OrdinalIgnoreCase) >= 0
                    || __instance.texPath.IndexOf(@"Bodies\", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
                var graphicDatas = savedGraphics.GetOrAdd(__instance.texPath, _ => new List<GraphicData>());
                foreach (var item in graphicDatas)
                {
                    if (IsTheSameGraphicData(__instance, item) && item.cachedGraphic != null)
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

        public static bool IsTheSameGraphicData(GraphicData current, GraphicData other)
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
