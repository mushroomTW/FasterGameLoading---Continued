using System;
using System.Linq;
using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 與 Hyperdrive（vopaga.hyperdrive）並存：
    /// - Hyperdrive 在自己的建構子裡檢查 LoadedModManager.LoadModXML 是否已有其他 mod 的 prefix，有就放棄跨 mod 平行解析。
    ///   原版以未排序的 PLINQ 列舉 Mod 子類別，建構子順序與載入順序無關，FGL 可能先執行；
    ///   因此 Hyperdrive 尚未建構時，FGL 把自己的 LoadModXML prefix 延到 Hyperdrive 建構子結束後才套用。
    /// - Hyperdrive 已跨 mod 平行呼叫 LoadDefs，FGL 若再平行解析單一 mod 的 Defs/ 會形成兩層平行互搶核心，故讓出 Defs/。
    /// </summary>
    internal static class HyperdriveCompat
    {
        internal const string PackageId = "vopaga.hyperdrive";
        private const string ModTypeName = "RimWorldHyperdrive.HyperdriveMod";

        /// <summary>直接修改遊戲 DLL 的 Hyperdrive 版本會把輔助類別併入 Assembly-CSharp。</summary>
        private const string EnginePatchTypeName = "Verse.StartupOptimizer.OptimizedModManager";

        /// <summary>Hyperdrive 是否負責跨 mod 平行解析 Defs/ XML；由 Mod 建構子設定一次。</summary>
        internal static bool ParallelizesModDefs { get; set; }

        /// <summary>執行中的 Hyperdrive 的 Mod 類別；未啟用時回傳 null。</summary>
        internal static Type FindModType()
        {
            foreach (var mod in LoadedModManager.RunningMods)
            {
                if (!string.Equals(mod?.PackageIdPlayerFacing, PackageId, StringComparison.OrdinalIgnoreCase)) continue;

                var assemblies = mod.assemblies?.loadedAssemblies;
                if (assemblies == null) return null;
                foreach (var assembly in assemblies)
                {
                    var type = assembly.GetType(ModTypeName, throwOnError: false);
                    if (type != null) return type;
                }
            }
            return null;
        }

        /// <summary>依偵測結果設定 <see cref="ParallelizesModDefs"/>；<paramref name="hyperdriveModType"/> 為 <see cref="FindModType"/> 的結果。</summary>
        internal static void Detect(Type hyperdriveModType)
        {
            ParallelizesModDefs = hyperdriveModType != null
                || typeof(LoadedModManager).Assembly.GetType(EnginePatchTypeName, throwOnError: false) != null;
            if (ParallelizesModDefs)
            {
                FGLLog.Message("Hyperdrive detected: leaving parallel Defs XML loading to Hyperdrive.");
            }
        }

        /// <summary>
        /// 套用 FGL 的 LoadModXML prefix。Hyperdrive 已啟用但建構子尚未執行時，改在它的建構子之後才套用，
        /// 讓它檢查時看不到 FGL 的 prefix。
        /// </summary>
        internal static void PatchLoadModXML(Harmony harmony, Type hyperdriveModType)
        {
            // CreateModClasses 在建構子返回後才把實例放進 runningModClasses：查得到代表 Hyperdrive 已建構並檢查過。
            if (hyperdriveModType != null && LoadedModManager.GetMod(hyperdriveModType) == null)
            {
                var constructor = AccessTools.Constructor(hyperdriveModType, new[] { typeof(ModContentPack) });
                if (constructor != null)
                {
                    deferredHarmony = harmony;
                    harmony.Patch(constructor, postfix: new HarmonyMethod(typeof(HyperdriveCompat), nameof(AfterHyperdriveConstructed)));
                    return;
                }
                FGLLog.Warning("Hyperdrive constructor not found; Hyperdrive may disable its parallel XML loading.");
            }
            PatchLoadModXMLNow(harmony);
        }

        private static Harmony deferredHarmony;

        private static void AfterHyperdriveConstructed()
        {
            PatchLoadModXMLNow(deferredHarmony);
        }

        private static void PatchLoadModXMLNow(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.LoadModXML));
            // 已套用過就不再加第二份（例如 Hyperdrive 建構子被呼叫兩次）。
            if (Harmony.GetPatchInfo(target)?.Prefixes.Any(p => string.Equals(p.owner, harmony.Id, StringComparison.Ordinal)) is true) return;

            harmony.Patch(target, prefix: new HarmonyMethod(typeof(LoadedModManager_LoadModXML_Patch), nameof(LoadedModManager_LoadModXML_Patch.Prefix))
            {
                // 最先執行：其他 mod（例如 Hyperdrive 或取代 XML 載入的快取類 mod）的 prefix 回傳 false 時，排在後面的 prefix 會被略過。
                priority = Priority.First,
            });
        }
    }
}
