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

        /// <summary>
        /// 在背景執行緒中預先載入所有型別。
        /// 由 FasterGameLoadingMod 建構子呼叫。
        /// </summary>
        public static void Preload()
        {
            if (!FasterGameLoadingSettings.EnableMultiThreading)
            {
                // 當關閉多執行緒預載入時，直接在當前（主）執行緒同步載入，避免後續在其他執行緒上觸發初始化
                var types = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(assembly =>
                    {
                        try { return AccessTools.GetTypesFromAssembly(assembly); }
                        catch { return Array.Empty<Type>(); }
                    }).ToList();
                lock (typesLock)
                {
                    allTypesCached = types;
                }
                return;
            }
            Task.Run(() =>
            {
                var types = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(assembly =>
                    {
                        try { return AccessTools.GetTypesFromAssembly(assembly); }
                        catch { return Array.Empty<Type>(); }
                    }).ToList();
                lock (typesLock)
                {
                    allTypesCached = types;
                }
            });
        }

        public static bool Prefix(ref IEnumerable<Type> __result)
        {
            // double-checked locking：先讀取 volatile，再 lock 檢查
            var cached = allTypesCached;
            if (cached != null)
            {
                __result = cached;
                return false;
            }
            lock (typesLock)
            {
                cached = allTypesCached;
                if (cached != null)
                {
                    __result = cached;
                    return false;
                }
                allTypesCached = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(assembly =>
                    {
                        try { return AccessTools.GetTypesFromAssembly(assembly); }
                        catch { return Array.Empty<Type>(); }
                    }).ToList();
                __result = allTypesCached;
                return false;
            }
        }
    }
}