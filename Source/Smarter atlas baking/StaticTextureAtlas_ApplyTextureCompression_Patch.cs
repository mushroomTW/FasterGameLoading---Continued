using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
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

        public static void CustomApply(Texture2D tex, bool updateMipmaps, bool makeNoLongerReadable)
        {
            // If caching is enabled, we need the texture to remain readable so we can dump RawTextureData.
            bool finalReadable = FasterGameLoadingSettings.atlasCaching ? false : makeNoLongerReadable;
            tex.Apply(updateMipmaps, finalReadable);
        }
    }
}
