using System.Collections.Generic;
using System.Runtime.Serialization;
using NUnit.Framework;
using RimWorld;
using UnityEngine;
using Verse;

namespace FasterGameLoading.Tests.TextureDownscaler
{
    [TestFixture]
    public class TextureResizerTests
    {
        private static T Uninitialized<T>()
        {
            return (T)FormatterServices.GetUninitializedObject(typeof(T));
        }

        [Test]
        public void TargetSizes_ContainsExpectedTargetSizesForEachCategory()
        {
            Assert.That(TextureResizer.targetSizes[TextureResize.TextureType.Building], Is.EqualTo(256));
            Assert.That(TextureResizer.targetSizes[TextureResize.TextureType.Pawn], Is.EqualTo(256));
            Assert.That(TextureResizer.targetSizes[TextureResize.TextureType.Apparel], Is.EqualTo(128));
            Assert.That(TextureResizer.targetSizes[TextureResize.TextureType.Weapon], Is.EqualTo(128));
            Assert.That(TextureResizer.targetSizes[TextureResize.TextureType.Item], Is.EqualTo(128));
            Assert.That(TextureResizer.targetSizes[TextureResize.TextureType.Plant], Is.EqualTo(128));
            Assert.That(TextureResizer.targetSizes[TextureResize.TextureType.Tree], Is.EqualTo(256));
            Assert.That(TextureResizer.targetSizes[TextureResize.TextureType.Terrain], Is.EqualTo(1024));
        }

        [Test]
        public void TryGetResizeTarget_TerrainUsesTerrainTarget()
        {
            int targetSize;
            var result = TextureResizer.TryGetResizeTarget(
                texture: null, def: Uninitialized<TerrainDef>(), targetSize: out targetSize);

            Assert.That(result, Is.True);
            Assert.That(targetSize, Is.EqualTo(1024));
        }

        [Test]
        public void TryGetResizeTarget_SmallBuildingUsesBuildingTarget()
        {
            var def = Uninitialized<ThingDef>();
            def.building = Uninitialized<BuildingProperties>();
            def.graphicData = Uninitialized<GraphicData>();
            def.graphicData.drawSize = new Vector2(4f, 4f); // 4 + 4 = 8 <= 8
            int targetSize;

            var result = TextureResizer.TryGetResizeTarget(texture: null, def: def, targetSize: out targetSize);

            Assert.That(result, Is.True);
            Assert.That(targetSize, Is.EqualTo(256));
        }

        [Test]
        public void TryGetResizeTarget_ApparelUsesApparelTarget()
        {
            var def = Uninitialized<ThingDef>();
            def.apparel = Uninitialized<ApparelProperties>();
            def.graphicData = Uninitialized<GraphicData>();
            def.graphicData.drawSize = new Vector2(1f, 1f);
            int targetSize;

            var result = TextureResizer.TryGetResizeTarget(texture: null, def: def, targetSize: out targetSize);

            Assert.That(result, Is.True);
            Assert.That(targetSize, Is.EqualTo(128));
        }

        [Test]
        public void TryGetResizeTarget_WeaponUsesWeaponTarget()
        {
            var def = Uninitialized<ThingDef>();
            def.category = ThingCategory.Item;
            def.tools = new List<Tool> { Uninitialized<Tool>() };
            def.graphicData = Uninitialized<GraphicData>();
            def.graphicData.drawSize = new Vector2(1.5f, 1.5f);
            int targetSize;

            var result = TextureResizer.TryGetResizeTarget(texture: null, def: def, targetSize: out targetSize);

            Assert.That(result, Is.True);
            Assert.That(targetSize, Is.EqualTo(128));
        }

