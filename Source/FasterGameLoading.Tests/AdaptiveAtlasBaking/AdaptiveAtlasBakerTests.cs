using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;
using UnityEngine;
using Verse;

namespace FasterGameLoading.Tests.AdaptiveAtlasBaking
{
    [TestFixture]
    public class AdaptiveAtlasBakerTests
    {
        private static readonly FieldInfo BuildQueueField = AccessTools.Field(typeof(GlobalTextureAtlasManager), "buildQueue");
        private static readonly FieldInfo BuildQueueMasksField = AccessTools.Field(typeof(GlobalTextureAtlasManager), "buildQueueMasks");
        private static readonly FieldInfo StaticTextureAtlasesField = AccessTools.Field(typeof(GlobalTextureAtlasManager), "staticTextureAtlases");

        private static Harmony harmony;
        private static bool forceTryBakeSingleBatchFailure = false;
        private static int tryBakeSingleBatchCallCount;

        private object previousBuildQueue;
        private object previousBuildQueueMasks;
        private object previousStaticAtlases;
        private List<float> previousHistoricalBakeSpeeds;

        public static class MockTextureHelper
        {
            public static readonly IDictionary<Texture2D, (int width, int height, string name, TextureFormat format, int mips)> TextureProps = new Dictionary<Texture2D, (int width, int height, string name, TextureFormat format, int mips)>();

            public static Texture2D CreateTexture(int width = 256, int height = 256, string name = "TestTex", TextureFormat format = TextureFormat.RGBA32, int mips = 1)
            {
                var tex = (Texture2D)FormatterServices.GetUninitializedObject(typeof(Texture2D));
                TextureProps[tex] = (width, height, name, format, mips);
                return tex;
            }

            public static int GetWidth(Texture tex)
            {
                if (tex is Texture2D t && TextureProps.TryGetValue(t, out var p)) return p.width;
                return 256;
            }

            public static int GetHeight(Texture tex)
            {
                if (tex is Texture2D t && TextureProps.TryGetValue(t, out var p)) return p.height;
                return 256;
            }

            public static string GetName(UnityEngine.Object obj)
            {
                if (obj is Texture2D t && TextureProps.TryGetValue(t, out var p)) return p.name;
                return "MockTex";
            }

            public static TextureFormat GetFormat(Texture2D tex)
            {
                if (tex != null && TextureProps.TryGetValue(tex, out var p)) return p.format;
                return TextureFormat.RGBA32;
            }

            public static int GetMipmapCount(Texture tex)
            {
                if (tex is Texture2D t && TextureProps.TryGetValue(t, out var p)) return p.mips;
                return 1;
            }

            public static bool MockOpEquality(UnityEngine.Object x, UnityEngine.Object y)
            {
                return ReferenceEquals(x, y);
            }

            public static bool MockOpInequality(UnityEngine.Object x, UnityEngine.Object y)
            {
                return !ReferenceEquals(x, y);
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var mockGetWidth = AccessTools.Method(typeof(MockTextureHelper), nameof(GetWidth));
                var mockGetHeight = AccessTools.Method(typeof(MockTextureHelper), nameof(GetHeight));
                var mockOpEquality = AccessTools.Method(typeof(MockTextureHelper), nameof(MockOpEquality));
                var mockOpInequality = AccessTools.Method(typeof(MockTextureHelper), nameof(MockOpInequality));

                foreach (var inst in instructions)
                {
                    if ((inst.opcode == OpCodes.Call || inst.opcode == OpCodes.Callvirt) && inst.operand is MethodInfo m)
                    {
                        if (string.Equals(m.Name, "get_width", StringComparison.Ordinal))
                        {
                            yield return new CodeInstruction(OpCodes.Call, mockGetWidth);
                            continue;
                        }
                        if (string.Equals(m.Name, "get_height", StringComparison.Ordinal))
                        {
                            yield return new CodeInstruction(OpCodes.Call, mockGetHeight);
                            continue;
                        }
                        if (string.Equals(m.Name, "op_Equality", StringComparison.Ordinal))
                        {
                            yield return new CodeInstruction(OpCodes.Call, mockOpEquality);
                            continue;
                        }
                        if (string.Equals(m.Name, "op_Inequality", StringComparison.Ordinal))
                        {
                            yield return new CodeInstruction(OpCodes.Call, mockOpInequality);
                            continue;
                        }
                    }
                    yield return inst;
                }
            }



