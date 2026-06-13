using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

        /// <summary>
        /// 在背景執行緒中預先載入所有型別。
        /// 由 FasterGameLoadingMod 建構子呼叫。
        /// </summary>
        public static void Preload()
        {
            // 在主執行緒先取得 Assemblies 快照，防止列舉時集合發生 Race Condition
            var assembliesSnapshot = Enumerable.ToArray(AppDomain.CurrentDomain.GetAssemblies());
            int snapshotCount = assembliesSnapshot.Length;

            if (!FasterGameLoadingSettings.EnableMultiThreading)
            {
                // 當關閉多執行緒預載入時，直接在當前（主）執行緒同步載入，避免後續在其他執行緒上觸發初始化
                var types = assembliesSnapshot
                    .SelectMany(assembly =>
                    {
                        try { return AccessTools.GetTypesFromAssembly(assembly); }
                        catch { return Array.Empty<Type>(); }
                    }).ToList();
                lock (typesLock)
                {
                    allTypesCached = types;
                    cachedAssembliesCount = snapshotCount;
                    WarmupTypeCache(types);
                }
                return;
            }
            
            Task.Run(() =>
            {
                // 最外層安全網：fire-and-forget 背景 Task 的例外無人觀察，
                // 若 ToList/lock/WarmupTypeCache 拋出例外將靜默遺失，故統一兜底記錄。
                try
                {
                    // 稍微延遲 50 毫秒，避開啟動時的併發載入高峰
                    System.Threading.Thread.Sleep(FGLConsts.AccessToolsPreloadDelayMs);

                    var types = assembliesSnapshot
                        .SelectMany(assembly =>
                        {
                            try { return AccessTools.GetTypesFromAssembly(assembly); }
                            catch { return Array.Empty<Type>(); }
                        }).ToList();
                    lock (typesLock)
                    {
                        allTypesCached = types;
                        cachedAssembliesCount = snapshotCount;
                        WarmupTypeCache(types);
                    }
                }
                catch (Exception ex)
                {
                    FGLLog.Warning("背景預載全類型快取時發生未預期例外: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// 前置攔截：如果已經有全類型快取且組件數量一致，則直接回傳快取結果並跳過原方法。
        /// 若組件數量改變（例如在加載期新加載了其他 Mod 的 DLL），則判定快取失效並重新載入。
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

            // double-checked locking
            var cached = allTypesCached;
            if (cached != null && cachedAssembliesCount == currentCount)
            {
                __result = cached;
                return false;
            }
            lock (typesLock)
            {
                cached = allTypesCached;
                if (cached != null && cachedAssembliesCount == currentCount)
                {
                    __result = cached;
                    return false;
                }
                allTypesCached = assemblies
                    .SelectMany(assembly =>
                    {
                        try { return AccessTools.GetTypesFromAssembly(assembly); }
                        catch { return Array.Empty<Type>(); }
                    }).ToList();
                cachedAssembliesCount = currentCount;
                WarmupTypeCache(allTypesCached);
                __result = allTypesCached;
                return false;
            }
        }

        /// <summary>
        /// 預先填充 GenTypes.GetTypeInAnyAssemblyInt 的快取，消除主執行緒首次反射查詢的開銷。
        /// </summary>
        /// <remarks>
        /// 注意：此處僅能預熱 FullName（全名）。
        /// 過去在此處亦將 type.Name（短名稱）寫入快取，但由於不同 Mod 間極易存在同名但不同命名空間的類別，
        /// 預熱時不分順序直接寫入 type.Name 會導致「型態短名稱污染」，使 YetAnotherOptimizer 或核心在載入/反射欄位時拿到錯誤的 Type。
        /// 這會進一步在翻譯注入（InjectIntoDefs）時，因欄位反射型別錯誤，於 MakeGenericType 拋出 Invalid generic arguments 崩潰。
        /// 對於短名稱的快取，應交由執行期解析成功後再於 Postfix 中動態寫入。
        /// </remarks>
        private static void WarmupTypeCache(List<Type> types)
        {
            if (types == null) return;
            foreach (var type in types)
            {
                if (type == null) continue;
                try
                {
                    var fullName = type.FullName;
                    if (!string.IsNullOrEmpty(fullName))
                    {
                        GenTypes_GetTypeInAnyAssemblyInt_Patch.cachedResults.TryAdd(fullName, type);
                    }
                }
                catch
                {
                    // 忽略個別型別反射處理錯誤
                }
            }
        }
    }
}