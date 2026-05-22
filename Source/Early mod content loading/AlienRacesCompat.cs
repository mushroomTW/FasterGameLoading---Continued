using HarmonyLib;
using RimWorld;
using System;
using System.Linq;
using System.Reflection;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 在正常載入完成後，重新觸發 Alien Races 的 extended graphics variant 掃描，
    /// 避免提早載入導致 variantCount 為 0 而產生錯誤的身體貼圖路徑解析。
    /// 使用反射避免對此 Mod 的硬依賴。
    /// </summary>
    public static class AlienRacesCompat
    {
        private static bool rescanDone = false;

        /// <summary>
        /// 在所有 Mod 完成 ReloadContentInt 後呼叫，安排在載入完成後重新掃描。
        /// </summary>
        public static void ScheduleRescan()
        {
            if (rescanDone) return;

            try
            {
                var alienAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "AlienRace");
                if (alienAssembly == null) return;

                Log.Message("[FasterGameLoading] 檢測到 Alien Races，安排在載入完成後重新掃描 extended graphics");

                // 使用 LongEventHandler 在載入完成後執行
                // 此時所有 Def 已解析完畢，貼圖資料庫完整
                LongEventHandler.ExecuteWhenFinished(() => PerformRescan(alienAssembly));
            }
            catch (Exception ex)
            {
                Log.Warning("[FasterGameLoading] Alien Races 檢測失敗: " + ex.Message);
            }
        }

        private static void PerformRescan(Assembly alienAssembly)
        {
            if (rescanDone) return;

            try
            {
                // 尋找 AlienPartGenerator 型別
                var apgType = alienAssembly.GetType("AlienRace.AlienPartGenerator");
                if (apgType == null) return;

                // 取得 graphicsQueue (HashSet<AlienPartGenerator>)
                var graphicsQueueField = AccessTools.Field(apgType, "graphicsQueue");
                if (graphicsQueueField == null) return;

                var queue = graphicsQueueField.GetValue(null);
                if (queue == null) return;

                var countProp = AccessTools.Property(queue.GetType(), "Count");
                if (countProp == null)
                {
                    Log.Warning("[FasterGameLoading] Alien Races graphicsQueue 型別無 Count 屬性");
                    return;
                }
                var count = (int)countProp.GetValue(queue);

                // 如果 queue 還有內容，表示 Hook 尚未執行，不需我們手動觸發
                if (count > 0)
                {
                    Log.Message("[FasterGameLoading] Alien Races graphics queue 尚未處理，不需手動重新掃描");
                    rescanDone = true;
                    return;
                }

                // queue 為空 → Hook 已執行過（可能跑太早），需重新填充並觸發
                var thingDefAlienRaceType = AccessTools.TypeByName("AlienRace.ThingDef_AlienRace");
                if (thingDefAlienRaceType == null) return;

                var addMethod = AccessTools.Method(queue.GetType(), "Add");
                if (addMethod == null) return;

                bool anyAdded = false;

                foreach (var def in DefDatabase<ThingDef>.AllDefs)
                {
                    var defType = def.GetType();
                    if (!thingDefAlienRaceType.IsAssignableFrom(defType))
                        continue;

                    // 導覽路徑: ThingDef_AlienRace.alienRace.generalSettings.alienPartGenerator
                    var alienRace = AccessTools.Field(defType, "alienRace")?.GetValue(def) ??
                                    AccessTools.Property(defType, "alienRace")?.GetValue(def);
                    if (alienRace == null) continue;

                    var gs = AccessTools.Property(alienRace.GetType(), "generalSettings")?.GetValue(alienRace);
                    if (gs == null) continue;

                    var apg = AccessTools.Property(gs.GetType(), "alienPartGenerator")?.GetValue(gs);
                    if (apg == null) continue;

                    addMethod.Invoke(queue, new object[] { apg });
                    anyAdded = true;
                }

                if (anyAdded)
                {
                    var loadGraphicsHook = AccessTools.Method(apgType, "LoadGraphicsHook");
                    if (loadGraphicsHook != null)
                    {
                        loadGraphicsHook.Invoke(null, null);
                        Log.Message("[FasterGameLoading] Alien Races extended graphics 重新掃描完成");
                    }
                    else
                    {
                        Log.Warning("[FasterGameLoading] 找不到 AlienPartGenerator.LoadGraphicsHook 方法");
                    }
                }

                rescanDone = true;
            }
            catch (Exception ex)
            {
                Log.Warning("[FasterGameLoading] Alien Races 重新掃描失敗: " + ex.Message);
#if DEBUG
                Log.Error($"[FasterGameLoading] Alien Races rescan error details: {ex}");
#endif
            }
        }
    }
}