            public static bool MockIsInMainThread(ref bool __result)
            {
                __result = false;
                return false;
            }
        }




        [OneTimeSetUp]
        // MA0051: 這是一份「要打哪些補丁」的線性清單，每一項都是獨立的一次
        // harmony.Patch 呼叫。拆成多個方法只會讓讀者得跨方法重組同一份清單，
        // 抽出的單元也取不到比 Part1／Part2 更誠實的名字，故就地抑制。
#pragma warning disable MA0051
        public void OneTimeSetUp()
#pragma warning restore MA0051
        {
            harmony = new Harmony("FasterGameLoading.Tests.AdaptiveAtlasBaker");

            // 0. Patch FGLLog.Emit to safely avoid Verse.UnityData initialization in tests
            var emitMethod = AccessTools.Method(typeof(FGLLog), "Emit");
            var prefixSkip = AccessTools.Method(typeof(AdaptiveAtlasBakerTests), nameof(PrefixSkip));
            if (emitMethod != null)
            {
                harmony.Patch(emitMethod, prefix: new HarmonyMethod(prefixSkip));
            }

            // 1. Skip vanilla insertion
            var insertVanilla = AccessTools.Method(typeof(AdaptiveAtlasBaker), "InsertVanillaStaticAtlasEntries");
            harmony.Patch(insertVanilla, prefix: new HarmonyMethod(prefixSkip));

            // 2. Skip destroy atlas textures
            var destroyTextures = AccessTools.Method(typeof(AdaptiveAtlasBaker), "DestroyAtlasTextures");
            harmony.Patch(destroyTextures, prefix: new HarmonyMethod(prefixSkip));

            // 3. Patch StaticTextureAtlas constructor and methods to avoid native mesh/texture calls
            var atlasCtor = AccessTools.Constructor(typeof(StaticTextureAtlas), new Type[] { typeof(TextureAtlasGroupKey) });
            if (atlasCtor != null)
            {
                harmony.Patch(atlasCtor, prefix: new HarmonyMethod(prefixSkip));
            }

            var atlasInsert = AccessTools.Method(typeof(StaticTextureAtlas), "Insert", new Type[] { typeof(Texture2D), typeof(Texture2D) });
            if (atlasInsert != null)
            {
                harmony.Patch(atlasInsert, prefix: new HarmonyMethod(prefixSkip));
            }

            var atlasBake = AccessTools.Method(typeof(StaticTextureAtlas), "Bake", new Type[] { typeof(bool) });
            if (atlasBake != null)
            {
                harmony.Patch(atlasBake, prefix: new HarmonyMethod(prefixSkip));
            }

            var atlasBuildMeshes = AccessTools.Method(typeof(StaticTextureAtlas), "BuildMeshesForUvs", new Type[] { typeof(Rect[]) });
            if (atlasBuildMeshes != null)
            {
                harmony.Patch(atlasBuildMeshes, prefix: new HarmonyMethod(prefixSkip));
            }

            // 4. Hook TryBakeSingleBatch for controllable failure simulation
            var tryBakeMethod = AccessTools.Method(typeof(AdaptiveAtlasBaker), "TryBakeSingleBatch");
            if (tryBakeMethod != null)
            {
                var prefixTryBake = AccessTools.Method(typeof(AdaptiveAtlasBakerTests), nameof(PrefixTryBakeSingleBatch));
                harmony.Patch(tryBakeMethod, prefix: new HarmonyMethod(prefixTryBake));
            }

            // 5. Transpile the iterator MoveNext to safely replace texture width/height
            // 以前綴比對定位協程狀態機型別，而非寫死 d__0：
            // 編譯器產生的序號是該迭代器方法在型別中的序位，只要在它之前新增
            // 任何巢狀型別或迭代器就會位移，寫死序號會讓補丁靜默失效。
            var iteratorType = Array.Find(
                typeof(AdaptiveAtlasBaker).GetNestedTypes(BindingFlags.NonPublic),
                t => t.Name.StartsWith("<PerformAdaptiveStaticAtlasBake>d__", StringComparison.Ordinal));
            var transpilerMethod = AccessTools.Method(typeof(MockTextureHelper), nameof(MockTextureHelper.Transpiler));
            if (iteratorType != null)
            {
                var moveNext = AccessTools.Method(iteratorType, "MoveNext");
                harmony.Patch(moveNext, transpiler: new HarmonyMethod(transpilerMethod));
            }

            // 6. CommitBakedAtlases 會存取 GlobalTextureAtlasManager 的欄位。
            // Krafs.Publicizer 只在編譯期把它們開放，執行期載入的仍是原始 Verse 組件，
            // 直接存取會拋 FieldAccessException。經 Harmony 轉譯的方法會以
            // skipVisibility 的 DynamicMethod 重新產生，才得以繞過該檢查 ——
            // 協程 MoveNext 一直是靠這個機制運作，方法抽出後同樣需要掛上。
            var commitBaked = AccessTools.Method(typeof(AdaptiveAtlasBaker), "CommitBakedAtlases");
            if (commitBaked != null)
            {
                harmony.Patch(commitBaked, transpiler: new HarmonyMethod(transpilerMethod));
            }
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            harmony.UnpatchAll("FasterGameLoading.Tests.AdaptiveAtlasBaker");
            MockTextureHelper.TextureProps.Clear();
        }

