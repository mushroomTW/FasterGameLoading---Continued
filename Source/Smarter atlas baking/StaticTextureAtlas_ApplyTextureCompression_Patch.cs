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

            foreach (var inst in instructions)
            {
                if (inst.Calls(applyMethod))
                {
                    yield return new CodeInstruction(OpCodes.Call, customApplyMethod);
                }
                else
                {
                    yield return inst;
                }
            }
        }

        /// <summary>
        /// 自訂 Apply 呼叫：當圖集快取啟用時，強制將 makeNoLongerReadable 設為 false，
        /// 確保後續可以透過 GetRawTextureData 匯出紋理資料。
        /// </summary>
        public static void CustomApply(Texture2D tex, bool updateMipmaps, bool makeNoLongerReadable)
        {
            bool finalReadable = FasterGameLoadingSettings.AtlasCaching ? false : makeNoLongerReadable;
            tex.Apply(updateMipmaps, finalReadable);
        }
    }
}
