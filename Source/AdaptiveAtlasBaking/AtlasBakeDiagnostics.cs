using System;
using System.Linq;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 靜態圖集烘焙的純診斷工具。
    ///
    /// 動機：原版 <see cref="StaticTextureAtlas"/>.BuildMaskAtlas 以
    /// <c>Graphics.CopyTexture</c> 把各來源 mask 貼圖搬進 mask 圖集。當某張 mask 的
    /// 尺寸與其對應 main 貼圖不符（兩者佔同一 UV rect，尺寸必須一致），或 mask 為
    /// null / GPU 資料無效時，<c>CopyTexture</c> 會在原生層直接 Access Violation 硬崩潰
    /// ——這是 C# try/catch 接不到的，因此唯一能做的是「在烘焙前先把可疑貼圖印出來」，
    /// 方便鎖定肇事模組。
    ///
    /// 注意：不檢查 mask.format vs main.format。main 與 mask 進的是兩個各自獨立的圖集，
    /// 格式彼此無需相容（BC7 main + DXT1 mask 為原版常態），比較兩者只會產生誤報洗版。
    ///
    /// 本類別完全唯讀、且全程包在 try/catch 中，診斷自身絕不丟例外、不影響烘焙流程。
    /// 應在「即將呼叫原版 GlobalTextureAtlasManager.BakeStaticAtlases() 之前」呼叫。
    /// </summary>
    public static class AtlasBakeDiagnostics
    {
        /// <summary>
        /// 掃描目前的 buildQueue，逐一檢查每張帶 mask 的 main 貼圖，
        /// 將可能觸發原生 CopyTexture 崩潰的 mask 問題以 Warning 印出。
        /// </summary>
        /// <param name="context">呼叫情境字串（例如 "deferred fallback"），方便在 log 中辨識來源。</param>
        public static void LogPotentialMaskIssues(string context)
        {
            try
            {
                var buildQueue = GlobalTextureAtlasManager.buildQueue;
                if (buildQueue == null || buildQueue.Count == 0)
                {
                    return;
                }

                int groupCount = 0;
                int textureCount = 0;
                int maskCount = 0;
                int issueCount = 0;

                foreach (var kvp in buildQueue.ToList())
                {
                    var key = kvp.Key;
                    if (!key.hasMask)
                    {
                        // 沒有 mask 的群組不會進 BuildMaskAtlas，跳過。
                        groupCount++;
                        continue;
                    }

                    groupCount++;

                    foreach (Texture2D main in kvp.Value.Item1.ToList())
                    {
                        if (main == null)
                        {
                            continue;
                        }

                        textureCount++;

                        if (!GlobalTextureAtlasManager.buildQueueMasks.TryGetValue(main, out var mask)
                            || mask == null)
                        {
                            // hasMask 為 true 但找不到對應 mask：BuildMaskAtlas 內以 null 進 CopyTexture 是高風險。
                            issueCount++;
                            FGLLog.Warning(
                                $"[AtlasDiag/{context}] Group '{DescribeKey(key)}' main texture '{DescribeTexture(main)}' " +
                                $"declares hasMask but has no matching mask (null). BuildMaskAtlas may call CopyTexture on a null source.");
                            continue;
                        }

                        maskCount++;

                        // ── 主因檢查：mask 與 main 尺寸不符是 CopyTexture 區塊複製越界 / AV 的最常見原因 ──
                        // 註：main 與 mask 會被搬進兩個各自獨立的圖集（colorTexture / maskTexture），
                        // 兩者佔同一個 UV rect，故尺寸必須一致；但「格式」彼此無需相容——
                        // CopyTexture 只要求 mask 的格式與「目標 mask 圖集」相容，與 main 的格式無關。
                        // 因此不比較 mask.format vs main.format（BC7 main + DXT1 mask 是原版常態，並非崩潰條件）。
                        if (mask.width != main.width || mask.height != main.height)
                        {
                            issueCount++;
                            FGLLog.Warning(
                                $"[AtlasDiag/{context}] Size mismatch: main '{DescribeTexture(main)}' " +
                                $"vs mask '{DescribeTexture(mask)}'. CopyTexture requires matching source/destination blocks; " +
                                $"this discrepancy is very likely the cause of a native BuildMaskAtlas crash.");
                        }
                    }
                }

                FGLLog.Message(
                    $"[AtlasDiag/{context}] Scan complete: groups {groupCount}, main textures with mask {textureCount}, " +
                    $"successfully paired masks {maskCount}, suspicious items detected {issueCount}.");

                if (issueCount > 0)
                {
                    FGLLog.Warning(
                        $"[AtlasDiag/{context}] Detected {issueCount} suspicious mask(s); if a native crash follows in " +
                        $"BuildMaskAtlas / CopyTexture, it is very likely caused by the content mod owning one of the textures above.");
                }
            }
            catch (Exception ex)
            {
                // 診斷工具本身絕不可影響烘焙：吞掉任何例外，只留一條警告。
                FGLLog.Warning($"[AtlasDiag/{context}] Exception while scanning buildQueue (ignored, does not affect baking)", ex);
            }
        }

        private static string DescribeTexture(Texture2D tex)
        {
            if (tex == null)
            {
                return "<null>";
            }

            string name = string.IsNullOrEmpty(tex.name) ? "<no name>" : tex.name;
            return $"{name} [{tex.width}x{tex.height}, {tex.format}, mips={tex.mipmapCount}]";
        }

        private static string DescribeKey(TextureAtlasGroupKey key)
        {
            return $"{key}, hasMask={key.hasMask}";
        }
    }
}
