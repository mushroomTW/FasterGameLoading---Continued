# 遊戲內整合測試（RimTest Redux）

驗證 FGL 在真實遊戲載入完成後的狀態，補足 `FasterGameLoading.Tests`（headless NUnit，Unity 大量被 stub）測不到的部分：
Harmony patch 實際套用結果、延遲圖形／圖示／音效是否全部完成、靜態圖集烘焙結果、提早載入的時序、
多執行緒 XML 預載入與四種反射快取（GetTypeInAnyAssemblyInt、AllTypes、TypeByName、AllLeafSubclasses）和原版結果的一致性、
翻譯注入、降質工具實際縮放出的尺寸與降質快取的載入結果、mod 內容是否被重複載入、切換語言後的完整重載、相容性分支，以及進入地圖後的圖形與音效。

## 需求

- 已訂閱 [RimTest Redux](https://steamcommunity.com/sharedfiles/filedetails/?id=3762405308)、ilyvion's Laboratory、Harmony
- rimworld-mod-mcp（自動啟動隔離的遊戲 session 並讀回結果）

## 執行

1. 建置（參考 `Assemblies/FasterGameLoading.dll`，不會重建 FGL；要測修改後的 FGL 請先自行建置它）：
   `dotnet build Source/FasterGameLoading.InGameTests/FasterGameLoading.InGameTests.csproj -c Release`
2. `run_test_cycle`：`path` 指向 `Source/FasterGameLoading.InGameTests/Mod`，
   `companion_mods` 至少包含 `brrainz.harmony`、`ilyvion.laboratory`、`ilyvion.rimtestredux`、`Taranchuk.FasterGameLoading`。
   完整覆蓋需要跑以下六組（相容性與地圖相關測試在條件不符時直接略過，不會失敗）：

   | 組別 | `seed_config` | `quicktest` | 額外 `companion_mods` | 涵蓋 |
   |---|---|---|---|---|
   | 預設 | 無 | `false` | 無 | 預設設定＋語言重載 |
   | 全開 | `Profiles/AllOn/` | `false` | 無 | 延遲圖形、自適應圖集、較快的圖集壓縮＋語言重載 |
   | 地圖 | 無 | `true` | 無 | World.FinalizeInit 音效路徑、地圖物件圖形 |
   | 相容（HAR 等） | `Profiles/AllOn/` | `false` | `UnlimitedHugs.HugsLib`、`erdelf.HumanoidAlienRaces`、`Ancot.AncotLibrary`、`automatic.bionicicons` | HugsLib 重新導向、排除名單、圖集保護 |
   | 相容（GS+） | 無 | `false` | `Telefonmast.GraphicsSettings` | 降質快取對 Graphics Settings+ 讓開 |
   | 相容（Loading Progress） | 無 | `false` | `ilyvion.LoadingProgress` | Loading Progress 以 packageId 找到 FGL，不再自己重載每個 mod 的內容（`NoModContentWasLoadedTwice`） |

3. 結果寫在遊戲 log 的 `[RimTest Redux] TESTING START … TESTING END` 之間；失敗項目以 Error 輸出，`list_test_diagnostics` 可直接讀到。
   主選單組別會有兩段結果：第 1 輪（初次載入）與第 2 輪（切換成日文重載後），log 以 `[FGL InGameTests] Round N` 標示；
   全部結束時輸出 `[FGL InGameTests] All rounds finished.`。
   `NoModContentWasLoadedTwice` 失敗時先看列出的路徑：mod 自帶同名不同副檔名的檔案（如 `Foo.png` 與 `Foo.jpg`）也會觸發原版的重複警告，與 FGL 無關。

## 設計重點

- RimTest Redux 內建的「啟動時執行」會在 FGL 延遲管線跑完前觸發，因此由 `TestRunDriver` 改為等 `DelayedActions.PerformActions` 結束後才執行；quicktest 時另外等地圖進入遊戲。
- 語言重載：第 1 輪後切換到 FGL 沒提供翻譯的日文，重載會重跑 `CallAll` 與整條延遲管線（Mod 建構子不重跑），第 2 輪重跑所有測試，另加 `LanguageReloadTests`；遊戲中無法切換語言，quicktest 只跑一輪。
- 反射與 XML 的一致性測試以 Harmony reverse patch 取得未改寫的原版方法當對照組。
- 降質快取測試不需事先執行降質工具：自行產生原始貼圖與快取 PNG、登記到快取對照表後透過原版 `LoadTexture` 載入。
  長寬比測試則以非正方形原圖直接呼叫降質工具的 `ResizeTexture` 產生快取，再同樣透過 `LoadTexture` 載入。
- 本 mod 刻意排在 FGL **之前**載入：`ContentLoadProbe` 必須在 FGL 建構子啟動提早載入前就位；
  也藉此把被 `seed_config` 改名成本 mod 資料夾的 FGL 設定檔複製回 FGL 的檔名。
- DLL 輸出到 `Mod/Assemblies/`，不進 FGL 本體；`Source/` 已被 `_PublisherPlus.xml` 排除，不會上傳工作坊。
