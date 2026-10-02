using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;
using RimWorld;
using UnityEngine;
using Verse;

namespace FasterGameLoading.Tests
{
    [TestFixture]
    public class UtilsTests
    {
        private static T Uninitialized<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));

        [Test]
        public void TestNormalizePath_WithNull_ReturnsNull()
        {
            string input = null;
            string result = input.NormalizePath();
            Assert.IsNull(result);
        }

        [Test]
        public void TestNormalizePath_WithWindowsBackslashes_NormalizesToForwardSlashes()
        {
            string input = @"C:\Program Files (x86)\RimWorld\Mods\Textures\UI\Icon.png";
            string expected = "C:/Program Files (x86)/RimWorld/Mods/Textures/UI/Icon.png";
            string result = input.NormalizePath();
            Assert.AreEqual(expected, result);
        }

        [Test]
        public void TestNormalizePath_WithMixedSlashes_NormalizesToForwardSlashes()
        {
            string input = "C:/Program Files (x86)\\RimWorld/Mods\\Textures/UI/Icon.png";
            string expected = "C:/Program Files (x86)/RimWorld/Mods/Textures/UI/Icon.png";
            string result = input.NormalizePath();
            Assert.AreEqual(expected, result);
        }

        [Test]
        public void TestFloorToPowerOfTwo_WithPowerOfTwoInput_ReturnsSameValue()
        {
            Assert.AreEqual(1, 1.FloorToPowerOfTwo());
            Assert.AreEqual(256, 256.FloorToPowerOfTwo());
            Assert.AreEqual(1024, 1024.FloorToPowerOfTwo());
            Assert.AreEqual(2048, 2048.FloorToPowerOfTwo());
        }

        [Test]
        public void TestFloorToPowerOfTwo_WithNonPowerOfTwoInput_ReturnsLargestPowerOfTwoLessOrEqual()
        {
            Assert.AreEqual(2, 3.FloorToPowerOfTwo());
            Assert.AreEqual(4, 5.FloorToPowerOfTwo());
            Assert.AreEqual(4, 7.FloorToPowerOfTwo());
            Assert.AreEqual(256, 300.FloorToPowerOfTwo());
            Assert.AreEqual(512, 1000.FloorToPowerOfTwo());
        }

        [Test]
        public void TestFloorToPowerOfTwo_WithZeroOrNegativeInput_ReturnsZero()
        {
            Assert.AreEqual(0, 0.FloorToPowerOfTwo());
            Assert.AreEqual(0, (-1).FloorToPowerOfTwo());
            Assert.AreEqual(0, (-10).FloorToPowerOfTwo());
        }

        [Test]
        public void ShouldBeLoadedImmediately_WithDesignationCategory_ReturnsTrue()
        {
            var def = Uninitialized<ThingDef>();
            def.designationCategory = Uninitialized<DesignationCategoryDef>();
            Assert.That(def.ShouldBeLoadedImmediately(), Is.True);
        }

        [Test]
        public void ShouldBeLoadedImmediately_WithUiIconPath_ReturnsTrue()
        {
            var def = Uninitialized<ThingDef>();
            def.uiIconPath = "UI/Icons/ThingIcon";
            Assert.That(def.ShouldBeLoadedImmediately(), Is.True);
        }

        [Test]
        public void ShouldBeLoadedImmediately_WithBlueprintOrFrame_ReturnsTrue()
        {
            var blueprintDef = Uninitialized<ThingDef>();
            blueprintDef.entityDefToBuild = Uninitialized<ThingDef>();
            blueprintDef.category = ThingCategory.Ethereal;
            blueprintDef.thingClass = typeof(Blueprint_Build);
            Assert.That(blueprintDef.ShouldBeLoadedImmediately(), Is.True);

            var frameDef = Uninitialized<ThingDef>();
            frameDef.entityDefToBuild = Uninitialized<ThingDef>();
            frameDef.category = ThingCategory.Building;
            frameDef.thingClass = typeof(Frame);
            AccessTools.Field(typeof(ThingDef), "isFrameInt")?.SetValue(frameDef, value: true);
            Assert.That(frameDef.ShouldBeLoadedImmediately(), Is.True);
        }

        [Test]
        public void ShouldBeLoadedImmediately_WithLinkedGraphicData_ReturnsTrue()
        {
            var def = Uninitialized<ThingDef>();
            def.graphicData = Uninitialized<GraphicData>();
            def.graphicData.linkType = LinkDrawerType.Basic;
            Assert.That(def.ShouldBeLoadedImmediately(), Is.True);
        }

        public class Building_Pipe : Thing;

        [Test]
        public void ShouldBeLoadedImmediately_WithPipeClass_ReturnsTrue()
        {
            var def = Uninitialized<ThingDef>();
            def.thingClass = typeof(Building_Pipe);
            Assert.That(def.ShouldBeLoadedImmediately(), Is.True);
        }

        [Test]
        public void ShouldBeLoadedImmediately_WithMedicine_ReturnsTrue()
        {
            var def = Uninitialized<ThingDef>();
            def.thingClass = typeof(Medicine);
            Assert.That(def.ShouldBeLoadedImmediately(), Is.True);

            var defWithTakeGroup = Uninitialized<ThingDef>();
            var groupField = AccessTools.Field(typeof(ThingDef), "orderedTakeGroup");
            if (groupField != null)
            {
                var groupObj = FormatterServices.GetUninitializedObject(groupField.FieldType);
                var defNameField = AccessTools.Field(groupField.FieldType, "defName");
                defNameField?.SetValue(groupObj, FGLConsts.MedicineDefName);
                groupField.SetValue(defWithTakeGroup, groupObj);
                Assert.That(defWithTakeGroup.ShouldBeLoadedImmediately(), Is.True);
            }
        }

        [Test]
        public void ShouldBeLoadedImmediately_WithWeaponOrApparel_ReturnsTrue()
        {
            var weaponDef = Uninitialized<ThingDef>();
            weaponDef.category = ThingCategory.Item;
            weaponDef.tools = new List<Tool> { Uninitialized<Tool>() };
            Assert.That(weaponDef.ShouldBeLoadedImmediately(), Is.True);

            var apparelDef = Uninitialized<ThingDef>();
            apparelDef.category = ThingCategory.Item;
            apparelDef.apparel = Uninitialized<ApparelProperties>();
            Assert.That(apparelDef.ShouldBeLoadedImmediately(), Is.True);
        }

        [Test]
        public void ShouldBeLoadedImmediately_WithFoodOrStuff_ReturnsTrue()
        {
            var foodDef = Uninitialized<ThingDef>();
            foodDef.ingestible = Uninitialized<IngestibleProperties>();
            Assert.That(foodDef.ShouldBeLoadedImmediately(), Is.True);

            var stuffDef = Uninitialized<ThingDef>();
            stuffDef.stuffProps = Uninitialized<StuffProperties>();
            Assert.That(stuffDef.ShouldBeLoadedImmediately(), Is.True);
        }

        [Test]
        public void ShouldBeLoadedImmediately_WithRace_ReturnsTrue()
        {
            var pawnDef = Uninitialized<ThingDef>();
            pawnDef.race = Uninitialized<RaceProperties>();
            Assert.That(pawnDef.ShouldBeLoadedImmediately(), Is.True);
        }

        [Test]
        public void ShouldBeLoadedImmediately_WithFurnitureOrProductionCategories_ReturnsTrue()
        {
            var furnitureDef = Uninitialized<ThingDef>();
            var cat = Uninitialized<ThingCategoryDef>();
            cat.defName = "BuildingsFurniture";
            furnitureDef.thingCategories = new List<ThingCategoryDef> { cat };
            Assert.That(furnitureDef.ShouldBeLoadedImmediately(), Is.True);

            var productionDef = Uninitialized<ThingDef>();
            var prodCat = Uninitialized<ThingCategoryDef>();
            prodCat.defName = "BuildingsProduction";
            productionDef.thingCategories = new List<ThingCategoryDef> { prodCat };
            Assert.That(productionDef.ShouldBeLoadedImmediately(), Is.True);

            var securityDef = Uninitialized<ThingDef>();
            var secCat = Uninitialized<ThingCategoryDef>();
            secCat.defName = "BuildingsSecurity";
            securityDef.thingCategories = new List<ThingCategoryDef> { secCat };
            Assert.That(securityDef.ShouldBeLoadedImmediately(), Is.True);
        }

        [Test]
        public void ShouldBeLoadedImmediately_WithGenericDecorativeThing_ReturnsFalse()
        {
            var genericDef = Uninitialized<ThingDef>();
            var miscCat = Uninitialized<ThingCategoryDef>();
            miscCat.defName = "MiscCategory";
            genericDef.thingCategories = new List<ThingCategoryDef> { miscCat };

            Assert.That(genericDef.ShouldBeLoadedImmediately(), Is.False);
        }

        [Test]
        public void ShouldBeLoadedImmediately_WithNullCategoryElementOrNullDefName_DoesNotThrowAndReturnsFalse()
        {
            var defWithNullCategory = Uninitialized<ThingDef>();
            defWithNullCategory.thingCategories = new List<ThingCategoryDef> { null };
            Assert.DoesNotThrow(() => Assert.That(defWithNullCategory.ShouldBeLoadedImmediately(), Is.False));

            var defWithNullDefName = Uninitialized<ThingDef>();
            var catWithNullName = Uninitialized<ThingCategoryDef>();
            catWithNullName.defName = null;
            defWithNullDefName.thingCategories = new List<ThingCategoryDef> { catWithNullName };
            Assert.DoesNotThrow(() => Assert.That(defWithNullDefName.ShouldBeLoadedImmediately(), Is.False));
        }
    }
}
