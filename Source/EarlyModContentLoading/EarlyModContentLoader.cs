using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 負責管理與執行 Mod 內容的提早載入邏輯。
    /// </summary>
    public class EarlyModContentLoader
    {
        private Queue<ModContentPack> pendingEarlyLoads;
        private int consecutiveTimeouts;
        private int skipFrames;
        private const int TIMEOUT_THRESHOLD = 3;
        private const int SKIP_FRAME_COUNT = 5;

        /// <summary>
        /// 取得 Mod 內容提早載入是否已完成。
        /// </summary>
        public bool EarlyLoadingComplete { get; private set; }

        /// <summary>
        /// 每幀執行，利用空閒時間預先載入尚未處理的 Mod 內容。
        /// </summary>
        /// <param name="delayedActions">延遲動作管理器的實例，用於確認時間預算。</param>
        public void Update(DelayedActions delayedActions)
        {
            if (XmlChangeDetector.needWriteSettings && DelayedActions.allModClassesCreated)
            {
                XmlChangeDetector.needWriteSettings = false;
                try
                {
                    LoadedModManager.GetMod<FasterGameLoadingMod>().WriteSettings();
                    if (FasterGameLoadingSettings.VerboseLogging)
                    {
                        FGLLog.Message("XML cache invalidated and new hash saved to settings on main thread.");
                    }
                }
                catch (Exception ex)
                {
                    FGLLog.Warning("Failed to save updated XML combined hash:", ex);
                }
            }

            // earlyModContentLoading 採 camelCase 以相容 loading-progress 的反射查詢，詳見 FasterGameLoadingSettings
            if (EarlyLoadingComplete || !FasterGameLoadingSettings.earlyModContentLoading)
                return;

            if (skipFrames > 0)
            {
                skipFrames--;
                return;
            }

            if (pendingEarlyLoads == null)
            {
                var modsToLoad = LoadedModManager.RunningMods
                    .Where(x => !ModContentPack_ReloadContentInt_Patch.loadedMods.Contains(x)
                                && !EarlyLoadSkipList.ShouldSkip(x))
                    .ToList();
                pendingEarlyLoads = new Queue<ModContentPack>(modsToLoad);
            }

            delayedActions.RestartStopwatch();
            while (pendingEarlyLoads.Count > 0)
            {
                var modToLoad = pendingEarlyLoads.Dequeue();
                if (ModContentPack_ReloadContentInt_Patch.loadedMods.Contains(modToLoad))
                    continue;
                try
                {
                    modToLoad.ReloadContentInt();
                    ModContentPack_ReloadContentInt_Patch.loadedMods.Add(modToLoad);
                }
                catch (Exception ex)
                {
                    // 載入失敗時不加入 loadedMods，讓正式流程可以重試
                    FGLLog.Warning($"Early loading failed for {modToLoad.PackageIdPlayerFacing}, will retry in normal flow:", ex);
                }

                // 用完時間預算就讓出這幀，下幀繼續
                if (delayedActions.IsOverBudget)
                {
                    consecutiveTimeouts++;
                    if (consecutiveTimeouts >= TIMEOUT_THRESHOLD)
                    {
                        consecutiveTimeouts = 0;
                        skipFrames = SKIP_FRAME_COUNT;
                    }
                    return;
                }
                else
                {
                    consecutiveTimeouts = 0;
                }
            }

            EarlyLoadingComplete = true;
        }

        /// <summary>
        /// 重置提早載入狀態（語言切換等情況）。
        /// </summary>
        public void Reset()
        {
            pendingEarlyLoads = null;
            EarlyLoadingComplete = false;
            consecutiveTimeouts = 0;
            skipFrames = 0;
        }
    }
}
