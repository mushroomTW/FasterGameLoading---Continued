using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using RimWorld;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 負責管理與執行 Mod 內容的提早載入邏輯。
    /// </summary>
    public class EarlyModContentLoader
    {
        private Queue<ModContentPack> pendingEarlyLoads;
        private bool useImageOptSyncScope;
        private int consecutiveTimeouts;
        private int skipFrames;
        private const int TIMEOUT_THRESHOLD = 3;
        private const int SKIP_FRAME_COUNT = 5;

        // ReloadContentInt 在遊戲 Assembly-CSharp 中是 private，直接呼叫會觸發 JIT 的可見性驗證
        // (MethodAccessException)。提早載入需在遊戲正式流程前觸發內容重載，故經由反射呼叫；
        // 運行時遊戲 DLL 中的 Harmony 攔截 (ModContentPack_ReloadContentInt_Patch) 對反射呼叫同樣生效。
        private static readonly MethodInfo ReloadContentIntMethod = typeof(ModContentPack).GetMethod(
            "ReloadContentInt",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); // NOSONAR S3011: 遊戲 DLL 中該方法為 private，經由反射呼叫是唯一可行的整合方式

        /// <summary>
        /// 經由反射呼叫 ModContentPack.ReloadContentInt(false)，繞過 private 的可見性驗證。
        /// </summary>
        private static void InvokeReloadContentInt(ModContentPack mod)
        {
            if (ReloadContentIntMethod == null)
            {
                throw new MissingMethodException(typeof(ModContentPack).FullName, "ReloadContentInt");
            }
            try
            {
                ReloadContentIntMethod.Invoke(mod, new object[] { false });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                // 反射會將目標方法拋出的例外包裝成 TargetInvocationException，這裡解包後原樣重新拋出。
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            }
        }

        private static volatile bool modClassesCreated;

        static EarlyModContentLoader()
        {
            // 重載流程（切換語言）會重建 mod 類別；重置後等下一次 LoadModXML 才再開放。
            SessionLifecycle.On(LifecyclePhase.LanguageReloading, static () => ModClassesCreated = false);
        }

        /// <summary>
        /// 所有 Mod 建構子是否都已執行完畢；由 <see cref="LoadedModManager_LoadModXML_Patch"/> 在事件緒設定。
        /// </summary>
        internal static bool ModClassesCreated
        {
            get => modClassesCreated;
            set => modClassesCreated = value;
        }

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
            // earlyModContentLoading 採 camelCase 以相容 loading-progress 的反射查詢，詳見 FasterGameLoadingSettings
            if (EarlyLoadingComplete)
                return;

            // 提早載入只在初次載入的長事件期間有意義：PlayData 載入完成後，剩下的內容已由原版
            // ExecuteWhenFinished 接手。之後（例如切換語言）RunningMods 仍是即將被 ClearAllPlayData
            // 銷毀的舊 ModContentPack，再載入只會重複載入並與事件緒的 ClearDestroy 同時存取內容字典。
            if (PlayDataLoader.Loaded)
            {
                if (FasterGameLoadingSettings.earlyModContentLoading && !ModClassesCreated)
                {
                    // 閘門始終沒開：LoadModXML 的 patch 沒生效（遊戲 API 改名或被其他 mod 取代），提早載入整個 session 都沒執行。
                    FGLLog.Warning("Early mod content loading never started: LoadedModManager.LoadModXML hook did not run.");
                }
                EarlyLoadingComplete = true;
                return;
            }

            if (!FasterGameLoadingSettings.earlyModContentLoading)
            {
                return;
            }

            // Mod 建構子在事件緒執行時，這裡（主執行緒）就已開始每幀呼叫；此時 FGL 自己與其他 mod 的
            // Harmony patch 都還沒套用，提早載入的內容會完全繞過它們。等 CreateModClasses 結束再開始。
            if (!ModClassesCreated)
            {
                return;
            }

            if (skipFrames > 0)
            {
                skipFrames--;
                return;
            }

            if (pendingEarlyLoads == null)
            {
                BuildPendingEarlyLoads();
            }

            delayedActions.RestartStopwatch();
            while (pendingEarlyLoads.Count > 0)
            {
                var modToLoad = pendingEarlyLoads.Dequeue();
                if (ModContentPack_ReloadContentInt_Patch.loadedMods.Contains(modToLoad))
                    continue;

                LoadOneModContent(modToLoad);

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

                consecutiveTimeouts = 0;
            }

            EarlyLoadingComplete = true;
        }

        /// <summary>建立本輪待提早載入的 Mod 清單（排除已載入與略過名單中的項目）。</summary>
        private void BuildPendingEarlyLoads()
        {
            // ImageOpt 整合狀態在 Mod 初始化後不會改變；每輪提早載入只判斷一次。
            useImageOptSyncScope = ImageOptEarlyLoadCoordinator.IsInstalled;
            var pending = LoadedModManager.RunningMods
                .Where(static x => !ModContentPack_ReloadContentInt_Patch.loadedMods.Contains(x)
                            && !ProtectedMods.ShouldSkipEarlyLoad(x));
            pendingEarlyLoads = new Queue<ModContentPack>(pending);
        }

        /// <summary>
        /// 提早載入單一 Mod 的內容。載入失敗時不加入 loadedMods，讓正式流程可以重試。
        /// </summary>
        private void LoadOneModContent(ModContentPack modToLoad)
        {
            try
            {
                if (useImageOptSyncScope)
                {
                    using (ImageOptEarlyLoadCoordinator.EnterEarlyLoadSyncScope())
                    {
                        InvokeReloadContentInt(modToLoad);
                    }
                }
                else
                {
                    // 未啟用 ImageOpt 時維持原始熱路徑，不建立或釋放空 scope。
                    InvokeReloadContentInt(modToLoad);
                }
                ModContentPack_ReloadContentInt_Patch.loadedMods.Add(modToLoad);
            }
            catch (Exception ex)
            {
                FGLLog.Warning($"Early loading failed for {modToLoad.PackageIdPlayerFacing}, will retry in normal flow:", ex);
            }
        }
    }
}

