using HarmonyLib;
using System;
using System.Reflection;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 與 HugsLib 的相容層：將 HugsLibController.OnDefsLoaded 重新導向到主執行緒執行。
    /// 避免 HugsLib 在提早載入階段就觸發初始化，導致與 FasterGameLoading 的延遲載入機制衝突。
    /// 使用反射避免對 HugsLib 的硬依賴。
    /// </summary>
    [HarmonyPatch]
    public static class RedirectHugslibToMainThread
    {
        public static MethodBase targetMethod = AccessTools.Method("HugsLib.HugsLibController:OnDefsLoaded");
        public static bool Prepare() => FasterGameLoadingSettings.DelayGraphicLoading
            && targetMethod != null;
        public static MethodBase TargetMethod() => targetMethod;

        [HarmonyReversePatch]
        public static void OnDefsLoaded(object __instance) => throw new NotImplementedException("僅供 HarmonyReversePatch 使用的 stub");

        public static bool Prefix(object __instance)
        {
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                OnDefsLoaded(__instance);
            });
            return false;
        }
    }
}

