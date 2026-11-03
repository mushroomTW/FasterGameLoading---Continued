using System.Collections.Generic;
using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;
using UnityEngine;
using Verse;

namespace FasterGameLoading.Tests.DelayGraphicAndIconLoading;
[TestFixture]
public class GraphicData_Init_PatchTests
{
    private const string HarmonyId = "FasterGameLoading.Tests.GraphicData_Init_PatchTests";
    private static Harmony harmony;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        harmony = new Harmony(HarmonyId);

        // Postfix 與 IsSameShaderParameters 讀取 Verse 的私有欄位
        // （GraphicData.cachedGraphic、ShaderParameter.name/value/valueTex/type）。
        // Krafs.Publicizer 只在編譯期開放它們，執行期載入的仍是原始 Verse 組件，
        // 直接存取會拋 FieldAccessException。套上不改動 IL 的轉譯器後，
        // Harmony 會以 skipVisibility 的 DynamicMethod 重新產生方法，才得以繞過該檢查。
        var noOpTranspiler = new HarmonyMethod(
            AccessTools.Method(typeof(GraphicData_Init_PatchTests), nameof(NoOpTranspiler)));
        harmony.Patch(AccessTools.Method(typeof(GraphicData_Init_Patch), nameof(GraphicData_Init_Patch.Postfix)),
            transpiler: noOpTranspiler);
        harmony.Patch(AccessTools.Method(typeof(GraphicData_Init_Patch), "IsSameShaderParameters"),
            transpiler: noOpTranspiler);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
    }

    public static IEnumerable<CodeInstruction> NoOpTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        return instructions;
    }

    [SetUp]
    public void SetUp()
    {
        GraphicData_Init_Patch.savedGraphics.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        GraphicData_Init_Patch.savedGraphics.Clear();
    }

    [Test]
    public void IsSameGraphicData_WithEquivalentDefaultsReturnsTrue()
    {
        var current = new GraphicData();
        var other = new GraphicData();

        Assert.That(GraphicData_Init_Patch.IsSameGraphicData(current, other), Is.True);
    }

    [Test]
    public void IsSameGraphicData_WhenDrawSizeDiffersReturnsFalse()
    {
        var current = new GraphicData { drawSize = new Vector2(1f, 1f) };
        var other = new GraphicData { drawSize = new Vector2(2f, 1f) };

        Assert.That(GraphicData_Init_Patch.IsSameGraphicData(current, other), Is.False);
    }

    // 共用的 Graphic 會從第一份 GraphicData 讀取這些欄位；任一方不同就不得共用。
    private static IEnumerable<TestCaseData> FieldsThatPreventSharing()
    {
        yield return new TestCaseData((System.Action<GraphicData>)(d => d.addTopAltitudeBias = true)).SetName("{m}(addTopAltitudeBias)");
        yield return new TestCaseData((System.Action<GraphicData>)(d => d.ignoreThingDrawColor = true)).SetName("{m}(ignoreThingDrawColor)");
        yield return new TestCaseData((System.Action<GraphicData>)(d => d.maxSnS = new Vector2(0.1f, 0.1f))).SetName("{m}(maxSnS)");
        yield return new TestCaseData((System.Action<GraphicData>)(d => d.offsetSnS = new Vector2(0.1f, 0.1f))).SetName("{m}(offsetSnS)");
        yield return new TestCaseData((System.Action<GraphicData>)(d => d.cornerOverlayPath = "Things/Corner")).SetName("{m}(cornerOverlayPath)");
        yield return new TestCaseData((System.Action<GraphicData>)(d => d.shadowData = (ShadowData)FormatterServices.GetUninitializedObject(typeof(ShadowData)))).SetName("{m}(shadowData)");
        yield return new TestCaseData((System.Action<GraphicData>)(d => d.damageData = (DamageGraphicData)FormatterServices.GetUninitializedObject(typeof(DamageGraphicData)))).SetName("{m}(damageData)");
        yield return new TestCaseData((System.Action<GraphicData>)(d => d.attachments = new List<GraphicData>())).SetName("{m}(attachments)");
        yield return new TestCaseData((System.Action<GraphicData>)(d => d.attachPoints = new List<RimWorld.AttachPoint>())).SetName("{m}(attachPoints)");
        // 1.7 的 Init 依這些欄位設定貼圖重複模式與匯入設定，共用會略過 Init。
        yield return new TestCaseData((System.Action<GraphicData>)(d => d.textureRepeats = true)).SetName("{m}(textureRepeats)");
        yield return new TestCaseData((System.Action<GraphicData>)(d => d.borderMipMaps = true)).SetName("{m}(borderMipMaps)");
        yield return new TestCaseData((System.Action<GraphicData>)(d => d.uncompressed = true)).SetName("{m}(uncompressed)");
    }

    [TestCaseSource(nameof(FieldsThatPreventSharing))]
    public void IsSameGraphicData_WhenFieldSetOnOneSideReturnsFalse(System.Action<GraphicData> setField)
    {
        var current = new GraphicData();
        var other = new GraphicData();
        setField(current);

        Assert.That(GraphicData_Init_Patch.IsSameGraphicData(current, other), Is.False);
        Assert.That(GraphicData_Init_Patch.IsSameGraphicData(other, current), Is.False);
    }

    [Test]
    public void Prefix_ForFirstTexturePathAllowsOriginalInitAndRecordsState()
    {
        var data = new GraphicData { texPath = "Textures/Test" };
        bool state;

        var result = GraphicData_Init_Patch.Prefix(data, out state);

        Assert.That(result, Is.True);
        Assert.That(state, Is.True);
        Assert.That(GraphicData_Init_Patch.savedGraphics.ContainsKey("Textures/Test"), Is.True);
    }

    [Test]
    public void Prefix_ForEmptyTexturePathDoesNotCreateCacheEntry()
    {
        var data = new GraphicData { texPath = string.Empty };
        bool state;

        var result = GraphicData_Init_Patch.Prefix(data, out state);

        Assert.That(result, Is.True);
        Assert.That(state, Is.False);
        Assert.That(GraphicData_Init_Patch.savedGraphics, Is.Empty);
    }

    // ── Postfix：把成功初始化的 GraphicData 登記起來供後續共用 ──

    [Test]
    public void Postfix_RecordsInitializedGraphicDataForReuse()
    {
        var data = NewInitializedGraphicData("Textures/Post");
        GraphicData_Init_Patch.Prefix(data, out bool state);

        GraphicData_Init_Patch.Postfix(data, state);

        Assert.That(GraphicData_Init_Patch.savedGraphics["Textures/Post"], Does.Contain(data));
    }

    [Test]
    public void Postfix_DoesNotRecordWhenPrefixAlreadyServedCachedGraphic()
    {
        var data = NewInitializedGraphicData("Textures/Served");
        GraphicData_Init_Patch.Prefix(data, out _);

        GraphicData_Init_Patch.Postfix(data, __state: false);

        Assert.That(GraphicData_Init_Patch.savedGraphics["Textures/Served"], Is.Empty);
    }

    [Test]
    public void Postfix_DoesNotRecordWhenGraphicWasNotProduced()
    {
        var data = new GraphicData { texPath = "Textures/NoGraphic" };
        GraphicData_Init_Patch.Prefix(data, out bool state);

        GraphicData_Init_Patch.Postfix(data, state);

        Assert.That(GraphicData_Init_Patch.savedGraphics["Textures/NoGraphic"], Is.Empty,
            "沒有實際產生 Graphic 就登記，之後的命中會把 null 當成快取結果回傳。");
    }

    [Test]
    public void Postfix_WithUnknownTexturePathIsIgnored()
    {
        var data = NewInitializedGraphicData("Textures/NeverPrefixed");

        Assert.DoesNotThrow(() => GraphicData_Init_Patch.Postfix(data, __state: true));
        Assert.That(GraphicData_Init_Patch.savedGraphics, Is.Empty);
    }

    [Test]
    public void Postfix_StopsRecordingAfterTenVariantsOfTheSameTexture()
    {
        const string texPath = "Textures/Crowded";
        for (int i = 0; i < 12; i++)
        {
            var data = NewInitializedGraphicData(texPath);
            // 每份的 drawSize 都不同，避免 Prefix 直接以既有項目命中而提早返回。
            data.drawSize = new Vector2(i + 1, 1f);
            GraphicData_Init_Patch.Prefix(data, out bool state);
            GraphicData_Init_Patch.Postfix(data, state);
        }

        Assert.That(GraphicData_Init_Patch.savedGraphics[texPath], Has.Count.EqualTo(10),
            "同一貼圖的變體數量必須設上限，否則長期執行下這個快取會無限增長。");
    }

    // ── shaderParameters 的深度比對 ──

    [Test]
    public void IsSameGraphicData_WithIdenticalShaderParametersReturnsTrue()
    {
        var current = new GraphicData { shaderParameters = [NewShaderParameter("_Tint")] };
        var other = new GraphicData { shaderParameters = [NewShaderParameter("_Tint")] };

        Assert.That(GraphicData_Init_Patch.IsSameGraphicData(current, other), Is.True);
    }

    [Test]
    public void IsSameGraphicData_WhenShaderParameterNameDiffersReturnsFalse()
    {
        var current = new GraphicData { shaderParameters = [NewShaderParameter("_Tint")] };
        var other = new GraphicData { shaderParameters = [NewShaderParameter("_Other")] };

        Assert.That(GraphicData_Init_Patch.IsSameGraphicData(current, other), Is.False);
    }

    [Test]
    public void IsSameGraphicData_WhenShaderParameterValueDiffersReturnsFalse()
    {
        var current = new GraphicData { shaderParameters = [NewShaderParameter("_Tint", new Vector4(1f, 0f, 0f, 1f))] };
        var other = new GraphicData { shaderParameters = [NewShaderParameter("_Tint", new Vector4(0f, 1f, 0f, 1f))] };

        Assert.That(GraphicData_Init_Patch.IsSameGraphicData(current, other), Is.False);
    }

    [Test]
    public void IsSameGraphicData_WhenShaderParameterCountDiffersReturnsFalse()
    {
        var current = new GraphicData { shaderParameters = [NewShaderParameter("_Tint")] };
        var other = new GraphicData { shaderParameters = new List<ShaderParameter>() };

        Assert.That(GraphicData_Init_Patch.IsSameGraphicData(current, other), Is.False);
    }

    [Test]
    public void IsSameGraphicData_WhenOnlyOneSideHasShaderParametersReturnsFalse()
    {
        var current = new GraphicData { shaderParameters = [NewShaderParameter("_Tint")] };
        var other = new GraphicData();

        Assert.That(GraphicData_Init_Patch.IsSameGraphicData(current, other), Is.False);
    }

    [Test]
    public void IsSameGraphicData_WithNullEntriesOnBothSidesReturnsTrue()
    {
        var current = new GraphicData { shaderParameters = [null] };
        var other = new GraphicData { shaderParameters = [null] };

        Assert.That(GraphicData_Init_Patch.IsSameGraphicData(current, other), Is.True);
    }

    [Test]
    public void IsSameGraphicData_WithNullEntryOnOneSideReturnsFalse()
    {
        var current = new GraphicData { shaderParameters = [null] };
        var other = new GraphicData { shaderParameters = [NewShaderParameter("_Tint")] };

        Assert.That(GraphicData_Init_Patch.IsSameGraphicData(current, other), Is.False);
    }

    private static GraphicData NewInitializedGraphicData(string texPath)
    {
        var data = new GraphicData { texPath = texPath };
        // cachedGraphic 在 Verse 中是私有欄位（生產專案靠 Krafs.Publicizer 直接存取，
        // 測試專案沒有），故以反射設定。Graphic 的建構式會碰 Unity 材質，
        // 這裡只需要一個非 null 的參考當作「已初始化」的標記。
        var field = AccessTools.Field(typeof(GraphicData), "cachedGraphic");
        Assert.That(field, Is.Not.Null, "GraphicData.cachedGraphic 欄位不存在，測試前提已失效。");
        field.SetValue(data, (Graphic)FormatterServices.GetUninitializedObject(typeof(Graphic_Single)));
        return data;
    }

    private static ShaderParameter NewShaderParameter(string name, Vector4 value = default)
    {
        var parameter = (ShaderParameter)FormatterServices.GetUninitializedObject(typeof(ShaderParameter));
        var nameField = AccessTools.Field(typeof(ShaderParameter), "name");
        var valueField = AccessTools.Field(typeof(ShaderParameter), "value");
        Assert.That(nameField, Is.Not.Null, "ShaderParameter.name 欄位不存在，測試前提已失效。");
        Assert.That(valueField, Is.Not.Null, "ShaderParameter.value 欄位不存在，測試前提已失效。");
        nameField.SetValue(parameter, name);
        valueField.SetValue(parameter, value);
        return parameter;
    }
}
