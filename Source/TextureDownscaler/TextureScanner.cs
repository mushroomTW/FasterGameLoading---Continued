using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 負責掃描和對照所有已載入的 Mod 紋理與其關聯的 Def 和 ModContentPack。
    /// </summary>
    public class TextureScanner
    {
        /// <summary>按紋理類型分類的紋理條目。</summary>
        internal readonly Dictionary<TextureResize.TextureType, List<KeyValuePair<BuildableDef, string>>> textures = new();
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
            InitializeScanContainers();
            RefreshTexturePathMap();
            ScanPawnTextures();
            ScanStyleTextures();
            ScanBuildableTextures();
        }

        private void InitializeScanContainers()
        {
            foreach (var value in Enum.GetValues(typeof(TextureResize.TextureType)).Cast<TextureResize.TextureType>())
            {
                textures[value] = new();
            }
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
                        AddEntry(TextureResize.TextureType.Pawn, pawnKind.race, lifeStage.bodyGraphicData.Graphic);
                        if (lifeStage.dessicatedBodyGraphicData != null)
                        {
                            AddEntry(TextureResize.TextureType.Pawn, pawnKind.race, lifeStage.dessicatedBodyGraphicData.Graphic);
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
                    var type = TextureResize.GetTextureType(style.ThingDef);
                    AddEntry(type, style.ThingDef, style.StyleDef.Graphic);
                    if (style.StyleDef.wornGraphicPath.NullOrEmpty() is false)
                    {
                        foreach (var bodyType in DefDatabase<BodyTypeDef>.AllDefs)
                        {
                            if (TextureResize.TryGetGraphicApparel(style.ThingDef, style.StyleDef.wornGraphicPath, bodyType, out var graphic))
                            {
                                AddEntry(type, style.ThingDef, graphic);
                            }
                        }
                    }
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

                if (def is TerrainDef terrain)
                {
                    FillEntry(TextureResize.TextureType.Terrain, def);
                }
                else if (def is ThingDef thingDef)
                {
                    var type = TextureResize.GetTextureType(thingDef);
                    FillEntry(type, thingDef);
                    ScanApparelVariants(type, def, thingDef);
                    ScanPlantVariants(type, def, thingDef);
                }
            }
        }

        /// <summary>掃描服裝的多種穿著外觀變體（含 wornGraphicPaths）。</summary>
        private void ScanApparelVariants(TextureResize.TextureType type, BuildableDef def, ThingDef thingDef)
        {
            if (type != TextureResize.TextureType.Apparel) return;

            foreach (var bodyType in DefDatabase<BodyTypeDef>.AllDefs)
            {
                if (TextureResize.TryGetGraphicApparel(thingDef, thingDef.apparel.wornGraphicPath, bodyType, out var graphic))
                {
                    AddEntry(type, def, graphic);
                }
                if (thingDef.apparel.wornGraphicPaths != null)
                {
                    foreach (var path in thingDef.apparel.wornGraphicPaths)
                    {
                        if (TextureResize.TryGetGraphicApparel(thingDef, path, bodyType, out var graphic2))
                        {
                            AddEntry(type, def, graphic2);
                        }
                    }
                }
            }
        }

        /// <summary>掃描植物的特殊圖形變體（落葉、未成熟、受汙染）。</summary>
        private void ScanPlantVariants(TextureResize.TextureType type, BuildableDef def, ThingDef thingDef)
        {
            if (type != TextureResize.TextureType.Plant && type != TextureResize.TextureType.Tree) return;

            if (thingDef.plant.leaflessGraphic != null)
                AddEntry(type, def, thingDef.plant.leaflessGraphic);
            if (thingDef.plant.immatureGraphic != null)
                AddEntry(type, def, thingDef.plant.immatureGraphic);
            if (thingDef.plant.pollutedGraphic != null)
                AddEntry(type, def, thingDef.plant.pollutedGraphic);
        }

        /// <summary>
        /// 將 Def 的圖形和 UI 圖示加入紋理條目。
        /// </summary>
        private void FillEntry(TextureResize.TextureType type, BuildableDef def, Graphic graphicOverride = null)
        {
            var graphic = graphicOverride ?? def.graphic;
            AddEntry(type, def, graphic);
            if (def.uiIconPath.NullOrEmpty() is false && def.uiIcon != null)
            {
                if (TryGetTexturePath(def.uiIcon, out var fullPath))
                {
                    AddEntry(TextureResize.TextureType.UI, def, fullPath, def.uiIcon);
                }
            }
        }

        /// <summary>
        /// 遞迴展開 Graphic 物件樹，將所有材質紋理加入條目。
        /// 支援 Graphic_Multi、Graphic_Appearances、Graphic_Single、
        /// Graphic_RandomRotated、Graphic_Linked、Graphic_Collection 等類型。
        /// </summary>
        private void AddEntry(TextureResize.TextureType type, BuildableDef def, Graphic graphic)
        {
            switch (graphic)
            {
                case Graphic_Multi multi:
                    foreach (var mat in multi.mats) GetMatTexture(type, mat, def);
                    break;
                case Graphic_Appearances appearances:
                    foreach (var subGraphic in appearances.subGraphics) AddEntry(type, def, subGraphic);
                    break;
                case Graphic_Single single:
                    GetMatTexture(type, single.mat, def);
                    break;
                case Graphic_RandomRotated randomRotated:
                    AddEntry(type, def, randomRotated.subGraphic);
                    break;
                case Graphic_Linked linked:
                    AddEntry(type, def, linked.subGraphic);
                    break;
                case Graphic_Collection collection:
                    foreach (var subGraphic in collection.subGraphics) AddEntry(type, def, subGraphic);
                    break;
            }
        }

        /// <summary>
        /// 從 Material 中提取 mainTexture 和 mask texture 加入條目。
        /// </summary>
        private void GetMatTexture(TextureResize.TextureType type, Material mat, BuildableDef def)
        {
            if (mat?.mainTexture != mat && mat?.mainTexture != null && TryGetTexturePath(mat.mainTexture, out var fullPath))
            {
                AddEntry(type, def, fullPath, mat.mainTexture);
                Texture2D mask = null;
                if (mat.HasProperty(ShaderPropertyIDs.MaskTex))
                {
                    mask = (Texture2D)mat.GetTexture(ShaderPropertyIDs.MaskTex);
                }
                if (mask != null && TryGetTexturePath(mask, out var maskPath))
                {
                    AddEntry(type, def, maskPath, mask);
                }
            }
        }

        /// <summary>從 WeakReference 快取中重新整理紋理路徑對照表。</summary>
        private void RefreshTexturePathMap()
        {
            foreach (var kvp in ModContentLoaderTexture2D_LoadTexture_Patch.savedTextures)
            {
                if (kvp.Value.TryGetTarget(out var tex))
                {
                    texturesByPaths[tex] = kvp.Key;
                }
            }
        }

        /// <summary>
        /// 根據 Texture 物件尋找其磁碟路徑。先在本地快取查詢，
        /// 找不到時遍歷 WeakReference 快取進行 ReferenceEquals 比對。
        /// </summary>
        public bool TryGetTexturePath(Texture texture, out string fullPath)
        {
            if (texture != null && texturesByPaths.TryGetValue(texture, out fullPath))
                return true;

            if (texture != null)
            {
                if (ModContentLoaderTexture2D_LoadTexture_Patch.TryGetSavedTexturePath(texture, out fullPath))
                {
                    texturesByPaths[texture] = fullPath;
                    return true;
                }
            }

            fullPath = null;
            return false;
        }

        /// <summary>將紋理條目加入指定類型的分類中。</summary>
        private void AddEntry(TextureResize.TextureType type, BuildableDef def, string fullPath, Texture texture)
        {
            var entry = new KeyValuePair<BuildableDef, string>(def, fullPath);
            textures[type].Add(entry);
            texturesByDefs[texture] = entry;
        }

        /// <summary>清理掃描階段的暫存資料。</summary>
        public void ClearTextureScanData()
        {
            texturesByPaths.Clear();
            texturesByDefs.Clear();
            foreach (var value in textures.Values) { value.Clear(); }
        }
    }
}
