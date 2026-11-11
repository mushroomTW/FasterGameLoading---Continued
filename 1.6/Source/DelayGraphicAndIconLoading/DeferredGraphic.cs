using System;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 延遲圖形佇列項目：要載入圖形的 Def、原版 PostLoad 回呼，以及排入當下的圖形輸入快照。
    /// 圖形快照必須在 ExecuteWhenFinished 回呼一執行時擷取，晚於其他 Mod 的靜態建構子就失去意義。
    /// </summary>
    internal readonly struct DeferredGraphic
    {
        internal DeferredGraphic(ThingDef def, Action run, GraphicSnapshot snapshot)
        {
            Def = def;
            Run = run;
            Snapshot = snapshot;
        }

        internal ThingDef Def { get; }

        internal Action Run { get; }

        internal GraphicSnapshot Snapshot { get; }
    }
}
