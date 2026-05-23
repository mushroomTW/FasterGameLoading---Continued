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
            var executeDelayed = AccessTools.Method(typeof(BuildableDef_PostLoad_Patch), nameof(ExecuteDelayed));
            foreach (var code in codeInstructions)
            {
                if (code.Calls(execute))
                {
                    // Replace ExecuteWhenFinished(action) with ExecuteDelayed(action, this)
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, executeDelayed);
                }
                else
                {
                    yield return code;
                }
            }
        }

        /// <summary>
        ///BuildableDef UI 圖示在早期 UI 設定期間透過指定和架構選單讀取。
        ///延遲它們會在 BaseContent.BadTex 上留下許多定義，顯示為紅色叉子。
        /// </summary>
        public static void ExecuteDelayed(Action action, BuildableDef def)
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
            if (thingDef.IsBlueprint || thingDef.IsFrame)
                return true;

            // 連接型圖形（如牆壁、管道）必須立即載入
            if (thingDef.graphicData != null && thingDef.graphicData.Linked)
                return true;

            // 特殊建築類型
            if (thingDef.thingClass != null && thingDef.thingClass.Name == "Building_Pipe")
                return true;

            // 醫療用品
            if (typeof(Medicine).IsAssignableFrom(thingDef.thingClass)
                || thingDef.orderedTakeGroup?.defName == "Medicine")
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
                cat.defName.Contains("Furniture") ||
                cat.defName.Contains("Production") ||
                cat.defName.Contains("Security")))
                return true;

            return false;
        }
    }
}
