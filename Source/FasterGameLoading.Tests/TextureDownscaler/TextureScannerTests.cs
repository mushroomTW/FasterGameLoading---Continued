using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;
using UnityEngine;
using Verse;

namespace FasterGameLoading.Tests.TextureDownscaler
{
    [TestFixture]
    public class TextureScannerTests
    {
        private static T Uninitialized<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));



        [TearDown]
        public void TearDown()
        {
            LoadedTextureRegistry.Clear();
        }

        [Test]
        public void TryGetTexturePath_WithNullTextureReturnsFalse()
        {
            var scanner = new TextureScanner();
            string path;

            var result = scanner.TryGetTexturePath(texture: null, fullPath: out path);

            Assert.That(result, Is.False);
            Assert.That(path, Is.Null);
        }

        [Test]
        public void TryGetTexturePath_WhenInTexturesByPaths_ReturnsTrueAndPath()
        {
            var scanner = new TextureScanner();
            var texture = Uninitialized<Texture2D>();
            scanner.texturesByPaths[texture] = "Mods/MyMod/Textures/Test.png";

            var result = scanner.TryGetTexturePath(texture, out var path);

            Assert.That(result, Is.True);
            Assert.That(path, Is.EqualTo("Mods/MyMod/Textures/Test.png"));
        }

        [Test]
        public void TryGetTexturePath_WhenInSavedTexturesPatch_FindsAndCachesPath()
        {
            var scanner = new TextureScanner();
            var texture = Uninitialized<Texture2D>();
            var expectedPath = "Mods/MyMod/Textures/FromSaved.png";

            LoadedTextureRegistry.Record(expectedPath, texture);

            var result = scanner.TryGetTexturePath(texture, out var path);

            Assert.That(result, Is.True);
            Assert.That(path, Is.EqualTo(expectedPath));
            Assert.That(scanner.texturesByPaths.ContainsKey(texture), Is.True);
        }

        [Test]
        public void AddEntry_DirectRegistration_PopulatesTexturesByDefs()
        {
            var scanner = new TextureScanner();
            var mainTex = Uninitialized<Texture2D>();
            var def = Uninitialized<ThingDef>();
            def.defName = "TestThing";

            var addDirectEntry = typeof(TextureScanner).GetMethod("AddEntry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null,
                new[] { typeof(BuildableDef), typeof(string), typeof(Texture) },
                null);
            Assert.That(addDirectEntry, Is.Not.Null);
            addDirectEntry.Invoke(scanner, new object[] { def, "Mods/MyMod/Textures/Main.png", mainTex });

            Assert.That(scanner.texturesByDefs.ContainsKey(mainTex), Is.True);
            Assert.That(scanner.texturesByDefs[mainTex].Key, Is.SameAs(def));
            Assert.That(scanner.texturesByDefs[mainTex].Value, Is.EqualTo("Mods/MyMod/Textures/Main.png"));
        }

        [Test]
        public void FillEntry_WithUiIcon_RegistersUiIconPath()
        {
            var scanner = new TextureScanner();
            var iconTex = Uninitialized<Texture2D>();
            scanner.texturesByPaths[iconTex] = "Mods/MyMod/Textures/Icon.png";

            var def = Uninitialized<ThingDef>();
            def.defName = "TestIconDef";
            def.uiIconPath = "Mods/MyMod/Textures/Icon.png";
            def.uiIcon = iconTex;

            var fillEntryMethod = typeof(TextureScanner).GetMethod("FillEntry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.That(fillEntryMethod, Is.Not.Null);

            fillEntryMethod.Invoke(scanner, new object[] { def, null });

            Assert.That(scanner.texturesByDefs.ContainsKey(iconTex), Is.True);
            Assert.That(scanner.texturesByDefs[iconTex].Value, Is.EqualTo("Mods/MyMod/Textures/Icon.png"));
        }

        [Test]
        public void ScanApparelVariants_And_ScanPlantVariants_ExecuteSafely()
        {
            var scanner = new TextureScanner();
            var def = Uninitialized<ThingDef>();
            def.defName = "ApparelDef";
            def.apparel = Uninitialized<RimWorld.ApparelProperties>();
            def.apparel.wornGraphicPath = "Things/Apparel/Worn";
            def.apparel.wornGraphicPaths = new List<string> { "Things/Apparel/WornAlt" };

            var plantDef = Uninitialized<ThingDef>();
            plantDef.defName = "PlantDef";
            plantDef.plant = Uninitialized<RimWorld.PlantProperties>();
            var single = Uninitialized<Graphic_Single>();
            AccessTools.Field(typeof(Graphic_Single), "mat")?.SetValue(single, Uninitialized<Material>());
            plantDef.plant.leaflessGraphic = single;
            plantDef.plant.immatureGraphic = single;
            plantDef.plant.pollutedGraphic = single;

            var scanApparel = typeof(TextureScanner).GetMethod("ScanApparelVariants",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var scanPlant = typeof(TextureScanner).GetMethod("ScanPlantVariants",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            Assert.That(scanApparel, Is.Not.Null);
            Assert.That(scanPlant, Is.Not.Null);

            Assert.DoesNotThrow(() => scanApparel.Invoke(scanner, new object[] { def, def }));
            Assert.DoesNotThrow(() => scanPlant.Invoke(scanner, new object[] { plantDef, plantDef }));
        }

        [Test]
        public void RefreshTexturePathMap_PopulatesTexturesByPaths()
        {
            var scanner = new TextureScanner();
            var tex = Uninitialized<Texture2D>();
            LoadedTextureRegistry.Record("Mods/MyMod/Tex.png", tex);

            var refreshMethod = typeof(TextureScanner).GetMethod("RefreshTexturePathMap",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.That(refreshMethod, Is.Not.Null);

            refreshMethod.Invoke(scanner, null);

            Assert.That(scanner.texturesByPaths.ContainsKey(tex), Is.True);
            Assert.That(scanner.texturesByPaths[tex], Is.EqualTo("Mods/MyMod/Tex.png"));
        }

        [Test]
        public void BuildTextureScanData_ExecutesWithoutException()
        {
            var scanner = new TextureScanner();

            Assert.DoesNotThrow(() => scanner.BuildTextureScanData());
        }

        [Test]
        public void ClearTextureScanData_ClearsAllIndexes()
        {
            var scanner = new TextureScanner();
            var texture = Uninitialized<Texture2D>();
            scanner.texturesByPaths[texture] = "texture";
            scanner.texturesByDefs[texture] =
                new KeyValuePair<BuildableDef, string>(key: null, value: "texture");

            scanner.ClearTextureScanData();

            Assert.That(scanner.texturesByPaths, Is.Empty);
            Assert.That(scanner.texturesByDefs, Is.Empty);
        }

        [Test]
        public void ClearTextureScanData_IsSafeWhenEmpty()
        {
            var scanner = new TextureScanner();

            Assert.DoesNotThrow(() => scanner.ClearTextureScanData());
        }

        [Test]
        public void CacheLoadCounters_GetterSetter_RoundTripPreservesValue()
        {
            // cacheLoadHits / cacheLoadFailures 是公開靜態計數器，供 Prefix 在命中/失敗時遞增，
            // 並在摘要訊息中讀取。此測試驗證 getter 與 setter 能正確往返數值。
            ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadHits = 7;
            ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadFailures = 3;

            Assert.That(ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadHits, Is.EqualTo(7));
            Assert.That(ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadFailures, Is.EqualTo(3));

            // 清理，避免污染其他測試
            ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadHits = 0;
            ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadFailures = 0;
        }

        [Test]
        public void CacheLoadCounters_ResetToZero_ClearsPreviousValues()
        {
            ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadHits = 11;
            ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadFailures = 5;

            ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadHits = 0;
            ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadFailures = 0;

            Assert.That(ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadHits, Is.EqualTo(0));
            Assert.That(ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadFailures, Is.EqualTo(0));
        }
    }
}
