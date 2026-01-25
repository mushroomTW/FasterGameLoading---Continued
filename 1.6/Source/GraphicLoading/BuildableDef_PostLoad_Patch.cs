using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Verse;

namespace FasterGameLoading
{
    [HarmonyPatch(typeof(BuildableDef), "PostLoad")]
    public static class BuildableDef_PostLoad_Patch
    {
        public static bool Prepare() => FasterGameLoadingSettings.delayGraphicLoading;
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codeInstructions)
        {
            var execute = AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.ExecuteWhenFinished));
            var executeDelayed = AccessTools.Method(typeof(BuildableDef_PostLoad_Patch), nameof(ExecuteDelayed));
            foreach (var code in codeInstructions)
            {
                if (code.Calls(execute))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, executeDelayed);
                }
                else
                {
                    yield return code;
                }
            }
        }

        public static void ExecuteDelayed(Action action, BuildableDef def)
        {
            if (def is ThingDef thingDef && thingDef.ShouldBeLoadedImmediately())
            {
                LongEventHandler.ExecuteWhenFinished(action);
            }
            else
            {
                FasterGameLoadingMod.delayedActions.iconsToLoad.Add((def, action));
            }
        }

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
