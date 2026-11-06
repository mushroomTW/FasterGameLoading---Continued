using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace FasterGameLoading;

/// <summary>
/// 延遲動作管理器 — 掛載於獨立的 GameObject 上，負責：
/// 1. 協調 EarlyModContentLoader 利用 LateUpdate 提早載入 Mod 內容，並在 Update 泵送主執行緒工作。
/// 2. 收下 ResolveReferences 延後的音效解析，進入遊戲後分幀執行。
/// </summary>
public class DelayedActions : MonoBehaviour
{
    // ── 每幀時間預算 ──
    // 遊戲中每幀最多佔用 8ms，主選單中最多 50ms。
    public static float MaxImpactThisFrame => Current.Game != null ? 0.008f : 0.05f;

    // 延後協程是否已跑完；語言切換時重置。
    public bool DeferredWorkCompleted { get; private set; }

    // ── 延遲佇列 ──
    // 音效佇列只決定分幀解析的順序；要執行的動作存在 pendingSubSounds，
    // 播放請求可能先一步從那裡取走並當場解析（見 ResolveSubSoundNow），佇列輪到時便直接略過。
    // 原版流程中生產者與消費者皆在主執行緒；仍用 Concurrent 集合是為防禦其他 mod 在背景執行緒觸發 ResolveReferences。
    private readonly ConcurrentQueue<SubSoundDef> subSoundDefToResolve = new();

    private readonly ConcurrentDictionary<SubSoundDef, Action> pendingSubSounds = new();

    internal int SubSoundDefToResolveCount => pendingSubSounds.Count;

    /// <summary>
    /// 排入一個 SubSoundDef 的解析動作。同一個 SubSoundDef 重複排入時只保留最後一個動作：
    /// 原版的回呼每次都會先清空 resolvedGrains 再重建，執行一次與多次結果相同。
    /// </summary>
    public void EnqueueSubSound(SubSoundDef def, Action action)
    {
        pendingSubSounds[def] = action;
        subSoundDefToResolve.Enqueue(def);
    }

    // 語言切換時丟棄所有尚未執行的延遲動作，並回到尚未完成的狀態。
    public void ClearQueues()
    {
        subSoundDefToResolve.Clear();
        pendingSubSounds.Clear();
        DeferredWorkCompleted = false;
    }

    // ── 提早載入狀態與處理器 ──
    private readonly Stopwatch stopwatch = new();
    private readonly EarlyModContentLoader earlyModContentLoader = new();

    public bool earlyLoadingComplete => earlyModContentLoader.EarlyLoadingComplete;

    // 目前幀的時間預算是否已耗盡。
    public bool IsOverBudget => (float)stopwatch.ElapsedTicks / Stopwatch.Frequency >= MaxImpactThisFrame;

    internal void RestartStopwatch() => stopwatch.Restart();

    // ════════════════════════════════════════════════════════════════
    //  每幀的主執行緒工作
    // ════════════════════════════════════════════════════════════════

    // MA0038/S2325: Unity 只會對執行個體呼叫 Update 訊息，不能改成 static。
#pragma warning disable MA0038, S2325
    public void Update()
#pragma warning restore MA0038, S2325
    {
        // 在主執行緒排空背景執行緒累積的日誌，避免背景緒直接呼叫非執行緒安全的 Verse.Log
        FGLLog.FlushPending();
        MainThreadTextureLoader.Drain();
        SessionLifecycle.DrainMainThreadRaises();
        ReportStartupTiming();
    }

    /// <summary>
    /// 初次啟動的延後協程完成後產生一次啟動時間摘要，開啟詳細日誌時才寫進 log。
    /// 不論日誌開關都要觀察，主選單時間才是真的出現時間；啟動後才開啟詳細日誌時不輸出。
    /// </summary>
    internal static void ReportStartupTiming()
    {
        var timeline = StartupTimeline.Instance;
        if (!timeline.Pending) return;
        var summary = timeline.Observe(StartupTimeline.MainMenuShown, Current.ProgramState is ProgramState.Playing);
        if (summary != null && FasterGameLoadingSettings.VerboseLogging) FGLLog.Message(summary);
    }