        private static bool PrefixSkip()
        {
            return false;
        }

        // 只宣告 __result：此 prefix 不需要讀取目標方法的任何引數，
        // 逐一列出反而會讓測試綁死 TryBakeSingleBatch 的私有簽章
        // （Harmony 依名稱比對參數，簽章一變更即無法套用補丁）。
        private static bool PrefixTryBakeSingleBatch(ref bool __result)
        {
#pragma warning disable MA0045 // 測試輔助工具的同步輸出，無需非同步
            TestContext.Progress.WriteLine($"PrefixTryBakeSingleBatch called! force={forceTryBakeSingleBatchFailure}");
#pragma warning restore MA0045
            tryBakeSingleBatchCallCount++;
            if (forceTryBakeSingleBatchFailure)
            {
                __result = false;
                return false;
            }
            return true;
        }














        [SetUp]
        public void SetUp()
        {
            forceTryBakeSingleBatchFailure = false;
            tryBakeSingleBatchCallCount = 0;

            previousBuildQueue = BuildQueueField?.GetValue(null);
            previousBuildQueueMasks = BuildQueueMasksField?.GetValue(null);
            previousStaticAtlases = StaticTextureAtlasesField?.GetValue(null);
            previousHistoricalBakeSpeeds = AdaptiveAtlasBaker.BakeSpeedHistory?.ToList();

            BuildQueueField?.SetValue(null, new Dictionary<TextureAtlasGroupKey, (List<Texture2D>, HashSet<Texture2D>)>());
            BuildQueueMasksField?.SetValue(null, new Dictionary<Texture2D, Texture2D>());
            StaticTextureAtlasesField?.SetValue(null, new List<StaticTextureAtlas>());
            AdaptiveAtlasBaker.BakeSpeedHistory = new List<float>();
        }


        [TearDown]
        public void TearDown()
        {
            forceTryBakeSingleBatchFailure = false;

            BuildQueueField?.SetValue(null, previousBuildQueue);
            BuildQueueMasksField?.SetValue(null, previousBuildQueueMasks);
            StaticTextureAtlasesField?.SetValue(null, previousStaticAtlases);
            AdaptiveAtlasBaker.BakeSpeedHistory = previousHistoricalBakeSpeeds ?? new List<float>();
            MockTextureHelper.TextureProps.Clear();
        }

