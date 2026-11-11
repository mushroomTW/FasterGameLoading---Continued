using System;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 排入延遲圖形佇列當下的圖形輸入快照。
    ///
    /// 動機：原版 <c>ThingDef.PostLoad</c> 排入的回呼，會在同一個 ExecuteWhenFinished 批次裡、
    /// <c>StaticConstructorOnStartupUtility.CallAll()</c> 之前建立圖形並插入靜態圖集。
    /// 因此任何在靜態建構子改寫圖形的 Mod（例如 Bionic Icons 把多個部位改成共用外框貼圖、
    /// 再以各部位圖示當遮罩），在原版都不會影響圖集內容。
    /// FGL 把同一個回呼延後到主選單之後，改寫早已完成，於是被改寫的貼圖才被插入圖集：
    /// 同一張外框配到不同遮罩時，<c>GlobalTextureAtlasManager.TryInsertStatic</c> 會報
    /// 「Same texture with 2 different masks inserted into texture atlas manager」，
    /// 而且該外框在圖集裡只會留下一張遮罩，導致圖示全部套用同一個部位符號。
    ///
    /// 這裡只記錄會決定「插入哪張主貼圖與遮罩」的輸入；比對到變更時由
    /// <see cref="DeferredGraphicAtlasSkip"/> 跳過插入，結果與原版一致：
    /// 改寫後的圖形本來就不在靜態圖集裡，改寫前那張圖形才是原版插入的對象。
    ///
    /// 擷取時機是原版 ExecuteWhenFinished 回呼一執行、尚未建立圖形時：此時 Def 交互參照
    /// （例如 <see cref="GraphicData.shaderType"/>）已解析完成，而 <c>CallAll()</c> 尚未執行。
    /// 提前到 <c>PostLoad</c> 擷取會把尚未綁定的參照誤判成改寫；晚到延遲佇列才擷取則已錯過改寫前的狀態。
    ///
    /// 不讀 <see cref="GraphicData"/> 的私有 <c>cachedGraphic</c>：Publicizer 只在編譯期開放它，
    /// 執行期載入的仍是原始組件，直接存取會拋 FieldAccessException。
    /// 目前驗證過的改寫 Mod（Bionic Icons 等）會同時設定 <c>Def.graphic</c>、texPath 或 shaderType，
    /// 比對這些欄位即可涵蓋，不必為此付出反射的成本。
    ///
    /// 已知限制：只以反射替換 <c>cachedGraphic</c>、其餘欄位完全不動的 Mod 不會被偵測到。
    /// 這類 Mod 若讓同一張主貼圖搭配不同遮罩，圖集仍會出現「Same texture with 2 different masks」。
    /// </summary>
    internal readonly struct GraphicSnapshot
    {
        // 快照當下是否有 GraphicData；沒有圖形資料的 Def 不會排入圖形佇列。
        private readonly bool hasGraphicData;

        // 決定主貼圖（texPath）、遮罩（maskPath）與遮罩支援（shaderType）的輸入。
        private readonly string texPath;
        private readonly string maskPath;
        private readonly Type graphicClass;
        private readonly ShaderTypeDef shaderType;

        // 排入當下 Def 已建好的圖形（正常為 null）；其他 Mod 若自己建立了圖形，這裡就會不同。
        private readonly Graphic graphic;

        private GraphicSnapshot(ThingDef def)
        {
            var data = def?.graphicData;
            hasGraphicData = data != null;
            graphic = def?.graphic;
            texPath = null;
            maskPath = null;
            graphicClass = null;
            shaderType = null;
            if (data == null)
            {
                return;
            }

            texPath = data.texPath;
            maskPath = data.maskPath;
            graphicClass = data.graphicClass;
            shaderType = data.shaderType;
        }

        // 在原版 ExecuteWhenFinished 回呼一執行、尚未建立圖形時擷取：
        // Def 交互參照已解析，其他 Mod 的靜態建構子則尚未改寫圖形。
        internal static GraphicSnapshot Capture(ThingDef def) => new GraphicSnapshot(def);

        /// <summary>
        /// 圖形輸入是否已在排入後被改寫。顏色、尺寸等不影響貼圖與遮罩的欄位刻意不比對：
        /// 它們改變不了插入的內容，列入只會白白讓更多貼圖失去靜態圖集。
        /// </summary>
        internal bool IsOutdated(ThingDef def) => !Matches(def);

        private bool Matches(ThingDef def)
        {
            // 其他 Mod 已經自行建立圖形（Bionic Icons 會在這裡換成帶遮罩的圖形）。
            if (!ReferenceEquals(graphic, def?.graphic))
            {
                return false;
            }

            var data = def?.graphicData;
            if (data == null)
            {
                return !hasGraphicData;
            }

            return hasGraphicData
                && string.Equals(texPath, data.texPath, StringComparison.Ordinal)
                && string.Equals(maskPath, data.maskPath, StringComparison.Ordinal)
                && graphicClass == data.graphicClass
                // shaderType 是 Def 交互參照；快照當下若仍為 null（XML 沒指定時），
                // 原版回呼會補上 Cutout，這不是其他 Mod 的改寫。
                && (shaderType == null || shaderType == data.shaderType);
        }
    }
}
