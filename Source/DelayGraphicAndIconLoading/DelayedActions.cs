using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace FasterGameLoading
{
    /// <summary>延遲視覺管線目前進行到的階段。</summary>
    public enum DeferredPhase
    {
        /// <summary>尚未開始（或語言切換後重置）。</summary>
        Idle,
        Graphics,
        Icons,
        AtlasBake,
        Sounds,
        /// <summary>整條管線（含圖集烘焙與音效解析）已跑完。</summary>
        Completed,
    }

    /// <summary>
    /// 延遲動作管理器 — 掛載於獨立的 GameObject 上，負責：
    /// 1. 協調 EarlyModContentLoader 利用 LateUpdate 提早載入 Mod 內容，並在 Update 泵送主執行緒工作。
    /// 2. 延遲視覺管線：收下 PostLoad／ResolveReferences 延後的圖形、圖示與音效解析，
    ///    進入遊戲後依序批次執行，並決定原版靜態圖集烘焙何時可以放行。
    /// </summary>
    public class DelayedActions : MonoBehaviour
    {
        // ── 每幀時間預算 ──
        /// <summary>遊戲中每幀最多佔用 8ms，主選單中最多 50ms。</summary>
        public static float MaxImpactThisFrame => Current.Game != null ? 0.008f : 0.05f;

        // ── 啟動時定案的設定 ──
        private static bool? capturedDeferredVisuals;
        private static bool? capturedAdaptiveBaking;

        /// <summary>
        /// 由 Mod 建構子在 PatchAll 之前呼叫，把延遲視覺與自適應烘焙兩個設定定案到遊戲結束。
        /// 相關補丁只在 PatchAll 時依 Prepare 套用一次；執行期若再讀設定頁的最新值，
        /// 會出現「補丁已套用、管線卻沒跑」的不一致（例如中途關閉延遲載入再切換語言，延遲佇列永遠不會排空）。
        /// 設定頁的變更因此要重新啟動遊戲才生效。
        /// </summary>
        internal static void CaptureStartupSettings()
        {
            capturedDeferredVisuals = FasterGameLoadingSettings.DelayGraphicLoading;
            capturedAdaptiveBaking = FasterGameLoadingSettings.StaticAtlasesBaking;
        }

        /// <summary>單元測試用：回到「尚未定案」，之後直接讀設定值。</summary>
        internal static void ReleaseStartupSettingsForTests()
        {
            capturedDeferredVisuals = null;
            capturedAdaptiveBaking = null;
        }

        /// <summary>本次遊戲行程是否啟用延遲視覺管線。尚未定案時（只發生在單元測試）直接讀設定。</summary>
        internal static bool DeferredVisualsEnabled => capturedDeferredVisuals ?? FasterGameLoadingSettings.DelayGraphicLoading;

        /// <summary>本次遊戲行程是否啟用自適應靜態圖集烘焙。尚未定案時（只發生在單元測試）直接讀設定。</summary>
        internal static bool AdaptiveBakingEnabled => capturedAdaptiveBaking ?? FasterGameLoadingSettings.StaticAtlasesBaking;

        // ── 原版靜態圖集烘焙的放行 ──
        private static bool vanillaStaticBakeAllowed;

        /// <summary>
        /// 原版 GlobalTextureAtlasManager.BakeStaticAtlases 此刻是否可以執行。
        /// 延遲視覺管線關閉時一律放行；開啟時啟動流程那一次會被擋下，
        /// 直到管線要以原版烘焙（自適應烘焙關閉或失敗時）才放行，之後也維持放行。
        /// </summary>
        internal static bool AllowsVanillaStaticBake => !DeferredVisualsEnabled || vanillaStaticBakeAllowed;

        /// <summary>延遲視覺管線目前的階段；<see cref="DeferredPhase.Completed"/> 代表整條管線已跑完。</summary>
        public DeferredPhase Phase { get; private set; }

        static DelayedActions()
        {
            SessionLifecycle.On(LifecyclePhase.LanguageReloading, static () => vanillaStaticBakeAllowed = false);
        }

        // ── 延遲佇列 ──
        // 三組型別佇列共用同一個私有泛型後端。
        private sealed class DeferredQueue<TDef>
        {
            private readonly Queue<(TDef def, Action run)> queue = new();

            public int Count
            {
                get { lock (queue) return queue.Count; }
            }

            public void Enqueue(TDef def, Action run)
            {
                lock (queue)
                {
                    queue.Enqueue((def, run));
                }
            }

            public bool TryDequeue(out TDef def, out Action run)
            {
                lock (queue)
                {
                    if (queue.Count > 0)
                    {
                        (def, run) = queue.Dequeue();
                        return true;
                    }
                }
                def = default;
                run = default;
                return false;
            }

            public void Clear()
            {
                lock (queue) queue.Clear();
            }
        }

        private readonly DeferredQueue<ThingDef> graphicsToLoad = new();
        private readonly DeferredQueue<BuildableDef> iconsToLoad = new();
        private readonly DeferredQueue<SubSoundDef> subSoundDefToResolve = new();

        internal int GraphicsToLoadCount => graphicsToLoad.Count;

        internal int IconsToLoadCount => iconsToLoad.Count;

        internal int SubSoundDefToResolveCount => subSoundDefToResolve.Count;

        public void EnqueueGraphic(ThingDef def, Action action) => graphicsToLoad.Enqueue(def, action);

        public void EnqueueIcon(BuildableDef def, Action action) => iconsToLoad.Enqueue(def, action);

        public void EnqueueSubSound(SubSoundDef def, Action action) => subSoundDefToResolve.Enqueue(def, action);

        /// <summary>語言切換時丟棄所有尚未執行的延遲動作，並回到尚未開始的階段。</summary>
        public void ClearQueues()
        {
            graphicsToLoad.Clear();
            iconsToLoad.Clear();
            subSoundDefToResolve.Clear();
            Phase = DeferredPhase.Idle;
        }

        // ── 提早載入狀態與處理器 ──
        private readonly Stopwatch stopwatch = new();
        private readonly EarlyModContentLoader earlyModContentLoader = new();

        public bool earlyLoadingComplete => earlyModContentLoader.EarlyLoadingComplete;

        /// <summary>目前幀的時間預算是否已耗盡。</summary>
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
        }

        public void LateUpdate()
        {
            earlyModContentLoader.Update(this);
        }

        // ════════════════════════════════════════════════════════════════
        //  遊戲進入後的延遲動作主協程
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// 主協程：依序執行所有延遲的載入動作。
        /// 由 Startup.Postfix 排入 LongEventHandler.toExecuteWhenFinished 觸發。
        /// </summary>
        public IEnumerator PerformActions()
        {
            var loadedDefs = new List<ThingDef>();
            try
            {
                // 延遲視覺管線只在延遲圖形載入啟用時執行，避免預設設定下重複烘焙原版靜態圖集。
                if (DeferredVisualsEnabled)
                {
                    Phase = DeferredPhase.Graphics;
                    yield return LoadDeferredGraphicsCoroutine(loadedDefs);
                    // 圖示只需要剛載入的圖形、與圖集無關；排在烘焙之前，玩家不必等整批圖集烘焙完才看到正確圖示。
                    Phase = DeferredPhase.Icons;
                    yield return LoadDeferredIconsCoroutine();
                    Phase = DeferredPhase.AtlasBake;
                    yield return BakeDeferredAtlasesCoroutine();
                    UpdateMapMeshForLoadedDefs(loadedDefs);
                }
                Phase = DeferredPhase.Sounds;
                yield return ResolveSubSoundDefsCoroutine();
                Phase = DeferredPhase.Completed;
            }
            finally
            {
                SoundStarter_Patch.Unpatch();
                GraphicData_Init_Patch.savedGraphics.Clear();
                stopwatch.Stop();
                // 保持 Update 泵送運作，讓後續背景貼圖請求仍能安全回到 Unity 主執行緒。
            }
        }

        /// <summary>
        /// 執行靜態圖集烘焙：執行自適應烘焙，失敗時 fallback 到原版流程。
        /// </summary>
        private IEnumerator BakeDeferredAtlasesCoroutine()
        {
            if (AdaptiveBakingEnabled)
            {
                var adaptiveBake = AdaptiveAtlasBaker.PerformAdaptiveStaticAtlasBake(this);
                while (adaptiveBake.MoveNext())
                {
                    yield return adaptiveBake.Current;
                }

                if (AdaptiveAtlasBaker.LastBakeFailed)
                {
                    FGLLog.Warning("Adaptive bake failed, falling back to vanilla static atlas baking");
                    AtlasBakeDiagnostics.LogPotentialMaskIssues("deferred fallback");
                    BakeStaticAtlasesWithVanilla();
                    FGLLog.Message("Vanilla static atlas baking complete");
                }
            }
            else
            {
                FGLLog.Message("Starting deferred vanilla static atlas baking");
                AtlasBakeDiagnostics.LogPotentialMaskIssues("deferred vanilla");
                BakeStaticAtlasesWithVanilla();
                FGLLog.Message("Deferred vanilla static atlas baking complete");
            }
        }

        private static void BakeStaticAtlasesWithVanilla()
        {
            vanillaStaticBakeAllowed = true;
            GlobalTextureAtlasManager.BakeStaticAtlases();
        }

        /// <summary>
        /// 三個預算協程的共用 driver：外層 while＋預算內批次＋yield＋RestartStopwatch。
        /// 圖形／圖示在非主執行緒時讓出執行權（防禦性保護）；音效解析不碰 Unity 物件，可直接執行。
        /// </summary>
        private IEnumerator DrainQueue<TDef>(DeferredQueue<TDef> queue, Action<TDef, Action> runOne, bool requireMainThread)
        {
            RestartStopwatch();
            while (queue.Count > 0)
            {
                // 協程只在主執行緒被恢復執行，此檢查僅為防禦性保護。
                // 若非主執行緒，讓出執行權後由外層 while 重新檢查，不落穿到 Unity 工作。
                if (requireMainThread && !UnityData.IsInMainThread)
                {
                    yield return 0;
                    continue;
                }
                while (queue.Count > 0 && !IsOverBudget)
                {
                    if (!queue.TryDequeue(out var def, out var run))
                        break;

                    runOne(def, run);
                }

                if (queue.Count > 0)
                {
                    yield return 0;
                    RestartStopwatch();
                }
            }
        }

        /// <summary>在時間預算內批次載入延遲的圖形紋理；載入成功的 ThingDef 加入 <paramref name="loadedDefs"/>，供更新地圖網格。</summary>
        internal IEnumerator LoadDeferredGraphicsCoroutine(ICollection<ThingDef> loadedDefs)
        {
            FGLLog.Message($"Starting deferred graphics: {GraphicsToLoadCount.ToString(CultureInfo.InvariantCulture)}");
            var drain = DrainQueue(graphicsToLoad, (def, run) => LoadOneGraphic(def, run, loadedDefs), requireMainThread: true);
            while (drain.MoveNext())
            {
                yield return drain.Current;
            }
            FGLLog.Message("Deferred graphics loaded");
        }

        /// <summary>
        /// 執行單一 ThingDef 的延遲圖形載入動作。
        /// UI 圖示交給延遲圖示佇列裡的原版回呼（ResolveIcon 另外會設定顏色、UI 材質與角度），這裡不自行填入。
        /// 個別 def 的例外只記錄不外傳，避免中斷整個延遲載入協程。
        /// </summary>
        private static void LoadOneGraphic(ThingDef def, Action action, ICollection<ThingDef> loadedDefs)
        {
            bool graphicActionSucceeded = false;
            try
            {
                action();
                loadedDefs.Add(def);
                graphicActionSucceeded = true;
            }
            catch (Exception ex)
            {
                FGLLog.Warning($"Error loading graphic for {def}:", ex);
            }
            // 僅在圖形動作成功後才呼叫 PostLoadSpecial，避免傳入損壞的圖形資料
            if (graphicActionSucceeded)
            {
                def.plant?.PostLoadSpecial(def);
            }
        }

        /// <summary>
        /// 將已載入的圖形標記為需要重新繪製地圖網格，
        /// 確保延遲載入的圖形在地圖上立即顯示。
        /// </summary>
        internal static void UpdateMapMeshForLoadedDefs(IReadOnlyList<ThingDef> loadedDefs)
        {
            try
            {
                if (Current.Game != null)
                {
                    foreach (var map in Find.Maps)
                    {
                        if (map.mapDrawer.sections != null)
                        {
                            foreach (var thing in map.listerThings.ThingsOfDefs(loadedDefs))
                            {
                                map.mapDrawer.MapMeshDirty(thing.Position,
                                    MapMeshFlagDefOf.Things | MapMeshFlagDefOf.Buildings);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FGLLog.Warning("Error updating map mesh:", ex);
            }
        }

        /// <summary>在時間預算內批次載入延遲的圖示紋理。</summary>
        internal IEnumerator LoadDeferredIconsCoroutine()
        {
            FGLLog.Message($"Starting deferred icons: {IconsToLoadCount.ToString(CultureInfo.InvariantCulture)}");
            var drain = DrainQueue(iconsToLoad, static (def, run) => LoadOneIcon(def, run), requireMainThread: true);
            while (drain.MoveNext())
            {
                yield return drain.Current;
            }
            FGLLog.Message("Deferred icons loaded");
        }

        /// <summary>
        /// 執行單一 BuildableDef 的延遲圖示載入動作。
        /// 圖示已非 BadTex 代表其他路徑已補上，直接跳過；
        /// 個別 def 的例外只記錄不外傳，避免中斷整個延遲載入協程。
        /// </summary>
        private static void LoadOneIcon(BuildableDef def, Action action)
        {
            if (def.uiIcon != BaseContent.BadTex)
            {
                return;
            }

            try
            {
                action();
            }
            catch (Exception ex)
            {
                FGLLog.Warning($"Error loading icon for {def}:", ex);
            }
        }

        /// <summary>
        /// 在時間預算內批次解析延遲的 SubSoundDef。
        /// 佇列清空後取消 SoundStarter 攔截，讓主選單與遊戲內音效恢復原版流程。
        /// </summary>
        internal IEnumerator ResolveSubSoundDefsCoroutine()
        {
            FGLLog.Message($"Starting SubSoundDef resolution: {SubSoundDefToResolveCount.ToString(CultureInfo.InvariantCulture)}");
            var drain = DrainQueue(subSoundDefToResolve, static (def, run) => TryRunSubSoundAction(def, run, logError: false), requireMainThread: false);
            while (drain.MoveNext())
            {
                yield return drain.Current;
            }
            // 協程已執行完畢，所有延遲的 SubSoundDef 已解析完成，在此時安全取消攔截，
            // 確保若玩家留在主選單也能正常播放按鈕與背景聲音。
            SoundStarter_Patch.Unpatch();
            FGLLog.Message("SubSoundDef resolution complete");
        }

        /// <summary>
        /// 立即解析所有仍在佇列中的 SubSoundDef，然後取消 SoundStarter 攔截。
        /// 世界初始化收尾時使用（此時必須有聲音），個別錯誤沿用原版的 Error 級別記錄。
        /// </summary>
        internal void ResolvePendingSubSounds()
        {
            while (subSoundDefToResolve.TryDequeue(out var def, out var run))
            {
                TryRunSubSoundAction(def, run, logError: true);
            }
            SoundStarter_Patch.Unpatch();
        }

        /// <summary>執行單一 SubSound 延遲動作；個別例外只記錄不外傳，避免中斷批次流程。</summary>
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
}
