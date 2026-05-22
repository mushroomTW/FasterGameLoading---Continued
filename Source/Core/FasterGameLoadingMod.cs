using HarmonyLib;
using UnityEngine;
using Verse;
namespace FasterGameLoading
{
    public class FasterGameLoadingMod : Mod
    {
        public static Harmony harmony;
        public static FasterGameLoadingSettings settings;
        public static DelayedActions delayedActions;
        public FasterGameLoadingMod(ModContentPack pack) : base(pack)
        {
            var gameObject = new GameObject("FasterGameLoadingMod");
            Object.DontDestroyOnLoad(gameObject);
            delayedActions = gameObject.AddComponent<DelayedActions>();
            settings = this.GetSettings<FasterGameLoadingSettings>();
            harmony = new Harmony("FasterGameLoadingMod");
            AccessTools_AllTypes_Patch.Preload();
            harmony.PatchAll();

            // 註冊執行個體層級的快取清理（在語言切換時由 CacheResetter.ResetAll() 觸發）
            CacheResetter.Register(() =>
            {
                if (delayedActions != null)
                {
                    delayedActions.StopAllCoroutines();
                    delayedActions.graphicsToLoad.Clear();
                    delayedActions.iconsToLoad.Clear();
                    delayedActions.subSoundDefToResolve.Clear();
                    delayedActions.ResetEarlyLoading();
                }
                try
                {
                    harmony.PatchCategory("SoundStarter");
                }
                catch { /* 如果已 patched 就忽略 */ }
            });
        }

        public override string SettingsCategory()
        {
            return "FGL_ModName".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            base.DoSettingsWindowContents(inRect);
            FasterGameLoadingSettings.DoSettingsWindowContents(inRect);
        }
    }
}

