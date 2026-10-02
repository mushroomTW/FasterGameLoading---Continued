using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 與 Humanoid Alien Races（HAR）並存：
    /// HAR 在 ThingDef_AlienRace.ResolveReferences 把 AlienPartGenerator.LoadGraphicsHook 掛上 Application.onBeforeRender，
    /// 只要 HAR 自己的貼圖已載入，下一幀就以 ContentFinder 計算所有種族部件的貼圖變體數量。
    /// 原版在同一幀內載入所有 mod 的貼圖，FGL 的提早載入則分幀進行：HAR 先載入時，尚未載入的種族 mod 變體數會被算成 0，貼圖遺失。
    /// 這裡讓 LoadGraphicsHook 等到所有執行中 mod 的內容都載入後才執行，與原版的先後順序相同，
    /// HAR 與依賴它的 mod 因此可以照常提早載入、平行解析 XML。
    /// </summary>
    internal static class AlienRaceGraphicsHookGate
    {
        private static readonly MethodBase target = AccessTools.Method("AlienRace.AlienPartGenerator:LoadGraphicsHook");

        private static volatile bool released;

        static AlienRaceGraphicsHookGate()
        {
            // 保險：有 mod 的內容沒經過 ReloadContentInt 記錄時，最晚在本輪 CallAll 完成後放行。
            SessionLifecycle.On(LifecyclePhase.StartupCompleted, static () => released = true);
            // 切換語言會重跑 ResolveReferences 與內容載入，HAR 重新掛上 hook，必須再等這一輪的內容載入。
            SessionLifecycle.On(LifecyclePhase.LanguageReloading, static () => released = false);
        }

        /// <summary>
        /// HAR 的 LoadGraphicsHook 是否已由這裡延後。HAR 未啟用、改了 API 或 patch 失敗時為 false，
        /// HAR 本體與依賴它的 mod 仍須排除在提早載入之外，見 <see cref="ProtectedMods.ShouldSkipEarlyLoad(string, object)"/>。
        /// </summary>
        internal static bool DefersGraphicsHook { get; private set; }

        internal static MethodBase TargetMethod() => target;

        /// <summary>
        /// 在 FGL 建構子中、貼圖預讀查詢排除名單之前套用。不放進 PatchAll：第三方方法 patch 失敗時，
        /// 只讓 HAR 回到排除名單，不會中斷 FGL 其餘補丁的套用。
        /// </summary>
        internal static void TryPatch(Harmony harmony)
        {
            if (target == null) return;
            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(AlienRaceGraphicsHookGate), nameof(Prefix)));
                DefersGraphicsHook = true;
            }
            catch (Exception ex)
            {
                FGLLog.Warning("Failed to patch HAR LoadGraphicsHook; HAR and its race mods will not be loaded early:", ex);
            }
        }

        /// <summary>還有執行中 mod 的內容未載入時略過原方法；HAR 仍訂閱 onBeforeRender，下一幀會再呼叫。</summary>
        internal static bool Prefix()
        {
            if (released) return true;
            foreach (var mod in LoadedModManager.RunningMods)
            {
                if (!ModContentPack_ReloadContentInt_Patch.loadedMods.Contains(mod)) return false;
            }
            released = true;
            return true;
        }
    }
}
