using System.Collections.Generic;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine;

namespace FasterGameLoading.Tests.TextureDownscaler
{
    [TestFixture]
    public class LoadedTextureRegistryTests
    {
        private bool previousStaticAtlasesBaking;

        private static Texture2D NewDetachedTexture()
        {
            return (Texture2D)FormatterServices.GetUninitializedObject(typeof(Texture2D));
        }

        [SetUp]
        public void SetUp()
        {
            previousStaticAtlasesBaking = FasterGameLoadingSettings.StaticAtlasesBaking;
            LoadedTextureRegistry.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            FasterGameLoadingSettings.StaticAtlasesBaking = previousStaticAtlasesBaking;
            SessionLifecycle.Raise(LifecyclePhase.LanguageReloading);
        }

        [Test]
        public void Record_ThenLookupBothWays()
        {
            var texture = NewDetachedTexture();
            LoadedTextureRegistry.Record("C:/Mods/Test/Textures/Thing.png", texture);

            Assert.That(LoadedTextureRegistry.TryGetPath(texture, out var path), Is.True);
            Assert.That(path, Is.EqualTo("C:/Mods/Test/Textures/Thing.png"));
            Assert.That(LoadedTextureRegistry.Snapshot(), Is.EqualTo(new[] { new KeyValuePair<Texture2D, string>(texture, "C:/Mods/Test/Textures/Thing.png") }));
        }

        [Test]
        public void Record_RebindingSameTextureUpdatesReverseLookup()
        {
            var texture = NewDetachedTexture();

            LoadedTextureRegistry.Record("first.png", texture);
            LoadedTextureRegistry.Record("second.png", texture);

            Assert.That(LoadedTextureRegistry.TryGetPath(texture, out string resolved), Is.True);
            Assert.That(resolved, Is.EqualTo("second.png"), "同一 Texture2D 換路徑重新登記後，反向查表必須指向新路徑。");
        }

        [Test]
        public void Record_ReplacingTextureAtSamePathDetachesOldEntry()
        {
            var oldTexture = NewDetachedTexture();
            var newTexture = NewDetachedTexture();

            LoadedTextureRegistry.Record("same.png", oldTexture);
            LoadedTextureRegistry.Record("same.png", newTexture);

            Assert.That(LoadedTextureRegistry.TryGetPath(oldTexture, out _), Is.False);
            Assert.That(LoadedTextureRegistry.TryGetPath(newTexture, out string resolved), Is.True);
            Assert.That(resolved, Is.EqualTo("same.png"));
        }

        [Test]
        public void TryGetPath_UnknownTextureReturnsFalse()
        {
            Assert.That(LoadedTextureRegistry.TryGetPath(NewDetachedTexture(), out string resolved), Is.False);
            Assert.That(resolved, Is.Null);
        }

        [Test]
        public void TryGetTexture_UnknownPathReturnsFalse()
        {
            Assert.That(LoadedTextureRegistry.TryGetTexture("never-loaded.png", out var texture), Is.False);
            Assert.That(texture, Is.Null);
        }

        [Test]
        public void Forget_DropsPathLookup()
        {
            LoadedTextureRegistry.Record("gone.png", NewDetachedTexture());

            LoadedTextureRegistry.Forget("gone.png");

            Assert.That(LoadedTextureRegistry.Snapshot(), Is.Empty);
        }

        [Test]
        public void MarkSkipBakingIfProtected_OnlyMarksProtectedModTexturesWhenAdaptiveBakingIsOn()
        {
            ProtectedMods.SetProtectedTextureRootsForTests("C:/Mods/TargetMod");
            var protectedTexture = NewDetachedTexture();
            var otherTexture = NewDetachedTexture();

            FasterGameLoadingSettings.StaticAtlasesBaking = false;
            LoadedTextureRegistry.MarkSkipBakingIfProtected("C:/Mods/TargetMod/Textures/Alien/head.png", protectedTexture);
            Assert.That(LoadedTextureRegistry.IsSkippedForBaking(protectedTexture), Is.False);

            FasterGameLoadingSettings.StaticAtlasesBaking = true;
            LoadedTextureRegistry.MarkSkipBakingIfProtected("C:/Mods/TargetMod/Textures/Alien/head.png", protectedTexture);
            LoadedTextureRegistry.MarkSkipBakingIfProtected("C:/Mods/OtherMod/Textures/rock.png", otherTexture);

            Assert.That(LoadedTextureRegistry.IsSkippedForBaking(protectedTexture), Is.True);
            Assert.That(LoadedTextureRegistry.IsSkippedForBaking(otherTexture), Is.False);
            Assert.That(LoadedTextureRegistry.IsSkippedForBaking(null), Is.False);
        }

        [Test]
        public void Clear_DropsEverything()
        {
            var texture = NewDetachedTexture();
            LoadedTextureRegistry.Record("a.png", texture);

            LoadedTextureRegistry.Clear();

            Assert.That(LoadedTextureRegistry.Snapshot(), Is.Empty);
            Assert.That(LoadedTextureRegistry.TryGetTexture("a.png", out _), Is.False);
        }
    }
}
