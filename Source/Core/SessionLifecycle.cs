using System;
using System.Collections.Generic;

namespace FasterGameLoading
{
    /// <summary>一次遊戲行程中，FGL 各模組需要收到通知的時間點。</summary>
    public enum LifecyclePhase
    {
        /// <summary>
        /// StaticConstructorOnStartupUtility.CallAll 完成：初次啟動與每次切換語言重載後都會觸發。
        /// 用來結算本輪載入的資料（例如本 session 查過的型別）、釋放只在載入期間需要的暫存。
        /// </summary>
        StartupCompleted,

        /// <summary>
        /// 切換語言、即將 ClearAllPlayData 重載所有內容之前。原版會銷毀並重新載入 Def 與貼圖，
        /// 以舊物件為鍵或值的快取必須在此清空，讓重載流程完整執行。
        /// </summary>
        LanguageReloading,
    }

    /// <summary>
    /// 各模組在自己的靜態建構子或 Mod 建構子中以 <see cref="On"/> 登記處理常式，
    /// 由 Startup 與 LanguageDatabase.SelectLanguage 的補丁在對應時間點 <see cref="Raise"/>。
    /// 同一階段依登記順序執行；個別處理常式的例外只記錄，不影響其餘處理常式。
    /// </summary>
    public static class SessionLifecycle
    {
        private static readonly Dictionary<LifecyclePhase, List<Action>> handlers = new Dictionary<LifecyclePhase, List<Action>>
        {
            [LifecyclePhase.StartupCompleted] = new List<Action>(),
            [LifecyclePhase.LanguageReloading] = new List<Action>(),
        };

        /// <summary>登記在 <paramref name="phase"/> 時要執行的處理常式；每次進入該階段都會執行。</summary>
        public static void On(LifecyclePhase phase, Action handler)
        {
            if (handler == null) return;
            lock (handlers)
            {
                handlers[phase].Add(handler);
            }
        }

        /// <summary>依登記順序執行 <paramref name="phase"/> 的所有處理常式。</summary>
        internal static void Raise(LifecyclePhase phase)
        {
            Action[] snapshot;
            lock (handlers)
            {
                snapshot = handlers[phase].ToArray();
            }
            foreach (var handler in snapshot)
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    FGLLog.Error($"Error in {phase} handler:", ex);
                }
            }
        }
    }
}
