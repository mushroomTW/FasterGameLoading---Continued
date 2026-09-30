using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 負責掃描和對照所有已載入的 Mod 紋理與其關聯的 Def 和 ModContentPack。
    /// </summary>
    public class TextureScanner
    {
        /// <summary>紋理 → 檔案路徑的對照表。</summary>
        internal readonly Dictionary<Texture, string> texturesByPaths = new();
        /// <summary>紋理 → (Def, 路徑) 的對照表。</summary>
        internal readonly Dictionary<Texture, KeyValuePair<BuildableDef, string>> texturesByDefs = new();

        /// <summary>
        /// 掃描所有已載入的紋理，按類型分類並建立對照表。
        /// 只處理非官方 Mod 的紋理（IsOfficialMod = false）。
        /// </summary>
        public void BuildTextureScanData()
        {
            RefreshTexturePathMap();
            ScanPawnTextures();
            ScanStyleTextures();
            ScanBuildableTextures();
        }



        /// <summary>掃描所有 PawnKindDef 的種族紋理與生命階段圖形。</summary>
        private void ScanPawnTextures()
        {
            foreach (var pawnKind in DefDatabase<PawnKindDef>.AllDefs)
            {
                var modContent = pawnKind.modContentPack;
                if (modContent != null && modContent.IsOfficialMod) continue;
                if (pawnKind.lifeStages == null) continue;

                foreach (var lifeStage in pawnKind.lifeStages)
                {
                    if (lifeStage.bodyGraphicData != null)
                    {
                        AddEntry(pawnKind.race, lifeStage.bodyGraphicData.Graphic);
                        if (lifeStage.dessicatedBodyGraphicData != null)
                        {
                            AddEntry(pawnKind.race, lifeStage.dessicatedBodyGraphicData.Graphic);
                        }
                    }
                }
            }
        }

        /// <summary>掃描所有 StyleCategoryDef 的外觀圖形。</summary>
        private void ScanStyleTextures()
        {
            foreach (var styleDef in DefDatabase<StyleCategoryDef>.AllDefs)
            {
                var modContent = styleDef.modContentPack;
                if (modContent != null && modContent.IsOfficialMod) continue;

                foreach (var style in styleDef.thingDefStyles)
                {
                    AddStyleEntry(style);
                }
            }
        }

        /// <summary>登錄單一風格的主圖形，以及（若有）其各體型的穿著外觀圖形。</summary>
        private void AddStyleEntry(ThingDefStyle style)
        {
            AddEntry(style.ThingDef, style.StyleDef.Graphic);

            if (style.StyleDef.wornGraphicPath.NullOrEmpty())
            {
                return;
            }

            foreach (var bodyType in DefDatabase<BodyTypeDef>.AllDefs)
            {
                if (TextureResize.TryGetGraphicApparel(style.ThingDef, style.StyleDef.wornGraphicPath, bodyType, out var graphic))
                {
                    AddEntry(style.ThingDef, graphic);
                }
            }
        }

        /// <summary>掃描所有 BuildableDef 的建物/物品/植物紋理。</summary>
        private void ScanBuildableTextures()
        {
            foreach (var def in DefDatabase<BuildableDef>.AllDefs)
            {
                var modContent = def.modContentPack;
                if (modContent != null && modContent.IsOfficialMod) continue;

                if (def is TerrainDef)
                {
                    FillEntry(def);
                }
                else if (def is ThingDef thingDef)
                {
                    FillEntry(thingDef);
                    ScanApparelVariants(def, thingDef);
                    ScanPlantVariants(def, thingDef);
                }
            }
        }

        /// <summary>掃描服裝的多種穿著外觀變體（含 wornGraphicPaths）。</summary>
        private void ScanApparelVariants(BuildableDef def, ThingDef thingDef)
        {
            if (TextureResize.GetTextureType(thingDef) is not TextureResize.TextureType.Apparel) return;

            foreach (var bodyType in DefDatabase<BodyTypeDef>.AllDefs)
            {
                if (TextureResize.TryGetGraphicApparel(thingDef, thingDef.apparel.wornGraphicPath, bodyType, out var graphic))
                {
                    AddEntry(def, graphic);
                }
                if (thingDef.apparel.wornGraphicPaths != null)
                {
                    foreach (var path in thingDef.apparel.wornGraphicPaths)
                    {
                        if (TextureResize.TryGetGraphicApparel(thingDef, path, bodyType, out var graphic2))
                        {
                            AddEntry(def, graphic2);
                        }
                    }
                }
            }
        }

        /// <summary>掃描植物的特殊圖形變體（落葉、未成熟、受汙染）。</summary>
        private void ScanPlantVariants(BuildableDef def, ThingDef thingDef)
        {
            var type = TextureResize.GetTextureType(thingDef);
            if (type is not TextureResize.TextureType.Plant and not TextureResize.TextureType.Tree) return;

            if (thingDef.plant.leaflessGraphic != null)
                AddEntry(def, thingDef.plant.leaflessGraphic);
            if (thingDef.plant.immatureGraphic != null)
                AddEntry(def, thingDef.plant.immatureGraphic);
            if (thingDef.plant.pollutedGraphic != null)
                AddEntry(def, thingDef.plant.pollutedGraphic);
        }

        /// <summary>
        /// 將 Def 的圖形和 UI 圖示加入紋理條目。
        /// </summary>
        private void FillEntry(BuildableDef def, Graphic graphicOverride = null)
        {
            var graphic = graphicOverride ?? def.graphic;
            AddEntry(def, graphic);
            if (!def.uiIconPath.NullOrEmpty() && def.uiIcon != null
                && TryGetTexturePath(def.uiIcon, out var fullPath))
            {
                AddEntry(def, fullPath, def.uiIcon);
            }
        }

        /// <summary>
        /// 遞迴展開 Graphic 物件樹，將所有材質紋理加入條目。
        /// 支援 Graphic_Multi、Graphic_Appearances、Graphic_Single、
        /// Graphic_RandomRotated、Graphic_Linked、Graphic_Collection 等類型。
        /// </summary>
        /// <summary>將紋理條目加入對照表。</summary>
        private void AddEntry(BuildableDef def, string fullPath, Texture texture)
        {
            texturesByDefs[texture] = new KeyValuePair<BuildableDef, string>(def, fullPath);
        }

        private void AddEntry(BuildableDef def, Graphic graphic)
        {
            switch (graphic)
            {
                case Graphic_Multi multi:
                    foreach (var mat in multi.mats) GetMatTexture(mat, def);
                    break;
                case Graphic_Appearances appearances:
                    foreach (var subGraphic in appearances.subGraphics) AddEntry(def, subGraphic);
                    break;
                case Graphic_Single single:
                    GetMatTexture(single.MatSingle, def);
                    break;
                case Graphic_RandomRotated randomRotated:
                    AddEntry(def, randomRotated.subGraphic);
                    break;
                case Graphic_Linked linked:
                    AddEntry(def, linked.subGraphic);
                    break;
                case Graphic_Collection collection:
                    foreach (var subGraphic in collection.subGraphics) AddEntry(def, subGraphic);
                    break;
            }
        }

        /// <summary>
        /// 從 Material 中提取 mainTexture 和 mask texture 加入條目。
        /// </summary>
        private void GetMatTexture(Material mat, BuildableDef def)
        {
            if (mat?.mainTexture != mat && mat?.mainTexture != null && TryGetTexturePath(mat.mainTexture, out var fullPath))
            {
                AddEntry(def, fullPath, mat.mainTexture);
                Texture2D mask = null;
                if (mat.HasProperty(ShaderPropertyIDs.MaskTex))
                {
                    mask = mat.GetTexture(ShaderPropertyIDs.MaskTex) as Texture2D;
                }
                if (mask != null && TryGetTexturePath(mask, out var maskPath))
                {
                    AddEntry(def, maskPath, mask);
                }
            }
        }

        /// <summary>從已載入貼圖登記重新整理紋理路徑對照表。</summary>
        private void RefreshTexturePathMap()
        {
            foreach (var kvp in LoadedTextureRegistry.Snapshot())
            {
                texturesByPaths[kvp.Key] = kvp.Value;
            }
        }

        /// <summary>
        /// 根據 Texture 物件尋找其磁碟路徑。先在本地快取查詢，找不到時查已載入貼圖登記的反向表。
        /// </summary>
        public bool TryGetTexturePath(Texture texture, out string fullPath)
        {
            if (!ReferenceEquals(texture, null) && texturesByPaths.TryGetValue(texture, out fullPath))
                return true;

            if (!ReferenceEquals(texture, null)
                && LoadedTextureRegistry.TryGetPath(texture, out fullPath))
            {
                texturesByPaths[texture] = fullPath;
                return true;
            }

            fullPath = null;
            return false;
        }

        /// <summary>清理掃描階段的暫存資料。</summary>
        public void ClearTextureScanData()
        {
            texturesByPaths.Clear();
            texturesByDefs.Clear();
        }
    }
}
