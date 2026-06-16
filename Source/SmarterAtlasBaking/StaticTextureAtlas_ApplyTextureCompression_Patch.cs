using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 StaticTextureAtlas.ApplyTextureCompression，確保在「確定要烘焙且要寫快取」的
    /// 窗口內紋理保持可讀取狀態，以便 SaveToCacheCoroutine 能正確匯出 RawTextureData。
    ///
    /// 舊做法：只要 AtlasCaching 為 true 就讓所有紋理保持可讀，即使快取命中（不需寫快取）
    /// 也會在 CPU 端常駐副本，浪費記憶體。
    ///
    /// 新做法：由呼叫端（GlobalTextureAtlasManager_BakeStaticAtlases_Patch）在
    /// 「快取未命中且確定要烘焙」時將 KeepTexturesReadable 設為 true，
    /// 寫入協程（SaveToCacheCoroutine）完成或失敗後設回 false。
    /// 快取命中路徑不設旗標，紋理壓縮後即可正常釋放 CPU 副本。
    /// </summary>
    [HarmonyPatch(typeof(StaticTextureAtlas), "ApplyTextureCompression")]
    public static class StaticTextureAtlas_ApplyTextureCompression_Patch
    {
        /// <summary>
        /// 當此旗標為 true 時，CustomApply 強制將 makeNoLongerReadable 設為 false，
        /// 確保後續可以透過 GetRawTextureData 匯出紋理資料。
        /// 應只在「確定要烘焙且要寫快取」的窗口設為 true，寫完後立即設回 false。
        /// 注意：SaveToCacheCoroutine 是協程，旗標須涵蓋到整個協程執行期間。
        /// </summary>
        public static bool KeepTexturesReadable = false;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var applyMethod = AccessTools.Method(typeof(Texture2D), "Apply", new[] { typeof(bool), typeof(bool) });
            var customApplyMethod = AccessTools.Method(typeof(StaticTextureAtlas_ApplyTextureCompression_Patch), nameof(CustomApply));

            int substitutions = 0;
            foreach (var inst in instructions)
            {
                if (inst.Calls(applyMethod))
                {
                    substitutions++;
                    yield return new CodeInstruction(OpCodes.Call, customApplyMethod);
                }
                else
                {
                    yield return inst;
                }
            }

            // 若未找到任何 Apply(bool,bool) 呼叫，代表遊戲更新後方法簽章已改變，
            // 圖集快取在儲存後可能無法讀取紋理資料，需立即警告開發者。
            if (substitutions == 0)
            {
                FGLLog.Warning("StaticTextureAtlas_ApplyTextureCompression_Patch: Transpiler found no Apply(bool,bool) call to replace. The patch may be broken due to a game update — atlas cache write-back may fail.");
            }
        }

        /// <summary>
        /// 自訂 Apply 呼叫：僅在 KeepTexturesReadable 旗標為 true 時（烘焙+寫快取窗口）
        /// 強制將 makeNoLongerReadable 設為 false，確保後續可透過 GetRawTextureData 匯出。
        /// 快取命中路徑不設旗標，紋理壓縮後正常釋放 CPU 副本，避免記憶體浪費。
        /// </summary>
        public static void CustomApply(Texture2D tex, bool updateMipmaps, bool makeNoLongerReadable)
        {
            bool finalMakeNoLongerReadable = KeepTexturesReadable ? false : makeNoLongerReadable;
            tex.Apply(updateMipmaps, finalMakeNoLongerReadable);
        }
    }
}