    public void LateUpdate()
    {
        earlyModContentLoader.Update(this);
    }

    // ════════════════════════════════════════════════════════════════
    //  遊戲進入後的延後協程
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 主協程：解析延後的音效。
    /// 由 Startup.Postfix 排入 LongEventHandler.toExecuteWhenFinished 觸發。
    /// </summary>
    public IEnumerator PerformActions()
    {
        try
        {
            yield return ResolveSubSoundDefsCoroutine();
            DeferredWorkCompleted = true;
            StartupTimeline.Instance.MarkAllReady();
        }
        finally
        {
            SoundStarter_Patch.Unpatch();
            stopwatch.Stop();
            // 保持 Update 泵送運作，讓後續背景貼圖請求仍能安全回到 Unity 主執行緒。
        }
    }

    /// <summary>
    /// 在時間預算內批次解析延遲的 SubSoundDef（解析會呼叫 Resources.LoadAll，只能在主執行緒）。
    /// 佇列清空後取消 SoundStarter 攔截，讓主選單與遊戲內音效恢復原版流程。
    /// </summary>
    internal IEnumerator ResolveSubSoundDefsCoroutine()
    {
        FGLLog.Message($"Starting SubSoundDef resolution: {SubSoundDefToResolveCount.ToString(CultureInfo.InvariantCulture)}");
        RestartStopwatch();
        while (!subSoundDefToResolve.IsEmpty)
        {
            // 協程只在主執行緒被恢復執行，此檢查僅為防禦性保護。
            if (!UnityData.IsInMainThread)
            {
                yield return 0;
                continue;
            }
            while (!IsOverBudget && subSoundDefToResolve.TryDequeue(out var def))
            {
                TryRunPendingSubSound(def, logError: false);
            }

            if (!subSoundDefToResolve.IsEmpty)
            {
                yield return 0;
                RestartStopwatch();
            }
        }
        // 所有延遲的 SubSoundDef 已解析完成，播放前不必再檢查，在此時取消攔截。
        SoundStarter_Patch.Unpatch();
        FGLLog.Message("SubSoundDef resolution complete");
    }

    /// <summary>
    /// 立即解析所有仍在佇列中的 SubSoundDef，然後取消 SoundStarter 攔截。
    /// 世界初始化收尾時使用（此時必須有聲音），個別錯誤沿用原版的 Error 級別記錄。
    /// </summary>
    internal void ResolvePendingSubSounds()
    {
        while (subSoundDefToResolve.TryDequeue(out var def))
        {
            TryRunPendingSubSound(def, logError: true);
        }
        SoundStarter_Patch.Unpatch();
    }

    /// <summary>
    /// 播放前當場解析仍在等待中的 SubSoundDef（由 SoundStarter_Patch 呼叫）；不在等待中的不做任何事。
    /// 解析會讀取音訊資源（Resources.LoadAll），呼叫端必須確認在主執行緒。
    /// </summary>
    internal void ResolveSubSoundNow(SubSoundDef def) => TryRunPendingSubSound(def, logError: false);

    // 取走並執行 SubSoundDef 尚未執行的解析動作；已被播放請求先行解析的直接略過。
    private void TryRunPendingSubSound(SubSoundDef def, bool logError)
    {
        if (pendingSubSounds.TryRemove(def, out var run))
        {
            TryRunSubSoundAction(def, run, logError);
        }
    }

    // 執行單一 SubSound 延遲動作；個別例外只記錄不外傳，避免中斷批次流程。
    private static void TryRunSubSoundAction(SubSoundDef def, Action run, bool logError)
    {
        try
        {
            run();
        }
        catch (Exception ex)
        {
            if (logError)
                FGLLog.Error($"Error resolving AudioGrain for {def}", ex);
            else
                FGLLog.Warning($"Error resolving AudioGrain for {def}:", ex);
        }
    }
}
