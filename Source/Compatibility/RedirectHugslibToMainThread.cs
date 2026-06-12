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
                // 防禦性包覆：若 HugsLib 版本異動導致 ReversePatch stub 未能綁定，
                // 會在此處拋出 NotImplementedException，須捕捉以免中斷主執行緒載入流程
                try
                {
                    OnDefsLoaded(__instance);
                }
                catch (Exception ex)
                {
                    FGLLog.Error("HugsLib OnDefsLoaded 重新導向執行失敗（HugsLib 版本可能不相容）：", ex);
                }
            });
            return false;
        }
    }
}

