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
                    .FirstOrDefault(a => a.GetName().Name == FGLConsts.AlienRaceAssemblyName);
                if (alienAssembly == null) return;

                FGLLog.Message("Alien Races detected, scheduling extended graphics rescan after loading");

                // 使用 LongEventHandler 在載入完成後執行
                // 此時所有 Def 已解析完畢，貼圖資料庫完整
                LongEventHandler.ExecuteWhenFinished(() => PerformRescan(alienAssembly));
            }
            catch (Exception ex)
            {
                FGLLog.Warning("Alien Races detection failed: ", ex);
            }
        }

        private static void PerformRescan(Assembly alienAssembly)
        {
            if (rescanDone) return;

            try
            {
                // 尋找 AlienPartGenerator 型別
                var apgType = alienAssembly.GetType(FGLConsts.AlienPartGeneratorTypeName);
                if (apgType == null) return;

                // 取得 graphicsQueue (HashSet<AlienPartGenerator>)
                var graphicsQueueField = AccessTools.Field(apgType, FGLConsts.GraphicsQueueFieldName);
                if (graphicsQueueField == null) return;

                var queue = graphicsQueueField.GetValue(null);
                if (queue == null) return;

                var countProp = AccessTools.Property(queue.GetType(), FGLConsts.CountPropertyName);
                if (countProp == null)
                {
                    FGLLog.Warning("Alien Races graphicsQueue type has no Count property");
                    return;
                }
                var count = (int)countProp.GetValue(queue);

                // 如果 queue 還有內容，表示 Hook 尚未執行，不需我們手動觸發
                if (count > 0)
                {
                    FGLLog.Message("Alien Races graphics queue not yet processed, skipping manual rescan");
                    rescanDone = true;
                    return;
                }

                // queue 為空 → Hook 已執行過（可能跑太早），需重新填充並觸發
                var thingDefAlienRaceType = AccessTools.TypeByName(FGLConsts.ThingDefAlienRaceTypeName);
                if (thingDefAlienRaceType == null) return;

                var addMethod = AccessTools.Method(queue.GetType(), FGLConsts.AddMethodName);
                if (addMethod == null) return;

                bool anyAdded = false;

                foreach (var def in DefDatabase<ThingDef>.AllDefs)
                {
                    var defType = def.GetType();
                    if (!thingDefAlienRaceType.IsAssignableFrom(defType))
                        continue;

                    // 導覽路徑: ThingDef_AlienRace.alienRace.generalSettings.alienPartGenerator
                    var alienRace = AccessTools.Field(defType, FGLConsts.AlienRaceFieldName)?.GetValue(def) ??
                                    AccessTools.Property(defType, FGLConsts.AlienRaceFieldName)?.GetValue(def);
                    if (alienRace == null) continue;

                    var gs = AccessTools.Property(alienRace.GetType(), FGLConsts.GeneralSettingsPropertyName)?.GetValue(alienRace);
                    if (gs == null) continue;

                    var apg = AccessTools.Property(gs.GetType(), FGLConsts.AlienPartGeneratorPropertyName)?.GetValue(gs);
                    if (apg == null) continue;

                    addMethod.Invoke(queue, new object[] { apg });
                    anyAdded = true;
                }

                if (anyAdded)
                {
                    var loadGraphicsHook = AccessTools.Method(apgType, FGLConsts.LoadGraphicsHookMethodName);
                    if (loadGraphicsHook != null)
                    {
                        loadGraphicsHook.Invoke(null, null);
                        FGLLog.Message("Alien Races extended graphics rescan complete");
                    }
                    else
                    {
                        FGLLog.Warning("AlienPartGenerator.LoadGraphicsHook method not found");
                    }
                }

                rescanDone = true;
            }
            catch (Exception ex)
            {
                FGLLog.Warning("Alien Races rescan failed: ", ex);
            }
        }
    }
}
