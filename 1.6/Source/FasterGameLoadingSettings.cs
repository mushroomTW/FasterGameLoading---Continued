using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    public class FasterGameLoadingSettings : ModSettings
    {
        public static Dictionary<string, string> loadedTexturesSinceLastSession = new Dictionary<string, string>();
        public static Dictionary<string, ModContentPack> modsByPackageIds = new Dictionary<string, ModContentPack>();
        public static Dictionary<string, string> loadedTypesByFullNameSinceLastSession = new Dictionary<string, string>();
        public static List<string> modsInLastSession = new List<string>();
        public static HashSet<string> successfulXMLPathesSinceLastSession = new HashSet<string>();
        public static HashSet<string> failedXMLPathesSinceLastSession = new HashSet<string>();
        public static bool delayGraphicLoading = false;
        public static bool earlyModContentLoading = true;
        public static bool disableStaticAtlasesBaking = false;
        public static ModContentPack GetModContent(string packageId)
        {
            var packageLower = packageId.ToLower();
            if (!modsByPackageIds.TryGetValue(packageLower, out var mod))
            {
                modsByPackageIds[packageLower] = mod = LoadedModManager.RunningModsListForReading.FirstOrDefault(x =>
                    x.PackageIdPlayerFacing.ToLower() == packageLower);
            }
            return mod;
        }

        public static void DoSettingsWindowContents(Rect inRect)
        {
            var ls = new Listing_Standard();
            ls.Begin(inRect);
            ls.CheckboxLabeled("FGL_EarlyModContentLoading".Translate(), ref earlyModContentLoading);
            ls.CheckboxLabeled("FGL_DelayGraphicLoading".Translate(), ref delayGraphicLoading);
            ls.CheckboxLabeled("FGL_DisableStaticAtlasesBaking".Translate(), ref disableStaticAtlasesBaking);
            ls.Gap(12f);

            // 紋理縮放說明文字
            var explanationText = "FGL_TextureResizingExplanation".Translate();
            var textHeight = Text.CalcHeight(explanationText, ls.ColumnWidth);
            var explanationRect = ls.GetRect(textHeight + 8f);
            Widgets.Label(explanationRect, explanationText);

            // 紋理縮放按鈕
            ls.Gap(4f);
            if (ls.ButtonText("FGL_DownscaleTextures".Translate()))
            {
                Find.WindowStack.Add(new Dialog_MessageBox("FGL_TextureResizingConfirmation".Translate(), "Confirm".Translate(), delegate
                {
                    TextureResize.DoTextureResizing();
                }, "GoBack".Translate()));
            }
            ls.End();
        }


        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref loadedTexturesSinceLastSession, "loadedTexturesSinceLastSession", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref loadedTypesByFullNameSinceLastSession, "loadedTypesByFullNameSinceLastSession", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref successfulXMLPathesSinceLastSession, "successfulXMLPathesSinceLastSession", LookMode.Value);
            Scribe_Collections.Look(ref failedXMLPathesSinceLastSession, "failedXMLPathesSinceLastSession", LookMode.Value);
            Scribe_Collections.Look(ref modsInLastSession, "modsInLastSession", LookMode.Value);
            Scribe_Values.Look(ref disableStaticAtlasesBaking, "disableStaticAtlasesBaking");
            Scribe_Values.Look(ref delayGraphicLoading, "delayGraphicLoading", false);
            Scribe_Values.Look(ref earlyModContentLoading, "earlyModContentLoading", true);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                loadedTexturesSinceLastSession ??= new Dictionary<string, string>();
                loadedTypesByFullNameSinceLastSession ??= new Dictionary<string, string>();
                failedXMLPathesSinceLastSession ??= new HashSet<string>();
                successfulXMLPathesSinceLastSession ??= new HashSet<string>();
                modsInLastSession ??= new List<string>();
                if (!modsInLastSession.SequenceEqual(ModsConfig.ActiveModsInLoadOrder.Select(x => x.packageIdLowerCase)))
                {
                    loadedTexturesSinceLastSession.Clear();
                    loadedTypesByFullNameSinceLastSession.Clear();
                    failedXMLPathesSinceLastSession.Clear();
                    successfulXMLPathesSinceLastSession.Clear();
                }
            }
        }
    }
}

