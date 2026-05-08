# Faster Game Loading - 智能圖集烘焙設計

## 概述
本文檔描述了Faster Game Loading模組中圖集烘焙演算法的增強功能。目標是透過結合歷史性能數據來改進自適應紋理圖集烘焙，減少每次遊戲會話開始時的試誤階段。

## 現有機制
`DelayedActions.cs` 中現有的自適應圖集烘焙演算法如下：
- 初始估算烘焙速度為 2,000,000 像素/秒
- 每批次烘焙後，測量實際烘焙速度（像素/秒）
- 使用線性插值(Lerp)，插值因子為0.2來更新估算速度：
  `measuredBakeSpeed = Lerp(oldEstimate, latestMeasurement, 0.2)`
- 根據以下公式計算下一批次大小：`newSliceSize = measuredBakeSpeed * TARGET_BAKE_TIME_SECONDS * PACK_DENSITY`
- 調整到最近的2的冪次方，範圍在 [1MB, 16MB] 像素之間

## 問題陳述
雖然當前自適應演算法在會話期間表現良好，但它總是以相同的保守估算值（2M像素/秒）開始，無論硬體的實際能力如何。這意味著：
- 高效能系統可能開始過慢，浪費潛在效能
- 低效能系統可能開始過於激進，導致卡頓
- 每次會話都需要一個「熱身」期，讓演算法收斂到最佳值

## 提出的解決方案：加權歷史平均
實施一個預測系統，利用歷史會話數據為自適應演算法提供更好的初始估算。

### 設計細節

#### 1. 資料存儲
在 `FasterGameLoadingSettings.cs` 中添加：
```csharp
public static List<float> historicalBakeSpeeds = new List<float>();
private const int HISTORY_SIZE = 5;  // 保留最近5次會話
private static readonly float[] WEIGHTS = {0.4f, 0.3f, 0.2f, 0.1f};  // 最近4次會話的權重
```

#### 2. 會話初始化
在 `DelayedActions.PerformAdaptiveStaticAtlasBake()` 中替換初始值：
```csharp
// 舊程式碼: float measuredBakeSpeed_PixelsPerSecond = 2_000_000f;

// 新程式碼:
float measuredBakeSpeed_PixelsPerSecond;
if (FasterGameLoadingSettings.historicalBakeSpeeds.Count == 0)
{
    // 首次運行 - 使用現有的保守估算值
    measuredBakeSpeed_PixelsPerSecond = 2_000_000f;
}
else
{
    // 計算歷史的加權平均作為初始值
    float weightedSum = 0f;
    float weightSum = 0f;
    int count = Math.Min(FasterGameLoadingSettings.historicalBakeSpeeds.Count, WEIGHTS.Length);
    
    for (int i = 0; i < count; i++)
    {
        weightedSum += FasterGameLoadingSettings.historicalBakeSpeeds[i] * WEIGHTS[i];
        weightSum += WEIGHTS[i];
    }
    
    measuredBakeSpeed_PixelsPerSecond = weightedSum / weightSum;
}
```

#### 3. 會話完成
在 `PerformAdaptiveStaticAtlasBake()` 結束時，清除佇列之前添加：
```csharp
// 將當前會話的最終速度加入歷史記錄
FasterGameLoadingSettings.historicalBakeSpeeds.Insert(0, measuredBakeSpeed_PixelsPerSecond);

// 維持歷史記錄大小限制
if (FasterGameLoadingSettings.historicalBakeSpeeds.Count > HISTORY_SIZE)
{
    FasterGameLoadingSettings.historicalBakeSpeeds.RemoveAt(HISTORY_SIZE);
}
```

#### 4. 資料持久化
`FasterGameLoadingSettings` 中現有的 `ExposeData()` 方法已經通過 Scribe 處理靜態欄位序列化，因此無需額外更改即可在會話間持久化。

### 權衡考量

#### 替代方案1：分層歷史參數
- **優點**：可以根據紋理數量/大小模式進行更好的預測
- **缺點**：複雜度顯著增加，需要存儲和匹配多維數據
- **結論**：此用例過度設計

#### 替代方案2：漸進式學習率
- **優點**：減少自適應因子的振盪
- **缺點**：無法解決核心問題——糟糕的初始估算
- **結論**：可能作為補充，但無法解決主要問題

#### 選擇的方案：加權歷史平均
- **優點**：
  - 簡單實現和理解
  - 從第二次會話開始即提供即時好處
  - 隨著時間自動適應硬體變化
  - 性能開銷極小
  - 使用現有的持久化機制
- **缺點**：
  - 首次會話仍使用保守估算
  - 假設不同會話之間的工作負荷相似（對於圖集烘焙通常成立）

### 實作檔案
- `Source\FasterGameLoadingSettings.cs` - 添加歷史存儲和權重
- `Source\DelayedActions.cs` - 修改自適應烘焙的初始化和完成

### 測試考量
1. 驗證首次會話仍然保持原始行為
2. 檢查歷史數據是否正確在會話間保存/載入
3. 驗證加權平均計算的正確性
4. 確保歷史記錄不會超過 HISTORY_SIZE
5. 確認演算法在會話內仍能像以前那樣進行適應

### 故障情境
- **歷史數據損壞**：系統回退到原始2M估算值（安全預設值）
- **極端硬體變化**：演算法在會話內仍能像以前那樣適應
- **模組列表顯著變化**：仍然有效，因為歷史數據對GPU性能仍然相關

## 成功標準
1. 第二次及後續會話顯示改進的初始烘焙效能
2. 第一次會話體驗不會退化
3. 系統在會話內繼續像以前那樣進行適應
4. 歷史數據正確地在遊戲啟動間持久化
5. 沒有增加載入時間或記憶體使用量

## 相關檔案
- `Source\FasterGameLoadingSettings.cs` - 設定和歷史存儲
- `Source\DelayedActions.cs` - 自適應烘焙演算法
- `Source\Misc\GlobalTextureAtlasManager_BakeStaticAtlases_Patch.cs` - 修補控制器