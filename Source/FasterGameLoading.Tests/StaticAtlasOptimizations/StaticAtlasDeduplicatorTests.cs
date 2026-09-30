using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Verse;

namespace FasterGameLoading.Tests.StaticAtlasOptimizations
{
    /// <summary>
    /// 以字串代替 Texture2D 測試 <see cref="StaticAtlasDeduplicator.RemoveCopiesOfBuildingTextures{T}"/>。
    /// Tests <see cref="StaticAtlasDeduplicator.RemoveCopiesOfBuildingTextures{T}"/> with strings in place of Texture2D.
    /// </summary>
    [TestFixture]
    public class StaticAtlasDeduplicatorTests
    {
        private static readonly (string, TextureAtlasGroup)[] ExpectedTableAndLamp =
        {
            ("table", TextureAtlasGroup.Item),
            ("lamp", TextureAtlasGroup.Misc),
        };

        private static readonly string[] BuildingTextures = { "table", "lamp" };
        private static readonly string[] ItemAfterRemoval = { "steel" };
        private static readonly string[] MiscAfterRemoval = { "bloodsplat" };
        private static readonly string[] OrderAfterRemoval = { "x", "y", "z" };

        private static TextureAtlasGroupKey Key(TextureAtlasGroup group, bool masked = false)
            => new TextureAtlasGroupKey { group = group, hasMask = masked };

        private static Dictionary<TextureAtlasGroupKey, (List<string>, HashSet<string>)> Queue(
            params (TextureAtlasGroupKey key, string[] textures)[] groups)
        {
            var queue = new Dictionary<TextureAtlasGroupKey, (List<string>, HashSet<string>)>();
            foreach (var (key, textures) in groups)
            {
                queue[key] = (textures.ToList(), new HashSet<string>(textures, StringComparer.Ordinal));
            }
            return queue;
        }

        [Test]
        public void RemovesItemAndMiscCopiesOfBuildingTextures()
        {
            var queue = Queue(
                (Key(TextureAtlasGroup.Building), BuildingTextures),
                (Key(TextureAtlasGroup.Item), new[] { "table", "steel" }),
                (Key(TextureAtlasGroup.Misc), new[] { "lamp", "bloodsplat" }));

            var removed = StaticAtlasDeduplicator.RemoveCopiesOfBuildingTextures(queue);

            Assert.That(removed, Is.EquivalentTo(ExpectedTableAndLamp));
            Assert.That(queue[Key(TextureAtlasGroup.Building)].Item1, Is.EqualTo(BuildingTextures));
            Assert.That(queue[Key(TextureAtlasGroup.Item)].Item1, Is.EqualTo(ItemAfterRemoval));
            Assert.That(queue[Key(TextureAtlasGroup.Item)].Item2, Is.EquivalentTo(ItemAfterRemoval));
            Assert.That(queue[Key(TextureAtlasGroup.Misc)].Item1, Is.EqualTo(MiscAfterRemoval));
            Assert.That(queue[Key(TextureAtlasGroup.Misc)].Item2, Is.EquivalentTo(MiscAfterRemoval));
        }

        [Test]
        public void MatchesOnlyTheSameMaskState()
        {
            var queue = Queue(
                (Key(TextureAtlasGroup.Building), new[] { "unmasked" }),
                (Key(TextureAtlasGroup.Item, masked: true), new[] { "unmasked", "masked" }),
                (Key(TextureAtlasGroup.Building, masked: true), new[] { "masked" }));

            var removed = StaticAtlasDeduplicator.RemoveCopiesOfBuildingTextures(queue);

            Assert.That(removed, Is.EqualTo(new[] { ("masked", TextureAtlasGroup.Item) }));
            Assert.That(queue[Key(TextureAtlasGroup.Item, masked: true)].Item1, Is.EqualTo(new[] { "unmasked" }));
        }

        [Test]
        public void RemovesAGroupThatIsLeftEmpty()
        {
            // 原版不會有空群組，拿到空群組時會試著烘焙一張空圖集。
            // Vanilla never has an empty group, and would try to bake an empty atlas from one.
            var queue = Queue(
                (Key(TextureAtlasGroup.Building), new[] { "table" }),
                (Key(TextureAtlasGroup.Item), new[] { "table" }));

            StaticAtlasDeduplicator.RemoveCopiesOfBuildingTextures(queue);

            Assert.That(queue.ContainsKey(Key(TextureAtlasGroup.Item)), Is.False);
            Assert.That(queue.ContainsKey(Key(TextureAtlasGroup.Building)), Is.True);
        }

        [Test]
        public void KeepsTheOrderOfTheRemainingTextures()
        {
            var queue = Queue(
                (Key(TextureAtlasGroup.Building), new[] { "a", "b" }),
                (Key(TextureAtlasGroup.Misc), new[] { "x", "a", "y", "b", "z" }));

            StaticAtlasDeduplicator.RemoveCopiesOfBuildingTextures(queue);

            Assert.That(queue[Key(TextureAtlasGroup.Misc)].Item1, Is.EqualTo(OrderAfterRemoval));
        }

        [Test]
        public void WithoutABuildingGroupRemovesNothing()
        {
            var queue = Queue(
                (Key(TextureAtlasGroup.Item), new[] { "table" }),
                (Key(TextureAtlasGroup.Misc), new[] { "table" }));

            var removed = StaticAtlasDeduplicator.RemoveCopiesOfBuildingTextures(queue);

            Assert.That(removed, Is.Empty);
            Assert.That(queue[Key(TextureAtlasGroup.Item)].Item1, Is.EqualTo(new[] { "table" }));
            Assert.That(queue[Key(TextureAtlasGroup.Misc)].Item1, Is.EqualTo(new[] { "table" }));
        }

        [Test]
        public void LeavesGroupsOtherThanItemAndMiscAlone()
        {
            var queue = Queue(
                (Key(TextureAtlasGroup.Building), new[] { "shared" }),
                (Key(TextureAtlasGroup.Plant), new[] { "shared" }),
                (Key(TextureAtlasGroup.Filth), new[] { "shared" }));

            var removed = StaticAtlasDeduplicator.RemoveCopiesOfBuildingTextures(queue);

            Assert.That(removed, Is.Empty);
            Assert.That(queue[Key(TextureAtlasGroup.Plant)].Item1, Is.EqualTo(new[] { "shared" }));
            Assert.That(queue[Key(TextureAtlasGroup.Filth)].Item1, Is.EqualTo(new[] { "shared" }));
        }

        [Test]
        public void ASecondCallFindsNothingToRemove()
        {
            var queue = Queue(
                (Key(TextureAtlasGroup.Building), BuildingTextures),
                (Key(TextureAtlasGroup.Item), new[] { "table", "steel" }));

            StaticAtlasDeduplicator.RemoveCopiesOfBuildingTextures(queue);
            var second = StaticAtlasDeduplicator.RemoveCopiesOfBuildingTextures(queue);

            Assert.That(second, Is.Empty);
            Assert.That(queue[Key(TextureAtlasGroup.Item)].Item1, Is.EqualTo(ItemAfterRemoval));
        }
    }
}
