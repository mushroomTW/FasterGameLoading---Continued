using HarmonyLib;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 Graphic.Draw 與 Graphic.DrawWorker，
    /// 在遊戲物體即將被繪製於畫面的瞬間，自動補加載其被延遲的真實材質像素。
    /// </summary>
    [HarmonyPatch]
    public static class Graphic_Mat_Patch
    {
        [HarmonyPatch(typeof(Graphic), nameof(Graphic.Draw))]
        [HarmonyPrefix]
        public static void Draw_Prefix(Graphic __instance)
        {
            if (FasterGameLoadingSettings.LazyTextureLoading)
            {
                TriggerLazyLoadForGraphic(__instance);
            }
        }

        [HarmonyPatch(typeof(Graphic), nameof(Graphic.DrawWorker))]
        [HarmonyPrefix]
        public static void DrawWorker_Prefix(Graphic __instance)
        {
            if (FasterGameLoadingSettings.LazyTextureLoading)
            {
                TriggerLazyLoadForGraphic(__instance);
            }
        }

        /// <summary>
        /// 遞迴遍歷 Graphic 樹，對其持有的所有材質套用補加載。
        /// </summary>
        private static void TriggerLazyLoadForGraphic(Graphic g)
        {
            if (g == null) return;

            // 1. 檢查基類持有的主材質 MatSingle
            if (g.MatSingle != null)
            {
                TriggerLazyLoad(g.MatSingle);
            }

            // 2. 檢查 Graphic_Multi 的 mats 多方向材質陣列
            if (g is Graphic_Multi multi && multi.mats != null)
            {
                for (int i = 0; i < multi.mats.Length; i++)
                {
                    if (multi.mats[i] != null)
                    {
                        TriggerLazyLoad(multi.mats[i]);
                    }
                }
            }

            // 3. 檢查 Graphic_Collection / Graphic_Appearances 等複合物件子圖形
            if (g is Graphic_Collection collection && collection.subGraphics != null)
            {
                for (int i = 0; i < collection.subGraphics.Length; i++)
                {
                    TriggerLazyLoadForGraphic(collection.subGraphics[i]);
                }
            }
            else if (g is Graphic_Appearances appearances && appearances.subGraphics != null)
            {
                for (int i = 0; i < appearances.subGraphics.Length; i++)
                {
                    TriggerLazyLoadForGraphic(appearances.subGraphics[i]);
                }
            }
            else if (g is Graphic_RandomRotated randomRotated && randomRotated.subGraphic != null)
            {
                TriggerLazyLoadForGraphic(randomRotated.subGraphic);
            }
            else if (g is Graphic_Linked linked && linked.subGraphic != null)
            {
                TriggerLazyLoadForGraphic(linked.subGraphic);
            }
        }

        private static void TriggerLazyLoad(Material mat)
        {
            if (mat != null)
            {
                // 檢查並補加載主貼圖
                LazyTextureLoader.TriggerLazyLoadIfPending(mat.mainTexture);

                // 檢查並補加載 Mask 遮罩貼圖
                if (mat.HasProperty(ShaderPropertyIDs.MaskTex))
                {
                    LazyTextureLoader.TriggerLazyLoadIfPending(mat.GetTexture(ShaderPropertyIDs.MaskTex));
                }
            }
        }
    }
}
