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
        // 以下兩個旗標僅在主執行緒上讀寫：
        //   ScheduleRescan 由 ModContentPack_ReloadContentInt_Patch.Postfix 呼叫（RimWorld 主執行緒）
        //   PerformRescan  由 LongEventHandler.ExecuteWhenFinished 的委派執行（同為主執行緒）
        // 因此不需要額外的執行緒同步機制。
        private static bool rescanDone = false;
        private static bool isScheduled = false;

        /// <summary>
        /// 在所有 Mod 完成 ReloadContentInt 後呼叫，安排在載入完成後重新掃描。
        /// 僅在主執行緒呼叫，見上方旗標說明。
        /// </summary>
        public static void ScheduleRescan()
        {
            if (rescanDone || isScheduled) return;

            try
            {
                var alienAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == FGLConsts.AlienRaceAssemblyName);
                if (alienAssembly == null) return;

                FGLLog.Message("FGL_Log_AlienRacesDetectedRescanScheduled".TranslateWithFallback("Alien Races detected, scheduling extended graphics rescan after loading"));

                isScheduled = true;
                // 使用 LongEventHandler 在載入完成後執行
                // 此時所有 Def 已解析完畢，貼圖資料庫完整
                LongEventHandler.ExecuteWhenFinished(() => PerformRescan(alienAssembly));
            }
            catch (Exception ex)
            {
                isScheduled = false;
                FGLLog.Warning("FGL_Log_AlienRacesDetectionFailed".TranslateWithFallback("Alien Races detection failed:"), ex);
            }
        }

        private static void PerformRescan(Assembly alienAssembly)
        {
            isScheduled = false;
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

                // 不再根據 count 提前跳過，直接將所有種族的 AlienPartGenerator 加入 queue 中
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

                    var gs = AccessTools.Field(alienRace.GetType(), FGLConsts.GeneralSettingsPropertyName)?.GetValue(alienRace) ??
                             AccessTools.Property(alienRace.GetType(), FGLConsts.GeneralSettingsPropertyName)?.GetValue(alienRace);
                    if (gs == null) continue;

                    var apg = AccessTools.Field(gs.GetType(), FGLConsts.AlienPartGeneratorPropertyName)?.GetValue(gs) ??
                              AccessTools.Property(gs.GetType(), FGLConsts.AlienPartGeneratorPropertyName)?.GetValue(gs);
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
                        FGLLog.Message("FGL_Log_AlienRacesRescanComplete".TranslateWithFallback("Alien Races extended graphics rescan complete"));
                    }
                    else
                    {
                        FGLLog.Warning("FGL_Log_AlienLoadGraphicsHookNotFound".TranslateWithFallback("AlienPartGenerator.LoadGraphicsHook method not found"));
                    }
                }

                rescanDone = true;
            }
            catch (Exception ex)
            {
                FGLLog.Warning("FGL_Log_AlienRacesRescanFailed".TranslateWithFallback("Alien Races rescan failed:"), ex);
            }
        }
    }
}
