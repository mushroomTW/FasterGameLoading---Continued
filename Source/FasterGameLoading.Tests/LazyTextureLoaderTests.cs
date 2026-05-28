using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace FasterGameLoading.Tests
{
    [TestFixture]
    public class LazyTextureLoaderTests
    {
        [SetUp]
        public void SetUp()
        {
            // 在每個測試前重置 ExcludePaths 以避免測試互相影響
            LazyTextureLoader.ExcludePaths.Clear();
            LazyTextureLoader.ExcludePaths.Add("UI/");
            LazyTextureLoader.ExcludePaths.Add("Icon");
            LazyTextureLoader.ExcludePaths.Add("bionicicons");
        }

        [Test]
        public void TestShouldLazyLoad_WithDefaultExclusions_ReturnsFalse()
        {
            // 預設排除的路徑應回傳 false
            Assert.IsFalse(LazyTextureLoader.ShouldLazyLoad("Textures/UI/MainButton.png"));
            Assert.IsFalse(LazyTextureLoader.ShouldLazyLoad("Textures/Icons/PawnIcon.png"));
            Assert.IsFalse(LazyTextureLoader.ShouldLazyLoad("Textures/BionicIcons/Heart.png"));
        }

        [Test]
        public void TestShouldLazyLoad_WithTargetPaths_ReturnsTrue()
        {
            // 遊戲世界資源路徑應回傳 true
            Assert.IsTrue(LazyTextureLoader.ShouldLazyLoad("Textures/Things/Building/Table.png"));
            Assert.IsTrue(LazyTextureLoader.ShouldLazyLoad("Textures/Pawn/Human/Body.png"));
            Assert.IsTrue(LazyTextureLoader.ShouldLazyLoad("Textures/Terrain/Floor/Stone.png"));
        }

        [Test]
        public void TestShouldLazyLoad_WithNonTargetPaths_ReturnsFalse()
        {
            // 既不在排除名單，也不符合主要 Def 物件資源路徑的，應回傳 false
            Assert.IsFalse(LazyTextureLoader.ShouldLazyLoad("Textures/Other/RandomAsset.png"));
        }

        [Test]
        public void TestRegisterExcludePath_AddsToExcludePathsAndWorks()
        {
            // 測試註冊自訂排除關鍵字
            string customEx = "CustomModPrefix";
            
            // 原本應該可以延遲載入（因為包含 /Things/ 且不包含 CustomModPrefix）
            Assert.IsTrue(LazyTextureLoader.ShouldLazyLoad("Textures/Things/CustomModPrefix/Table.png"));

            // 註冊排除關鍵字
            LazyTextureLoader.RegisterExcludePath(customEx);

            // 驗證關鍵字已被加入，且該路徑現在不會進行延遲載入（回傳 false）
            Assert.IsTrue(LazyTextureLoader.ExcludePaths.Contains(customEx));
            Assert.IsFalse(LazyTextureLoader.ShouldLazyLoad("Textures/Things/CustomModPrefix/Table.png"));
        }
    }
}
