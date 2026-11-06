using System;
using System.Diagnostics;
using System.Globalization;
using RimWorld;
using Verse;

namespace FasterGameLoading;

/// <summary>
/// 記錄初次啟動時，延後協程在主選單出現後還要多久才全部完成；開啟詳細日誌時在 log 輸出一行。
/// 到主選單為止的載入時間交給 Loading Progress 等工具，這裡只補它們看不到的主選單之後那一段。
/// 主選單以第一幀畫完為準，在每幀開始時觀察，精度為一幀；早於主選單完成的工作記為 +0.0。
/// 只記錄初次啟動：語言重載會重跑延後協程，但摘要只輸出一次。
/// </summary>
internal sealed class StartupTimeline
{
    private static readonly Stopwatch sinceLoad = Stopwatch.StartNew();

    // 遊戲使用的時間軸；單元測試可換成假時鐘的實例。
    internal static StartupTimeline Instance { get; set; } = new(() => sinceLoad.Elapsed.TotalSeconds);

    private readonly Func<double> clock;
    private double mainMenu = -1;
    private bool mainMenuFrameStarted;
    private double allReady = -1;

    internal StartupTimeline(Func<double> clock)
    {
        this.clock = clock;
    }

    // 摘要尚未輸出（也還沒確定不輸出）；之後不必再每幀觀察。
    internal bool Pending { get; private set; } = true;

    // 延後協程跑完。
    internal void MarkAllReady() => MarkFirst(ref allReady);

    private void MarkFirst(ref double mark)
    {
        if (mark < 0) mark = clock();
    }

    /// <summary>
    /// 每幀由主執行緒呼叫。主選單已出現且延後協程完成時回傳摘要；未經主選單直接進入遊戲時放棄。之後一律回傳 null。
    /// 完成時間由 <see cref="MarkAllReady"/> 在當下記錄，不靠每幀觀察：
    /// 其他 mod 可能在管線完成的下一幀就切換語言，把階段重設回 Idle。
    /// </summary>
    internal string Observe(bool mainMenuShown, bool playing)
    {
        if (!Pending) return null;
        // 主選單的第一幀含介面的首次初始化，實測可超過一秒；等它畫完（下一幀）才算出現，與玩家看到的時間一致。
        if (mainMenuShown && mainMenuFrameStarted) MarkFirst(ref mainMenu);
        mainMenuFrameStarted = mainMenuShown;
        if (mainMenu < 0)
        {
            if (playing) Pending = false;
            return null;
        }
        if (allReady < 0) return null;

        Pending = false;
        return string.Format(CultureInfo.InvariantCulture,
            "Startup timing after the main menu appeared: all deferred work done +{0:F1} s.",
            Math.Max(0, allReady - mainMenu));
    }

    // 主選單已顯示且沒有進行中的長事件。只在主執行緒呼叫。
    internal static bool MainMenuShown =>
        PlayDataLoader.Loaded && Current.ProgramState is ProgramState.Entry && !LongEventHandler.AnyEventNowOrWaiting && Find.UIRoot != null;
}
