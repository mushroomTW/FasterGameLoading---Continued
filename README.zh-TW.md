# Faster Game Loading - Continued

[![RimWorld 1.6 | 1.7](https://img.shields.io/badge/RimWorld-1.6%20%7C%201.7-brightgreen.svg)](http://rimworldgame.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![Languages](https://img.shields.io/badge/languages-EN%20%7C%20Traditional%20Chinese%20%7C%20Simplified%20Chinese%20%7C%20RU-orange.svg)

[English](README.md) | 繁體中文

讓 RimWorld 在大型模組清單下更快進入主選單。本模組降低 XML 載入、反射、紋理與圖集處理的啟動成本，不改變遊戲玩法或存檔資料。

原始模組作者為 [Taranchuk](https://github.com/Taranchuk/FasterGameLoading)；本專案是持續維護的分支，加入相容性修正與效能改進。

## 版本

RimWorld 1.6 與 1.7 使用不同的引擎，因此各自有獨立的建置與說明文件：

| 遊戲版本 | 執行環境 | 說明文件 |
| --- | --- | --- |
| RimWorld 1.6 | Unity / Mono（.NET Framework 4.7.2） | [1.6 README](1.6/README.zh-TW.md) |
| RimWorld 1.7 | Waylith / .NET 10 | [1.7 README](1.7/README.zh-TW.md) |

各版 README 涵蓋該版本的啟動流程、功能、實測、相容性、建議設定與建置方式。RimWorld 會自動載入對應版本的資料夾，不需要自己選擇。

## 安裝

> [!IMPORTANT]
> 需要 RimWorld 1.6 或 1.7 以及 [Harmony](https://github.com/pardeike/HarmonyRimWorld/releases/latest)。請把本模組放在 Harmony 之後載入。

- **Steam 工作坊**：在[工作坊頁面](https://steamcommunity.com/sharedfiles/filedetails/?id=3652938473)訂閱。
- **手動安裝**：下載或 clone 本儲存庫到 `RimWorld/Mods/` 資料夾，再於遊戲內的模組清單啟用 **Faster Game Loading - Continued**。

所有選項都在 `選項 → 模組設定 → 更快的遊戲載入 - Continued`。大多數玩家保持預設即可；請參考對應遊戲版本 README 中的建議設定。

## 專案結構

```text
FasterGameLoading/
├── About/                         # 模組中繼資料、預覽圖與工作坊 ID
├── 1.6/                           # RimWorld 1.6（.NET Framework 4.7.2）
│   ├── Assemblies/                # 編譯後的 DLL
│   ├── LanguageData/              # 翻譯 XML 檔（EN、zh-TW、zh-CN、RU）
│   ├── Source/                    # 1.6 原始碼
│   ├── README.md                  # 1.6 說明文件（英文）
│   └── README.zh-TW.md            # 1.6 說明文件（繁體中文）
├── 1.7/                           # RimWorld 1.7（.NET 10）
│   ├── Assemblies/                # 編譯後的 DLL
│   ├── LanguageData/              # 翻譯 XML 檔（EN、zh-TW、zh-CN、RU）
│   ├── Source/                    # 1.7 原始碼（獨立維護）
│   ├── README.md                  # 1.7 說明文件（英文）
│   └── README.zh-TW.md            # 1.7 說明文件（繁體中文）
├── SteamDescriptions/             # Steam 工作坊說明的原始檔
├── LICENSE
├── README.md
└── README.zh-TW.md
```

RimWorld 依執行中的版本選擇 `1.6/` 或 `1.7/`，所以各版建置都把 DLL 輸出到其 `Source/` 旁的 `Assemblies/` 資料夾。建置與測試指令請見各版 README。

各版本只載入自己的 `LanguageData/`；1.7 的翻譯不包含僅適用於 1.6 的延遲圖形和圖示載入、自適應圖集烘焙與較快的圖集壓縮。

## 致謝與授權

原始模組作者為 [Taranchuk](https://github.com/Taranchuk/FasterGameLoading)。本分支由 [mushroomTW](https://github.com/mushroomTW/FasterGameLoading---Continued) 維護，加入相容性修正與效能改進。以 [MIT 授權](LICENSE)釋出。
