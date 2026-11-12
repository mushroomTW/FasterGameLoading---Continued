using System.Collections.Generic;
using System.Linq;
using Verse;

namespace FasterGameLoading;

/// <summary>
/// 跨 session 載入快取的持久化與失效政策。這些不是「使用者設定」，而是自動記錄的載入歷程，
/// 僅因需要 Scribe 持久化而經由設定檔存放。資料本身歸各自的模組所有：
/// 型別對照在 <see cref="TypeLookupCache"/>、
/// 降質快取對照在 <see cref="TextureCacheManager"/>；這裡只記錄上次的 mod 清單，並在讀檔後決定哪些資料作廢。
/// </summary>
internal static class SessionCache
{
    /// <summary>
    /// 上一次 session 中啟用的 mod 列表（packageIdLowerCase）。
    /// </summary>
    internal static List<string> modsInLastSession { get; set; } = new();

    /// <summary>
    /// 1.6 自適應烘焙記錄的歷次烘焙速度。1.7 不使用自適應烘焙，這份資料只原樣讀寫，
    /// 讓同一份設定檔切回 1.6 時不會遺失；1.7 不讀取其內容。
    /// </summary>
    internal static List<float> BakeSpeedHistory { get; set; } = new();

    /// <summary>
    /// 由 FasterGameLoadingSettings.ExposeData() 委派呼叫，
    /// 處理所有跨 session 快取資料的序列化。
    /// </summary>
    internal static void ExposeData()
    {
        TypeLookupCache.ExposeData();
        var mods = modsInLastSession;
        Scribe_Collections.Look(ref mods, FGLConsts.ModsInLastSessionKey, LookMode.Value);
        modsInLastSession = mods;
        var bakeSpeeds = BakeSpeedHistory;
        Scribe_Collections.Look(ref bakeSpeeds, FGLConsts.HistoricalBakeSpeedsKey, LookMode.Value);
        BakeSpeedHistory = bakeSpeeds ?? [];

        if (Scribe.mode is LoadSaveMode.PostLoadInit)
        {
            RestoreAfterLoad();
        }
    }

    /// <summary>
    /// PostLoadInit 階段的還原：mod 組合或組件變更時捨棄型別對照，
    /// mod 組合變更時另外移除已不屬於執行中 mod 的降質快取項目。
    /// </summary>
    private static void RestoreAfterLoad()
    {
        modsInLastSession ??= [];
        bool modSetChanged = DetectModSetChange();

        TypeLookupCache.RestoreAfterLoad(modSetChanged);
        if (modSetChanged)
        {
            // 降質快取以「原始路徑＋檔案大小＋修改時間」自我驗證，mod 清單變動不代表快取過期；
            // 只移除原始檔已不屬於任何執行中 mod 的項目，對應的快取檔交由啟動後的背景清理刪除。
            FasterGameLoadingMod.Instance?.CacheManager?.RemoveEntriesOutside(RunningModRootDirectories());
        }
    }

    // 所有執行中 mod 的根目錄；讀檔時（mod 建構子內）清單已建立完成。
    private static IEnumerable<string> RunningModRootDirectories() =>
        LoadedModManager.RunningMods.Where(static m => m != null).Select(static m => m.RootDir);

    // 比對目前啟用的 mod 清單與上次 session 的記錄是否一致。
    private static bool DetectModSetChange()
    {
        var activeMods = ModsConfig.ActiveModsInLoadOrder;
        if (modsInLastSession == null || activeMods == null) return true;
        return !modsInLastSession.SequenceEqual(activeMods.Select(static m => m?.packageIdLowerCase), System.StringComparer.Ordinal);
    }
}