        [Test]
        public void PerformAdaptiveStaticAtlasBake_ReturnsCoroutineEntryPoint()
        {
            var iterator = AdaptiveAtlasBaker.PerformAdaptiveStaticAtlasBake(null);
            Assert.That(iterator, Is.Not.Null);
        }

        [Test]
        public void PerformAdaptiveStaticAtlasBake_WithoutHistory_UsesInitialEstimate()
        {
            AdaptiveAtlasBaker.BakeSpeedHistory.Clear();

            var iterator = AdaptiveAtlasBaker.PerformAdaptiveStaticAtlasBake(delayedActions: null);
            while (iterator.MoveNext()) { }

            Assert.That(AdaptiveAtlasBaker.BakeSpeedHistory.Count, Is.EqualTo(1));
            Assert.That(AdaptiveAtlasBaker.BakeSpeedHistory[0], Is.EqualTo(2_000_000f));
        }

        [Test]
        public void PerformAdaptiveStaticAtlasBake_WithHistory_CalculatesWeightedMovingAverage()
        {
            // Initial history with 1,000,000f
            AdaptiveAtlasBaker.BakeSpeedHistory = new List<float> { 1_000_000f };

            var iterator = AdaptiveAtlasBaker.PerformAdaptiveStaticAtlasBake(delayedActions: null);
            while (iterator.MoveNext()) { }

            // With single history 1,000,000f, weighted average is 1,000,000f * 0.4 / 0.4 = 1,000,000f
            Assert.That(AdaptiveAtlasBaker.BakeSpeedHistory[0], Is.EqualTo(1_000_000f));

            // With 2 history entries: 1,000,000f and 2,000,000f
            // weightedSum = 1,000,000 * 0.4 + 2,000,000 * 0.3 = 1,000,000
            // weightSum = 0.4 + 0.3 = 0.7
            // expected = 1,000,000 / 0.7 = 1428571.4f
            AdaptiveAtlasBaker.BakeSpeedHistory = new List<float> { 1_000_000f, 2_000_000f };
            var iterator2 = AdaptiveAtlasBaker.PerformAdaptiveStaticAtlasBake(delayedActions: null);
            while (iterator2.MoveNext()) { }

            float expectedSpeed = (1_000_000f * AdaptiveAtlasBaker.BakeSpeedWeights[0] + 2_000_000f * AdaptiveAtlasBaker.BakeSpeedWeights[1]) / (AdaptiveAtlasBaker.BakeSpeedWeights[0] + AdaptiveAtlasBaker.BakeSpeedWeights[1]);
            Assert.That(AdaptiveAtlasBaker.BakeSpeedHistory[0], Is.EqualTo(expectedSpeed).Within(1f));
        }

        [Test]
        public void PerformAdaptiveStaticAtlasBake_ClearsBuildQueueAndMasksOnCompletion()
        {
            var queue = new Dictionary<TextureAtlasGroupKey, (List<Texture2D>, HashSet<Texture2D>)>();
            var key = new TextureAtlasGroupKey { hasMask = false };
            var mainTex = MockTextureHelper.CreateTexture(64, 64, "Tex1");
            queue[key] = (new List<Texture2D> { mainTex }, new HashSet<Texture2D>());
            BuildQueueField.SetValue(null, queue);

            var masks = new Dictionary<Texture2D, Texture2D>
            {
                [mainTex] = null,
            };
            BuildQueueMasksField.SetValue(null, masks);

            var iterator = AdaptiveAtlasBaker.PerformAdaptiveStaticAtlasBake(delayedActions: null);
            while (iterator.MoveNext()) { }

            var finalQueue = (IDictionary)BuildQueueField.GetValue(null);
            var finalMasks = (IDictionary)BuildQueueMasksField.GetValue(null);

            Assert.That(finalQueue.Count, Is.EqualTo(0));
            Assert.That(finalMasks.Count, Is.EqualTo(0));
        }

