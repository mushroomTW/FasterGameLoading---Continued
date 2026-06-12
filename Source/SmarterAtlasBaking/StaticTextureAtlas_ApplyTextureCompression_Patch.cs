using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 攔截 StaticTextureAtlas.ApplyTextureCompression，確保在啟用圖集快取時
    /// 紋理保持可讀取狀態，以便 SaveToCacheCoroutine 能正確匯出 RawTextureData。
    /// </summary>
    [HarmonyPatch(typeof(StaticTextureAtlas), "ApplyTextureCompression")]
    public static class StaticTextureAtlas_ApplyTextureCompression_Patch
    {
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
        /// 自訂 Apply 呼叫：當圖集快取啟用時，強制將 makeNoLongerReadable 設為 false，
        /// 確保後續可以透過 GetRawTextureData 匯出紋理資料。
        /// </summary>
        public static void CustomApply(Texture2D tex, bool updateMipmaps, bool makeNoLongerReadable)
        {
            // 當 AtlasCaching 啟用時，finalMakeNoLongerReadable 設為 false，即表示「保持可讀取」（makeNoLongerReadable = false）
            bool finalMakeNoLongerReadable = FasterGameLoadingSettings.AtlasCaching ? false : makeNoLongerReadable;
            tex.Apply(updateMipmaps, finalMakeNoLongerReadable);
        }
    }
}
