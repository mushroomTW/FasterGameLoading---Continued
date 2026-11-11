using System.Runtime.Serialization;
using NUnit.Framework;
using Verse;

namespace FasterGameLoading.Tests.DelayGraphicAndIconLoading
{
    /// <summary>
    /// 圖形快照只記錄會影響「插入哪張主貼圖與遮罩」的輸入。
    /// 這些測試固定住兩件事：該偵測的改寫一定要偵測到（否則延遲載入會把改寫後的貼圖
    /// 插進靜態圖集，造成多遮罩衝突），而純視覺欄位的變動不得誤判（否則貼圖白白退出圖集）。
    /// </summary>
    [TestFixture]
    public class GraphicSnapshotTests
    {
        private static ThingDef CreateDef(GraphicData data)
        {
            var def = (ThingDef)FormatterServices.GetUninitializedObject(typeof(ThingDef));
            def.defName = "SnapshotDef";
            def.graphicData = data;
            return def;
        }

        private static GraphicData CreateGraphicData(string texPath)
        {
            var data = (GraphicData)FormatterServices.GetUninitializedObject(typeof(GraphicData));
            data.texPath = texPath;
            data.maskPath = null;
            data.graphicClass = typeof(Graphic_Single);
            data.shaderType = null;
            return data;
        }

        // 只需要一個可以比對的圖形實例，不必經過建構子。
        private static Graphic NewGraphic() => (Graphic)FormatterServices.GetUninitializedObject(typeof(Graphic_Single));

        private static ShaderTypeDef NewShaderType()
            => (ShaderTypeDef)FormatterServices.GetUninitializedObject(typeof(ShaderTypeDef));

        [Test]
        public void Capture_WithoutGraphicData_IsNotOutdatedWhileItStaysNull()
        {
            var def = CreateDef(null);

            var snapshot = GraphicSnapshot.Capture(def);

            Assert.That(snapshot.IsOutdated(def), Is.False);
        }

        [Test]
        public void IsOutdated_WhenGraphicDataAppearsAfterCapture_IsTrue()
        {
            var def = CreateDef(null);
            var snapshot = GraphicSnapshot.Capture(def);

            def.graphicData = CreateGraphicData("Things/New");

            Assert.That(snapshot.IsOutdated(def), Is.True, "新增的圖形不會是原版插入圖集的那一張。");
        }

        [Test]
        public void IsOutdated_WhenNothingChanged_IsFalse()
        {
            var def = CreateDef(CreateGraphicData("Things/Item/Part"));

            var snapshot = GraphicSnapshot.Capture(def);

            Assert.That(snapshot.IsOutdated(def), Is.False);
        }

        [Test]
        public void IsOutdated_WhenGraphicDataReplacedWithSameValues_IsFalse()
        {
            var def = CreateDef(CreateGraphicData("Things/Item/Part"));
            var snapshot = GraphicSnapshot.Capture(def);

            // 換成另一份內容完全相同的 GraphicData：插入的貼圖與遮罩不變，仍可進靜態圖集。
            def.graphicData = CreateGraphicData("Things/Item/Part");

            Assert.That(snapshot.IsOutdated(def), Is.False);
        }

        [Test]
        public void IsOutdated_WhenTexPathChanged_IsTrue()
        {
            var def = CreateDef(CreateGraphicData("Things/Item/Part"));
            var snapshot = GraphicSnapshot.Capture(def);

            def.graphicData.texPath = "Things/Item/Archotech";

            Assert.That(snapshot.IsOutdated(def), Is.True);
        }

        [Test]
        public void IsOutdated_WhenMaskPathChanged_IsTrue()
        {
            var def = CreateDef(CreateGraphicData("Things/Item/Archotech"));
            var snapshot = GraphicSnapshot.Capture(def);

            def.graphicData.maskPath = "Things/Item/Archotech_Eye";

            Assert.That(snapshot.IsOutdated(def), Is.True);
        }

        [Test]
        public void IsOutdated_WhenGraphicClassChanged_IsTrue()
        {
            var def = CreateDef(CreateGraphicData("Things/Item/Part"));
            var snapshot = GraphicSnapshot.Capture(def);

            def.graphicData.graphicClass = typeof(Graphic_Multi);

            Assert.That(snapshot.IsOutdated(def), Is.True);
        }

        [Test]
        public void IsOutdated_WhenShaderTypeChanged_IsTrue()
        {
            var def = CreateDef(CreateGraphicData("Things/Item/Part"));
            def.graphicData.shaderType = NewShaderType();
            var snapshot = GraphicSnapshot.Capture(def);

            // 遮罩是否會被載入取決於 shader 是否支援 MaskTex，所以 shader 變更也算圖形改寫。
            def.graphicData.shaderType = NewShaderType();

            Assert.That(snapshot.IsOutdated(def), Is.True);
        }

        [Test]
        public void IsOutdated_WhenShaderTypeResolvedFromNull_IsFalse()
        {
            var def = CreateDef(CreateGraphicData("Things/Item/Part"));
            var snapshot = GraphicSnapshot.Capture(def);

            // shaderType 是 Def 交互參照：XML 沒指定時，PostLoad 之後才會解析或由原版補上 Cutout。
            def.graphicData.shaderType = NewShaderType();

            Assert.That(snapshot.IsOutdated(def), Is.False, "尚未綁定的 shaderType 不是其他 Mod 的改寫。");
        }

        [Test]
        public void IsOutdated_WhenAnotherModSetsDefGraphic_IsTrue()
        {
            var def = CreateDef(CreateGraphicData("Things/Item/Archotech"));
            var snapshot = GraphicSnapshot.Capture(def);

            // Bionic Icons 這類 Mod 會建立帶遮罩的新圖形並寫入 def.graphic，texPath 等欄位可能完全沒動。
            def.graphic = NewGraphic();

            Assert.That(snapshot.IsOutdated(def), Is.True);
        }

        [Test]
        public void IsOutdated_WhenDefGraphicReplacedByAnotherInstance_IsTrue()
        {
            var def = CreateDef(CreateGraphicData("Things/Item/Archotech"));
            def.graphic = NewGraphic();
            var snapshot = GraphicSnapshot.Capture(def);

            def.graphic = NewGraphic();

            Assert.That(snapshot.IsOutdated(def), Is.True);
        }

        [Test]
        public void IsOutdated_WhenOnlyColorOrSizeChanged_IsFalse()
        {
            var def = CreateDef(CreateGraphicData("Things/Item/Part"));
            var snapshot = GraphicSnapshot.Capture(def);

            // 顏色與尺寸只影響材質參數，插入的貼圖與遮罩不變，不該因此退出靜態圖集。
            def.graphicData.color = UnityEngine.Color.red;
            def.graphicData.drawSize = new UnityEngine.Vector2(2f, 2f);

            Assert.That(snapshot.IsOutdated(def), Is.False);
        }
    }
}
