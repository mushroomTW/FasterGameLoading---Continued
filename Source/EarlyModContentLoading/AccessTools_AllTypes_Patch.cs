using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 快取 AccessTools.AllTypes() 的結果以加速後續查詢。
    /// Preload() 在背景執行緒中預先載入所有組件的型別列表，
    /// Prefix 攔截後直接回傳快取結果，避免重複掃描。
    /// </summary>
    [HarmonyPatch(typeof(AccessTools), "AllTypes")]
    public static class AccessTools_AllTypes_Patch
    {
        /// <summary>快取的全型別列表。使用 volatile + lock 確保執行緒安全。</summary>
        private static volatile List<Type> allTypesCached;
        private static readonly object typesLock = new();
        private static volatile int cachedAssembliesCount = 0;
        /// <summary>建立快取時所有動態組件的型別總數；同一動態組件新增型別不會改變組件數，需另外比對。</summary>
        private static volatile int cachedDynamicTypeCount = 0;

        /// <summary>
        /// 在背景執行緒中預先載入所有型別。
        /// 由 FasterGameLoadingMod 建構子呼叫。
        /// </summary>
        public static void Preload()
        {
            // 在主執行緒先取得 Assemblies 快照，防止列舉時集合發生 Race Condition。
            // GetAssemblies() 本身每次即回傳全新陣列快照，無需再包一層 ToArray。
            var assembliesSnapshot = AppDomain.CurrentDomain.GetAssemblies();
            int snapshotCount = assembliesSnapshot.Length;
            // 先計數再列舉：期間新增的動態型別若已列入清單，下次比對只會多重建一次，不會漏項。
            int dynamicTypeCount = CountDynamicTypes(assembliesSnapshot);

            if (!FasterGameLoadingSettings.EnableMultiThreading)
            {
                EnumerateTypesOnMainThread(assembliesSnapshot, snapshotCount, dynamicTypeCount);
                return;
            }

            // 多執行緒路徑：背景緒只做型別「列舉」（Assembly.GetTypes），
            // 不在背景緒讀取 type.FullName。
            //
            // 原因：type.FullName / 型別名稱解析會觸發 Mono 反射層的型別載入，而 Mono
            // （Unity 2022.3 MonoBleedingEdge）的型別/名稱解析並非完全執行緒安全。若背景緒
            // 在此大量解析型別名稱，剛好與主執行緒的型別解析（例如某些 Mod 透過
            // System.Xml.Serialization.XmlSerializer → Assembly.GetType 載入設定）並行，
            // 可能在 Mono 內部 class-init 狀態上競爭而導致原生崩潰（Assembly.GetType /
            // InternalGetType 的 Access Violation）。本檔案下方 WarmupTypeCache 的註解亦記載過
            // 型別名稱處理曾引發 MakeGenericType 崩潰，名稱解析確為此處最脆弱的環節。
            //
            // FullName 預熱由 GenTypes_GetTypeInAnyAssemblyInt_Patch.WarmupFullNames 在 Mod 建構子（載入事件緒）完成。
            EnumerateTypesInBackground(assembliesSnapshot, snapshotCount, dynamicTypeCount);
        }

        /// <summary>
        /// 關閉多執行緒時，在當前執行緒列舉型別；FullName 由 Mod 建構子另行預熱。
        /// </summary>
        private static void EnumerateTypesOnMainThread(Assembly[] assembliesSnapshot, int snapshotCount, int dynamicTypeCount)
        {
            StoreTypes(BuildTypeList(assembliesSnapshot), snapshotCount, dynamicTypeCount);
        }

        /// <summary>背景緒僅做型別「列舉」，不讀取 FullName（原因見 Preload 的說明）。</summary>
        private static void EnumerateTypesInBackground(Assembly[] assembliesSnapshot, int snapshotCount, int dynamicTypeCount)
        {
            Task.Run(() =>
            {
                // 最外層安全網：fire-and-forget 背景 Task 的例外無人觀察，
                // 若 ToList/lock 拋出例外將靜默遺失，故統一兜底記錄。
                try
                {
                    // 稍微延遲 50 毫秒，避開啟動時的併發載入高峰
                    System.Threading.Thread.Sleep(FGLConsts.AccessToolsPreloadDelayMs);

                    StoreTypes(BuildTypeList(assembliesSnapshot), snapshotCount, dynamicTypeCount);
                }
                catch (Exception ex)
                {
                    FGLLog.Warning("Unexpected exception preloading all types cache in background:", ex);
                }
            });
        }

        /// <summary>
        /// 對快照中的每個組件取得型別（經 <see cref="AssemblyTypesCache"/>，與 FullName 預熱共用列舉結果）並彙整為單一清單。
        /// 僅做型別「列舉」，不讀取 type.FullName（名稱解析交由 GenTypes 的預熱流程）。
        /// </summary>
        private static List<Type> BuildTypeList(System.Reflection.Assembly[] assemblies)
        {
            var list = new List<Type>();
            foreach (var assembly in assemblies)
            {
                try
                {
                    list.AddRange(AssemblyTypesCache.Get(assembly));
                }
                catch
                {
                    // 忽略個別組件型別讀取失敗
                }
            }
            return list;
        }

        /// <summary>
        /// 動態組件的型別總數。<see cref="AssemblyTypesCache"/> 對動態組件每次重新列舉，型別只增不減，
        /// 因此總數不變即代表動態型別集合不變；一般固定組件不列入，維持快取命中成本。
        /// </summary>
        private static int CountDynamicTypes(Assembly[] assemblies)
        {
            int count = 0;
            foreach (var assembly in assemblies)
            {
                if (!assembly.IsDynamic) continue;
                try
                {
                    count += AssemblyTypesCache.Get(assembly).Length;
                }
                catch
                {
                    // 與 BuildTypeList 相同：忽略個別組件型別讀取失敗
                }
            }
            return count;
        }

        /// <summary>
        /// 列舉＋lock＋賦值的共用後端：三處快取寫入（主執行緒列舉、背景列舉、快取失效重建）皆經由此處。
        /// </summary>
        private static void StoreTypes(List<Type> types, int assembliesCount, int dynamicTypeCount)
        {
            lock (typesLock)
            {
                allTypesCached = types;
                cachedAssembliesCount = assembliesCount;
                cachedDynamicTypeCount = dynamicTypeCount;
            }
        }

        /// <summary>
        /// 前置攔截：如果已經有全類型快取且組件數量與動態型別數量一致，則直接回傳快取結果並跳過原方法。
        /// 若組件數量改變（例如在加載期新加載了其他 Mod 的 DLL），或既有動態組件新增了型別，則判定快取失效並重新載入。
        /// </summary>
        /// <remarks>
        /// 快取失效策略假設：RimWorld 啟動期間 AppDomain 的組件集合只會增加（不會移除），
        /// 因此「數量相同 ≡ 集合相同」的等價關係成立。
        /// 若未來版本的 .NET 或 RimWorld 允許動態卸載組件，此假設需要重新評估。
        /// </remarks>
        public static bool Prefix(ref IEnumerable<Type> __result)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            int currentCount = assemblies.Length;
            int currentDynamicTypeCount = CountDynamicTypes(assemblies);

            // double-checked locking
            var cached = allTypesCached;
            if (cached != null && cachedAssembliesCount == currentCount && cachedDynamicTypeCount == currentDynamicTypeCount)
            {
                __result = cached;
                return false;
            }
            lock (typesLock)
            {
                cached = allTypesCached;
                if (cached != null && cachedAssembliesCount == currentCount && cachedDynamicTypeCount == currentDynamicTypeCount)
                {
                    __result = cached;
                    return false;
                }
                StoreTypes(BuildTypeList(assemblies), currentCount, currentDynamicTypeCount);
                __result = allTypesCached;
                return false;
            }
        }
    }
}