        [Test]
        public void TryGetResizeTarget_PawnUsesPawnTarget()
        {
            var def = Uninitialized<ThingDef>();
            def.race = Uninitialized<RaceProperties>();
            def.graphicData = Uninitialized<GraphicData>();
            def.graphicData.drawSize = new Vector2(1.5f, 1.5f);
            int targetSize;

            var result = TextureResizer.TryGetResizeTarget(texture: null, def: def, targetSize: out targetSize);

            Assert.That(result, Is.True);
            Assert.That(targetSize, Is.EqualTo(256));
        }

        [Test]
        public void TryGetResizeTarget_RaceWithoutGraphicDataUsesPawnTarget()
        {
            // 原版種族 Def 沒有 graphicData，身體圖形的 drawSize 由掃描階段依生命階段篩選。
            var def = Uninitialized<ThingDef>();
            def.race = Uninitialized<RaceProperties>();
            int targetSize;

            var result = TextureResizer.TryGetResizeTarget(texture: null, def: def, targetSize: out targetSize);

            Assert.That(result, Is.True);
            Assert.That(targetSize, Is.EqualTo(256));
        }

        [Test]
        public void TryGetResizeTarget_ItemUsesItemTarget()
        {
            var def = Uninitialized<ThingDef>();
            def.category = ThingCategory.Item;
            def.graphicData = Uninitialized<GraphicData>();
            def.graphicData.drawSize = new Vector2(1f, 1f);
            int targetSize;

            var result = TextureResizer.TryGetResizeTarget(texture: null, def: def, targetSize: out targetSize);

            Assert.That(result, Is.True);
            Assert.That(targetSize, Is.EqualTo(128));
        }

        [Test]
        public void TryGetResizeTarget_PlantUsesPlantTarget()
        {
            var def = Uninitialized<ThingDef>();
            def.category = ThingCategory.Plant;
            def.plant = Uninitialized<PlantProperties>();
            def.thingClass = typeof(Plant);
            def.graphicData = Uninitialized<GraphicData>();
            def.graphicData.drawSize = new Vector2(1f, 1f);
            int targetSize;

            var result = TextureResizer.TryGetResizeTarget(texture: null, def: def, targetSize: out targetSize);

            Assert.That(result, Is.True);
            Assert.That(targetSize, Is.EqualTo(128));
        }

        [Test]
        public void TryGetResizeTarget_TreeUsesTreeTarget()
        {
            var def = Uninitialized<ThingDef>();
            def.category = ThingCategory.Plant;
            def.plant = Uninitialized<PlantProperties>();
            def.plant.harvestTag = "Wood";
            def.thingClass = typeof(Plant);
            def.graphicData = Uninitialized<GraphicData>();
            def.graphicData.drawSize = new Vector2(2f, 2f);
            int targetSize;

            var result = TextureResizer.TryGetResizeTarget(texture: null, def: def, targetSize: out targetSize);

            Assert.That(result, Is.True);
            Assert.That(targetSize, Is.EqualTo(256));
        }

        [Test]
        public void TryGetResizeTarget_LargeThingIsNotSelected()
        {
            var def = Uninitialized<ThingDef>();
            def.building = Uninitialized<BuildingProperties>();
            def.graphicData = Uninitialized<GraphicData>();
            def.graphicData.drawSize = new Vector2(5f, 4f); // 5 + 4 = 9 > 8
            int targetSize;

            var result = TextureResizer.TryGetResizeTarget(texture: null, def: def, targetSize: out targetSize);

            Assert.That(result, Is.False);
            Assert.That(targetSize, Is.EqualTo(0));
        }

        [Test]
        public void TryGetResizeTarget_WithoutGraphicDataIsNotSelected()
        {
            int targetSize;

            var result = TextureResizer.TryGetResizeTarget(
                texture: null, def: Uninitialized<ThingDef>(), targetSize: out targetSize);

            Assert.That(result, Is.False);
            Assert.That(targetSize, Is.EqualTo(0));
        }

        [Test]
        public void DestroyTemporaryUnityObject_WithNullIsNoOp()
        {
            Assert.DoesNotThrow(() => TextureResizer.DestroyTemporaryUnityObject(null));
        }
    }
}