        [Test]
        public void PerformAdaptiveStaticAtlasBake_WithFullHistory_DoesNotExceedHistorySize()
        {
            AdaptiveAtlasBaker.BakeSpeedHistory = new List<float> { 1_500_000f, 1_600_000f, 1_700_000f, 1_800_000f };

            var queue = new Dictionary<TextureAtlasGroupKey, (List<Texture2D>, HashSet<Texture2D>)>();
            var key = new TextureAtlasGroupKey { hasMask = true };
            var mainTex1 = MockTextureHelper.CreateTexture(128, 128, "Tex1");
            var maskTex1 = MockTextureHelper.CreateTexture(128, 128, "Mask1");
            var mainTex2 = MockTextureHelper.CreateTexture(128, 128, "Tex2");
            var maskTex2 = MockTextureHelper.CreateTexture(128, 128, "Mask2");
            queue[key] = (new List<Texture2D> { mainTex1, mainTex2 }, new HashSet<Texture2D>());
            BuildQueueField.SetValue(null, queue);

            var masks = new Dictionary<Texture2D, Texture2D>
            {
                [mainTex1] = maskTex1,
                [mainTex2] = maskTex2,
            };
            BuildQueueMasksField.SetValue(null, masks);

            var iterator = AdaptiveAtlasBaker.PerformAdaptiveStaticAtlasBake(delayedActions: null);
            while (iterator.MoveNext()) { }

            Assert.That(AdaptiveAtlasBaker.BakeSpeedHistory.Count, Is.EqualTo(AdaptiveAtlasBaker.BakeSpeedHistorySize));
        }

        [Test]
        public void PerformAdaptiveStaticAtlasBake_SmallTexturesShareOneAtlas()
        {
            // 四張 256×256 共 262,144 像素，低於 1024×1024 的 slice 下限：
            // 必須合併成一張圖集，而不是各自切成小圖集（舊參數 256×256 起跳會拆成四張）。
            var queue = new Dictionary<TextureAtlasGroupKey, (List<Texture2D>, HashSet<Texture2D>)>();
            var key = new TextureAtlasGroupKey { hasMask = false };
            var textures = new List<Texture2D>();
            for (int i = 0; i < 4; i++)
            {
                textures.Add(MockTextureHelper.CreateTexture(256, 256, "Small" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            queue[key] = (textures, new HashSet<Texture2D>());
            BuildQueueField.SetValue(null, queue);

            var iterator = AdaptiveAtlasBaker.PerformAdaptiveStaticAtlasBake(delayedActions: null);
            while (iterator.MoveNext()) { }

            Assert.That(tryBakeSingleBatchCallCount, Is.EqualTo(1));
        }

        [Test]
        public void PerformAdaptiveStaticAtlasBake_WhenBatchBakeFails_MarksFailedAndEarlyExits()
        {
            var queue = new Dictionary<TextureAtlasGroupKey, (List<Texture2D>, HashSet<Texture2D>)>();
            var key = new TextureAtlasGroupKey { hasMask = false };
            var mainTex = MockTextureHelper.CreateTexture(64, 64, "FailTex");
            queue[key] = (new List<Texture2D> { mainTex }, new HashSet<Texture2D>());
            BuildQueueField.SetValue(null, queue);

            forceTryBakeSingleBatchFailure = true;

            var iterator = AdaptiveAtlasBaker.PerformAdaptiveStaticAtlasBake(delayedActions: null);
            while (iterator.MoveNext()) { }

            Assert.That(AdaptiveAtlasBaker.LastBakeFailed, Is.True);
            // On early failure, SessionCache speed is not recorded
            Assert.That(AdaptiveAtlasBaker.BakeSpeedHistory.Count, Is.EqualTo(0));
        }
    }
}


