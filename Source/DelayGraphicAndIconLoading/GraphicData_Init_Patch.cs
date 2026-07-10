using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
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
        public static bool Prepare() => FasterGameLoadingSettings.DelayGraphicLoading;

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
            // 若為 GraphicData 的子類別，可能含有額外欄位，
            // IsSameGraphicData 無法比對這些欄位，跳過快取以避免回傳錯誤的 Graphic。
            if (__instance.GetType() != typeof(GraphicData))
            {
                return true;
            }
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
                        if (graphicDatas.Count < 10)
                        {
                            graphicDatas.Add(__instance);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 比較兩個 GraphicData 的關鍵屬性是否完全相同。
        /// 進行值型別欄位比對，並遞迴深度比對 shaderParameters 與 asymmetricLink。
        /// </summary>
        public static bool IsSameGraphicData(GraphicData current, GraphicData other)
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
                current.maskPath == other.maskPath &&
                IsSameShaderParameters(current.shaderParameters, other.shaderParameters) &&
                IsSameAsymmetricLink(current.asymmetricLink, other.asymmetricLink))
            {
                return true;
            }
            return false;
        }

        private static bool IsSameShaderParameters(List<ShaderParameter> current, List<ShaderParameter> other)
        {
            if (current == null && other == null) return true;
            if (current == null || other == null) return false;
            if (current.Count != other.Count) return false;
            for (int i = 0; i < current.Count; i++)
            {
                var c = current[i];
                var o = other[i];
                if (c == null && o == null) continue;
                if (c == null || o == null) return false;
                if (c.name != o.name || c.value != o.value || c.valueTex != o.valueTex || c.type != o.type)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsSameAsymmetricLink(AsymmetricLinkData current, AsymmetricLinkData other)
        {
            if (current == null && other == null) return true;
            if (current == null || other == null) return false;
            return current.linkFlags == other.linkFlags &&
                   current.linkToDoors == other.linkToDoors &&
                   IsSameBorderData(current.drawDoorBorderEast, other.drawDoorBorderEast) &&
                   IsSameBorderData(current.drawDoorBorderWest, other.drawDoorBorderWest);
        }

        private static bool IsSameBorderData(AsymmetricLinkData.BorderData current, AsymmetricLinkData.BorderData other)
        {
            if (current == null && other == null) return true;
            if (current == null || other == null) return false;
            return current.color == other.color &&
                   current.size == other.size &&
                   current.offset == other.offset &&
                   current.colorMat == other.colorMat;
        }
    }
}
