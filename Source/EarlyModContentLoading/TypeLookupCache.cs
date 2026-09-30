using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 「在目前組件集合中以名稱找型別」的快取政策：唯一的組件搜尋清單、跨 session 的完整名稱對照與其組件指紋，
    /// 以及所有名稱查詢快取的失效時機。
    /// 各查詢補丁仍各自保存結果（GenTypes 與 AccessTools.TypeByName 的解析規則不同，不可共用），
    /// 但一律由 <see cref="Invalidate"/> 一起清空：語言切換時，以及原版 GenTypes.ClearCache 時。
    /// AccessTools.AllTypes 的快取不在此列：它只依組件數量失效，語言切換不會改變組件集合。
    /// </summary>
    internal static class TypeLookupCache
    {
        /// <summary>上一次 session 查詢過的名稱 → 完整型別名稱；讀檔時可能整份捨棄（見 <see cref="RestoreAfterLoad"/>）。</summary>
        internal static ConcurrentDictionary<string, string> FullNamesFromLastSession { get; set; } = new(StringComparer.Ordinal);

        /// <summary>
        /// 產生 <see cref="FullNamesFromLastSession"/> 那個 session 的組件指紋。
        /// 與本次指紋不符（mod 更新、遊戲更新）時，型別對照可能已指向不存在的類別，必須整份捨棄。
        /// </summary>
        internal static string PersistedFingerprint { get; set; }

        /// <summary>本行程是否已結算過一次對照；之後的輪次（語言重載）只併入新查到的名稱。</summary>
        internal static bool SessionMappingSettled { get; set; }

        static TypeLookupCache()
        {
            SessionLifecycle.On(LifecyclePhase.LanguageReloading, Invalidate);
            SessionLifecycle.On(LifecyclePhase.StartupCompleted, SettleSessionMapping);
        }

        /// <summary>
        /// 每輪載入結束時把本輪查到的對照結算為下次啟動要用的對照。
        /// 第一輪整份取代，丟掉上次 session 留下、這次沒用到的名稱；
        /// 語言重載後的輪次只併入：重載不會重跑 Mod 建構子與提早載入，本輪查到的名稱只是第一輪的子集，整份取代會讓對照縮水。
        /// </summary>
        internal static void SettleSessionMapping()
        {
            var thisRound = GenTypes_GetTypeInAnyAssemblyInt_Patch.loadedTypesThisSession;
            if (!SessionMappingSettled)
            {
                FullNamesFromLastSession = new ConcurrentDictionary<string, string>(thisRound, StringComparer.Ordinal);
                SessionMappingSettled = true;
                return;
            }
            foreach (var kvp in thisRound)
            {
                FullNamesFromLastSession[kvp.Key] = kvp.Value;
            }
        }

        /// <summary>清空所有名稱查詢快取（不含跨 session 對照）。</summary>
        internal static void Invalidate()
        {
            GenTypes_GetTypeInAnyAssemblyInt_Patch.ClearCache();
            AccessTools_TypeByName_Patch.cachedResults.Clear();
            GenTypes_AllLeafSubclasses_Patch.ClearCache();
        }

        /// <summary>
        /// 原版 GenTypes 以名稱查型別時搜尋的組件，依搜尋順序排列：
        /// 遊戲本體（Assembly-CSharp）在前，接著是各執行中 mod 依載入順序的組件。
        /// 預熱與組件指紋都以這份清單為準，兩者不會分歧。
        /// </summary>
        internal static List<Assembly> SearchAssemblies()
        {
            var assemblies = new List<Assembly> { typeof(GenTypes).Assembly };
            foreach (var mod in LoadedModManager.RunningMods)
            {
                var loaded = mod?.assemblies?.loadedAssemblies;
                if (loaded != null)
                {
                    assemblies.AddRange(loaded);
                }
            }
            return assemblies;
        }

        /// <summary>
        /// 以 <see cref="SearchAssemblies"/> 計算目前的組件指紋。
        /// 這些組件在 mod 類別建構前就已固定，讀檔與存檔兩端算出的集合一致；
        /// 無法計算時回傳 null，讓比對失敗而捨棄快取（fail-closed）。
        /// </summary>
        internal static string ComputeCurrentFingerprint()
        {
            try
            {
                return ComputeAssemblyFingerprint(SearchAssemblies());
            }
            catch (Exception ex)
            {
                FGLLog.Warning("Failed to compute assembly fingerprint for the type cache:", ex);
                return null;
            }
        }

        /// <summary>
        /// 純函式：以與載入順序無關的方式（依字串排序）把組件的 FullName 與 MVID 折成 MD5 十六進位字串。
        /// MVID 在每次重新編譯時都會改變，因此能偵測到版本號未變的 mod 更新。
        /// </summary>
        internal static string ComputeAssemblyFingerprint(IEnumerable<Assembly> assemblies)
        {
            var entries = assemblies
                .Where(static a => a != null && !a.IsDynamic)
                .Select(static a => a.FullName + "|" + a.ManifestModule.ModuleVersionId.ToString("N"))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static s => s, StringComparer.Ordinal);

            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(string.Join('\n', entries)));
                return string.Concat(hash.Select(static b => b.ToString("x2")));
            }
        }

        /// <summary>序列化跨 session 對照與其組件指紋；由 SessionCache.ExposeData 在每一輪 Scribe 呼叫。</summary>
        internal static void ExposeData()
        {
            Dictionary<string, string> tempTypes = null;
            if (Scribe.mode is LoadSaveMode.Saving)
            {
                tempTypes = new Dictionary<string, string>(FullNamesFromLastSession, StringComparer.Ordinal);
            }
            Scribe_Collections.Look(ref tempTypes, FGLConsts.LoadedTypesKey, LookMode.Value, LookMode.Value);
            // 讀檔分成 LoadingVars 與 PostLoadInit 兩輪，各自重新呼叫本方法：
            // 值型別字典只在 LoadingVars 那輪被填入 tempTypes，PostLoadInit 那輪的區域變數必為 null。
            // 因此必須在同一輪就寫回，否則上次存下的對照永遠不會被讀回來。
            if (Scribe.mode is LoadSaveMode.LoadingVars && tempTypes != null)
            {
                FullNamesFromLastSession = new ConcurrentDictionary<string, string>(tempTypes, StringComparer.Ordinal);
            }

            var fingerprint = Scribe.mode is LoadSaveMode.Saving
                ? ComputeCurrentFingerprint()
                : PersistedFingerprint;
            Scribe_Values.Look(ref fingerprint, FGLConsts.TypeCacheAssemblyFingerprintKey);
            PersistedFingerprint = fingerprint;
        }

        /// <summary>
        /// 讀檔完成後決定跨 session 對照是否還能用：mod 組合變了、組件內容變了、或本次指紋算不出來時整份捨棄。
        /// </summary>
        internal static void RestoreAfterLoad(bool modSetChanged)
        {
            FullNamesFromLastSession ??= new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            if (modSetChanged
                || ComputeCurrentFingerprint() is not { } currentFingerprint
                || !string.Equals(PersistedFingerprint, currentFingerprint, StringComparison.Ordinal))
            {
                // 兩端都是 null 時不可視為一致，故算不出本次指紋也一律捨棄。
                FullNamesFromLastSession.Clear();
            }
        }
    }
}
