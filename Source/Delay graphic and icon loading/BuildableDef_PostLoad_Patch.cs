using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 BuildableDef.PostLoad 中的 LongEventHandler.ExecuteWhenFinished 呼叫，
    /// 保持 UI 圖示在 RimWorld 原本的載入時機解析，避免選單顯示 BadTex 紅叉。
    /// </summary>
    [HarmonyPatch(typeof(BuildableDef), "PostLoad")]
    public static class BuildableDef_PostLoad_Patch
    {
        public static bool Prepare() => FasterGameLoadingSettings.DelayGraphicLoading;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codeInstructions)
        {
            var execute = AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.ExecuteWhenFinished));
            var executeNow = AccessTools.Method(typeof(BuildableDef_PostLoad_Patch), nameof(ExecuteImmediately));
            foreach (var code in codeInstructions)
            {
                if (code.Calls(execute))
                {
                    // 雖然在「延遲」模組中，但 UI 圖示必須立即載入：
                    // 延遲圖示會讓主選單顯示 BadTex 紅叉，因為選單在載入完成前就需要渲染圖示
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, executeNow);
                }
                else
                {
                    yield return code;
                }
            }
        }

        /// <summary>
        /// BuildableDef UI 圖示必須立即載入，不能延遲。
        /// 延遲它們會在 BaseContent.BadTex 上留下許多定義，顯示為紅色叉子。
        /// 此方法名稱中的「Immediately」是為強調這點，與 ThingDef_PostLoad_Patch 的延遲行為形成對比。
        /// </summary>
        public static void ExecuteImmediately(Action action, BuildableDef def)
        {
            LongEventHandler.ExecuteWhenFinished(action);
        }

        /// <summary>
        /// 判斷此 ThingDef 的圖示是否需要立即載入。
        /// 武器、裝備、食物、建築、殖民者等常用類型立即載入，
        /// 其餘（如背景裝飾物）則延遲載入。
        /// </summary>
        public static bool ShouldBeLoadedImmediately(this ThingDef thingDef)
        {
            // 基礎建築和藍圖必須立即載入
            if (thingDef.designationCategory != null || !thingDef.uiIconPath.NullOrEmpty())
                return true;

            if (thingDef.IsBlueprint || thingDef.IsFrame)
                return true;

            if (thingDef.graphicData != null && thingDef.graphicData.Linked)
                return true;

            if (thingDef.thingClass != null && thingDef.thingClass.Name == FGLConsts.BuildingPipe)
                return true;

            // 醫療用品
            if (typeof(Medicine).IsAssignableFrom(thingDef.thingClass)
                || thingDef.orderedTakeGroup?.defName == FGLConsts.MedicineDefName)
                return true;

            // 武器和裝備 - 殖民者常用物品
            if (thingDef.IsWeapon || thingDef.IsApparel)
                return true;

            // 食物 - 使用 ingestible 屬性檢查（避免在 PostLoad 階段訪問 StatDef）
            if (thingDef.ingestible != null || thingDef.IsStuff)
                return true;

            // 殖民者和動物
            if (thingDef.race != null)
                return true;

            // 常見家具和工作台
            if (thingDef.thingCategories != null && thingDef.thingCategories.Any(cat =>
                cat.defName.Contains(FGLConsts.FurnitureKeyword) ||
                cat.defName.Contains(FGLConsts.ProductionKeyword) ||
                cat.defName.Contains(FGLConsts.SecurityKeyword)))
                return true;

            return false;
        }
    }
}
