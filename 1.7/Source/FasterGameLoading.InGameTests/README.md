# RimWorld 1.7 遊戲內測試與基準測試

此目錄是獨立的開發用附屬模組，使用 [DevTools](https://github.com/SmashPhil/DevTools) 的 Test Explorer、`Expect` 與 Benchmark。產物位於 `Mod/Assemblies/`，不加入 FGL 正式版的 `1.7/Assemblies/`；`_PublisherPlus.xml` 排除整個 `1.7/Source/`。1.6 的建置與測試不使用 DevTools。

原有 `FasterGameLoading.Tests` 是另一個 .NET 10 + NUnit 專案，驗證可在不啟動遊戲的環境執行的邏輯；請繼續用：

```powershell
dotnet test --project 1.7/Source/FasterGameLoading.Tests/FasterGameLoading.Tests.csproj
```

## 準備與建置

DevTools 固定使用 commit [`e0b33dfc222de214c45af979c8a45a74e1fb06f3`](https://github.com/SmashPhil/DevTools/commit/e0b33dfc222de214c45af979c8a45a74e1fb06f3)。上游目前沒有預編譯發佈檔；請在本儲存庫根目錄執行以下 PowerShell 指令，使用本機 RimWorld 1.7 的組件建置：

```powershell
git clone https://github.com/SmashPhil/DevTools.git .test-temp/DevTools
git -C .test-temp/DevTools checkout e0b33dfc222de214c45af979c8a45a74e1fb06f3
$rimworld = (Resolve-Path ../..).Path
$devMod = Join-Path (Get-Location) '.test-temp/DevTools/_mod'
dotnet build .test-temp/DevTools/DevTools/DevTools.csproj -c ReleaseTesting "-p:ModDir=$devMod" "-p:RefDir=$rimworld"
dotnet build 1.7/Source/FasterGameLoading.csproj -c Release
dotnet build 1.7/Source/FasterGameLoading.InGameTests/FasterGameLoading.InGameTests.csproj -c Release
```

最後一個專案引用固定版本的 `DevTools.dll`，並只把它複製到開發附屬模組的 `Mod/Assemblies/`。若放在其他位置，可對該建置命令指定 `-p:DevToolsDll=<DLL 絕對路徑>`。遊戲、Harmony 與 FGL 的組件皆以 RimWorld 1.7 安裝及本儲存庫的實際檔案為準。

## 在遊戲內執行

用 rimworld-mod-mcp 的 `run_test_cycle` 啟動隔離 session：`path` 指向此目錄的 `Mod`，`companion_mods` 至少包含 `brrainz.harmony` 與 `Taranchuk.FasterGameLoading`。DevTools DLL 已在開發附屬模組內，不需額外訂閱。此模組刻意排在 FGL 之前，以便探針觀察提早載入，並把 `seed_config` 重新命名的 FGL 設定檔套回 FGL。

等 Player.log 出現 `[FGL InGameTests] Round 1: ready for DevTools tests` 後，開啟 RimWorld 開發者模式，於開發者工具按鈕選此附屬模組的 **Test Explorer → Run All**。DevTools fixture 另有最高優先權的等待測試：若透過 CLI 提早啟動，會逐幀等待延遲管線完成；逾時會標記失敗並停止後續測試。地圖測試使用 `TestType.Playing`，DevTools 執行器會切換到遊戲場景；也可用 `quicktest=true` 先進入地圖。DevTools 可對單項測試按 **Run Selected**；單項執行時請先確認上述 ready 訊息。

也可在已啟用這些模組的 RimWorld 啟動參數中加入 `--pid Taranchuk.FasterGameLoading.InGameTests --test --exit`。DevTools 會在完成後以 0／1 表示通過／失敗。想檢查載入錯誤可改用 `--smoke-test --exit`；這使用 DevTools 內建 Smoke Test，檢查啟動警告與錯誤。已在隔離的 RimWorld 1.7 session 驗證另一種 CLI 佈署方式：暫時把 `DevTools.dll` 與 `FasterGameLoading.InGameTests.dll` 放入 FGL 的 `1.7/Assemblies/`，以 `--pid Taranchuk.FasterGameLoading --test --exit` 執行，64 項通過後立即移除兩個開發 DLL。獨立附屬模組的 CLI 路徑仍需另行實測。

Smoke Test 的 `Mod/Configs/SmokeTestConfig.xml` 只允許兩種模組相依宣告警告；`SmokeLogScopeTests` 再讀取原始日誌，確認它們來自模組清單掃描，且警告所指模組未啟用。FGL 或已啟用模組的同類警告，以及其他警告和所有錯誤，仍會使測試失敗。所選 DevTools commit 將 `[SmokeTest]` 宣告為內部屬性，因此開發測試專案僅對 DevTools 使用既有的 Publicizer；正式 FGL 組件不參考它。此檢查限於 DevTools 執行時可取得的遊戲日誌。在上述暫時同模組佈署的隔離 session 中，`--smoke-test --exit` 已以退出碼 0 通過 3 項檢查；測試後已移除暫時 DLL 與設定檔。

| 情境 | `seed_config` | `quicktest` | 額外 `companion_mods` |
| --- | --- | --- | --- |
| 預設與語言重載 | 無 | `false` | 無 |
| 全開 | `Profiles/AllOn/Mod_FasterGameLoading---Continued_FasterGameLoadingMod.xml` | `false` | 無 |
| 地圖音效 | 無 | `true` | 無 |
| HAR 等相容性 | 全開設定檔 | `false` | HugsLib、HAR、Ancot、ChezhouLib 與對應種族模組 |
| GS+ | 無 | `false` | `Telefonmast.GraphicsSettings` |
| Loading Progress | 無 | `false` | `ilyvion.LoadingProgress` |
| Hyperdrive | 無 | `false` | `vopaga.hyperdrive` |

相容性案例依實際載入的模組決定是否有驗證對象。DevTools Test Plan 可在不同模組清單中啟動獨立程序，適合上表矩陣；上游此 commit 沒有可核對的 XML 範例，因此本次不加入未經驗證的 plan 格式，先逐組使用隔離 session。

語言重載案例需在主選單完成第 1 輪後切換至日文，等待 `Round 2: ready`，再執行 Test Explorer。切換前後的早載入次數、圖集參照及語言狀態由開發模組探針保存。若其他模組將切換語言改為重啟遊戲，這組跨輪次驗證不適用。

## 遷移取捨

原有的遊戲內斷言與有效案例已改用 DevTools fixture／`Expect`；NUnit 專案沒有遷移。舊版自動觸發 RimTest Redux runner 及其結果視窗已刪除，改由 DevTools Test Explorer／CLI 執行；語言重載改為先手動切換語言再跑第 2 輪。無法對應 1.7 功能的案例均已移除：`AdaptiveBakeDidNotFallBack`（依賴 1.7 不存在的 `AdaptiveAtlasBaker`）、`TexturePrefetchServesTexturesFromMemory`（依賴 1.7 不使用的 FGL `TexturePrefetcher`），以及隨 1.7 移除延遲圖形載入而刪除的 `DeferredGraphicsTests`。相關的 1.7「全開」設定也移除了延遲圖形、自適應烘焙與快速圖集壓縮選項。

## Benchmark 與啟動速度

DevTools 的 **Benchmark** 頁提供 `FGL type lookup`，比較遊戲內重複型別查詢；這是熱路徑基準測試，不能代替完整啟動計時。開發模組亦保留 `Round N timing` 日誌，記錄啟動起算秒數、GC、主執行緒檔案讀取與一次完整型別列舉。

此頁也提供 GenTypes 完整名稱加命名空間提示的快取／reverse-patch 原版對照。另可在 NUnit 專案手動執行 `TypeLookupBenchmarks` 的兩個 Explicit 基準，量測 Prefix／Postfix 的執行緒配置量與已列舉組件的全名預熱；平常測試不執行它們：

```powershell
dotnet test --project 1.7/Source/FasterGameLoading.Tests/FasterGameLoading.Tests.csproj -c Release --filter FullyQualifiedName~TypeLookupBenchmarks --output Detailed
```

這兩個基準不包含首次組件列舉、Harmony 包裝或完整遊戲啟動，不能直接換算成遊戲加速百分比。

正式比較啟動速度時，到主選單的秒數讀取 Loading Progress 隔離 session 設定檔的 `loadingTimes` 最後一筆；以 `seed_config` 開啟 `trackStartupLoadingImpact` 可拆分模組與階段。主選單後的延後音效，需另開 FGL `verboseLogging`，讀 Player.log 的 `Startup timing after the main menu appeared`。移動工作到主選單後會讓前段數字變短，不能單憑它宣稱總啟動變快。

DevTools 遷移本身不會進入玩家版執行路徑。遊戲內驗證另外找出兩項 1.7 紋理問題：背景 PNG 編碼的列序，以及非 UI 快取紋理經 `LoadImage` 後遺失 mipmap。前者在手動重建快取時多做一次輸出像素列複製，不影響一般啟動；後者只在非 UI 縮小紋理快取命中時，額外配置一張貼圖並複製像素來建立 mipmap，可能增加這類啟動的 CPU 與記憶體用量。未做相同模組清單的前後計時，因此沒有量化的秒數差異。

## 啟動實驗與細部診斷旗標

開發附屬模組提供命令列旗標，用於對照不同啟動策略與收集細部成本：

### 命令列旗標說明

1. `-fglStartupDiagnostics`：
   啟用昂貴診斷探針（XML 逐檔量測、型別查詢形態統計、延遲管線 MoveNext 包裝、音效與提早載入監控）。預設關閉；關閉時透過 Harmony `Prepare()` 略過所有昂貴掛點，不對基準測量造成額外負擔。

2. `-fglStartupExperiment=baseline|xml-sequential|no-sound-prewarm`：
   - `baseline`（預設）：FGL 既有多執行緒載入與 Core 音效背景預熱。
   - `xml-sequential`：僅在 FGL 原本會平行解析時，取代其解析為循序執行。沿用原版資料夾覆蓋順序（`XmlFilesInVanillaOrder`）、相對路徑字典、單檔例外容錯、整體失敗 fallback 原版、Hyperdrive Defs 讓出與受保護模組閘門。不改動遊戲原版行為。
   - `no-sound-prewarm`：僅以 Harmony 跳過 `CoreSoundPrewarmer.Start`，不關閉整體的 `EnableMultiThreading`（貼圖預載與平行 XML 仍照常運作）。

3. `-fglStartupOnly`：
   純啟動觀測模式。略過 DevTools 測試就緒等待與 `TypeEnumerationCost.Measure()` 的額外反射列舉；精確量測共同起點 timestamp（`ObservationStartTimestamp`，以 `Stopwatch.Frequency` 計量）至首個主選單完成幀與全部延遲工作完成。無論細部診斷是否開啟，皆維持相同的單調時鐘主選單幀間隔觀察（測量 `Root.Update` 總間隔，包含整幀；持續至主選單出現且全部延遲完成後至少 1.0 秒且至少 2 幀），產出樣本數、起訖、p95 與 max 幀時間。隨後於日誌輸出一行可機器解析的 `[FGL StartupExperiment] { ... }` JSON（基於 `System.Text.Json`），並自動呼叫 `Application.Quit(0)`。此模式不使用 `--test --exit`，且與 `TestRunDriver` 互斥。

4. `-fglAutoLanguageReload`：
   自動語言重載驗證模式。**前置條件**：啟動參數必須包含 `-savedatafolder` 確保在隔離資料目錄執行；若缺少此旗標會立即以退出碼 1 退出，保護全域 `Prefs.xml`。禁止與 DevTools CLI 過濾參數（如 `--where`, `--test-list`, `--test-plan`）混用。
   第一輪 DevTools 測試完成後，排程延後至 `Root.Update`、確認 `TestRunner.Active == false` 且舊 runner 完全結束後，才執行 `SelectLanguage("Japanese")` 重載 PlayData；等待第二輪延遲管線就緒後，自動執行同一批 DevTools 測試（禁止呼叫雙重 runner）。結果嚴格要求選定案例數 > 0、兩輪案例數一致、所有案例真正執行結束（`Status.Passed`），排斥 `Pending`、`NotRun`、`Canceled`、`Failed` 及 TestFixture Setup/Teardown 層級失敗。全部完成後還原玩家原始語言設定並儲存，最後依兩輪測試結果以退出碼 0 或 1 退出。

### 測量限制與關鍵邊界

- **音效快取命中認定**：依據最小正確性原則，不作推測性反查 StubResourceCache。由於 StubResourceCache 僅能反映 Lazy 委託是否建立，無法可靠證明是預熱或主緒建立，亦無法比對內部快取鍵，因此明確回報 `cacheHitMeasured: false`。`ContentFinder<AudioClip>.Get` 整體耗時命名為 `mainThreadAudioLookupDuration`（含查找與載入）及計數 `mainThreadAudioLookupCalls`，不宣稱為純主緒等待或已驗證預熱命中。
- **XML 真實區間與排程成本**：
  - 區間交集：記錄各批次真實 `[Start, End]` 區間，計算合併聯集後與音效預熱真實區間的精確交集，排除批次間的空檔；若預熱至輸出時仍未結束，標記為 `incomplete: true`。交集僅反映壁鐘重疊區間，不直接等同 OS 執行緒調度競爭。
  - 跨執行緒 Context：採用 `AsyncLocal` 追蹤批次，確保 `Parallel.For` 各 worker 執行緒繼承同一 batch 統計，支援巢狀調用還原與例外路徑。
  - 檔案與解析計數：記錄 `expectedFiles`（掃描預期檔案數）、`attemptedFiles`（實際嘗試進入解析數）、`succeededFiles` 與 `failedFiles`。
  - 排程成本：多 worker 累計 parse ticks 在多核心重疊，不可與 wall duration 直減；將排程成本標記為未量測／估計。
- **型別查詢形態統計限制**：
  - 透過 `Priority.First` Prefix 於型別重寫（全名映射）前捕捉原始查詢形態（全名、短名、是否帶命名空間提示），Postfix 僅依據 Prefix state 統計。
  - `__runOriginal=false` 僅代表原方法被略過，不單獨歸因為 FGL 快取；計數命名為 `originalSkipped`、`resolvedByOriginal` 與 `notFound`。
- **程序時間與就緒 Clamp**：
  - 時間起點為開發模組首次初始化 timestamp（`ObservationStartTimestamp`），不宣稱為完整程序啟動時間。
  - 高精準度就緒於 `StartupTimeline.MarkAllReady` 當下即時捕捉；若在主選單出現前已完成，數值 clamp 為 0 並標註 `clampedPreMainMenu: true`。

### 執行範例

```powershell
# 1. Baseline 啟動時間觀察（關閉細部診斷，取得主選單、全部延遲工作完成時間、主選單幀間隔統計及 JSON）
RimWorld.exe -fglStartupOnly -fglStartupExperiment=baseline

# 2. 循序 XML 模式比較（啟用細部診斷）
RimWorld.exe -fglStartupOnly -fglStartupExperiment=xml-sequential -fglStartupDiagnostics

# 3. 停用音效預熱模式比較（啟用細部診斷）
RimWorld.exe -fglStartupOnly -fglStartupExperiment=no-sound-prewarm -fglStartupDiagnostics

# 4. 自動雙輪語言重載測試（CLI 自動化，必須搭配 -savedatafolder）
RimWorld.exe -savedatafolder="C:\Temp\RimWorldTestData" --pid Taranchuk.FasterGameLoading.InGameTests --test --exit -fglAutoLanguageReload
```
