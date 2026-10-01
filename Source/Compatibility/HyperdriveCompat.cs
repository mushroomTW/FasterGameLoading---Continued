using System;
using System.Linq;
using System.Reflection;
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

        /// <summary>
        /// 本次 LoadModXML 的 Defs/ 是否由 Hyperdrive 跨 mod 平行解析；
        /// 由 <see cref="OnLoadModXMLStarting"/> 在 LoadModXML 開始時設定，LoadModXML 結束後歸零。
        /// </summary>
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

        /// <summary>
        /// 由 FGL 的 LoadModXML prefix 在 Hyperdrive 的 prefix 之前呼叫。Hyperdrive 放棄時不會掛上自己的 prefix，
        /// 因此以「LoadModXML 上有它的 prefix」判斷它這次確實會跨 mod 平行解析，而不是只看它有沒有啟用。
        /// </summary>
        internal static void OnLoadModXMLStarting()
        {
            ParallelizesModDefs = typeof(LoadedModManager).Assembly.GetType(EnginePatchTypeName, throwOnError: false) != null
                || Harmony.GetPatchInfo(LoadModXMLMethod)?.Prefixes.Any(static p => string.Equals(p.owner, PackageId, StringComparison.Ordinal)) is true;
            if (ParallelizesModDefs)
            {
                FGLLog.Message("Hyperdrive is parallelizing mod XML loading: leaving parallel Defs XML loading to Hyperdrive.");
            }
        }

        private static void AfterLoadModXML()
        {
            ParallelizesModDefs = false;
        }

        private static MethodInfo LoadModXMLMethod =>
            AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.LoadModXML));

        /// <summary>
        /// 套用 FGL 的 LoadModXML prefix。Hyperdrive 已啟用但建構子尚未執行時，改在它的建構子之後才套用，
        /// 讓它檢查時看不到 FGL 的 prefix。以 finalizer 掛在建構子上：建構子拋例外時仍會套用，提早載入的閘門不會失效。
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
                    harmony.Patch(constructor, finalizer: new HarmonyMethod(typeof(HyperdriveCompat), nameof(AfterHyperdriveConstructed)));
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
            var target = LoadModXMLMethod;
            // 已套用過就不再加第二份（例如 Hyperdrive 建構子被呼叫兩次）。
            if (Harmony.GetPatchInfo(target)?.Prefixes.Any(p => string.Equals(p.owner, harmony.Id, StringComparison.Ordinal)) is true) return;

            harmony.Patch(target, prefix: new HarmonyMethod(typeof(LoadedModManager_LoadModXML_Patch), nameof(LoadedModManager_LoadModXML_Patch.Prefix))
            {
                // 最先執行：其他 mod（例如 Hyperdrive 或取代 XML 載入的快取類 mod）的 prefix 回傳 false 時，排在後面的 prefix 會被略過。
                priority = Priority.First,
            },
            // Hyperdrive 只檢查 prefix 與 transpiler；finalizer 在 prefix 略過原方法或拋例外時也會執行，讓出 Defs/ 只限 LoadModXML 期間。
            finalizer: new HarmonyMethod(typeof(HyperdriveCompat), nameof(AfterLoadModXML)));
        }
    }
}
