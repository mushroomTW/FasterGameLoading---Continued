using HarmonyLib;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 GraphicData.Init 以快取和重複使用完全相同的 GraphicData 實例。
    /// 相同 texPath 且有相同參數的 GraphicData 會跳過重複的 Init 呼叫，
    /// 直接複用已快取的 cachedGraphic。
    /// </summary>
    [HarmonyPatch(typeof(GraphicData), "Init")]
    public static class GraphicData_Init_Patch
    {
        /// <summary>
        /// 以 texPath 為鍵的快取，值為所有使用該 texPath 的 GraphicData 列表。
        /// </summary>
        internal static ConcurrentDictionary<string, List<GraphicData>> savedGraphics = new ConcurrentDictionary<string, List<GraphicData>>();

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
                lock (graphicDatas)
                {
                    foreach (var item in graphicDatas)
                    {
                        if (IsSameGraphicData(__instance, item) && item.cachedGraphic != null)
                        {
                            __instance.cachedGraphic = item.cachedGraphic;
                            return false;
                        }
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
                    lock (graphicDatas)
                    {
                        graphicDatas.Add(__instance);
                    }
                }
            }
        }

        /// <summary>
        /// 比較兩個 GraphicData 的關鍵屬性是否完全相同。
        /// 只比較可直接存取的值型別欄位；shaderParameters 或 asymmetricLink
        /// 不為 null 時需要深度比較（目前尚未實作），直接視為不同以避免快取錯誤。
        /// </summary>
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
                    return true;
                }
            }
            return false;
        }
    }
}
