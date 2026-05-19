using Game.Core.Coordinates;

namespace SmartCutter
{
    /// <summary>
    /// Minimal <see cref="IBeltLaneRendererDefinition"/> implementation used by
    /// <see cref="SmartCutterDrawData"/> to position the 2 belt-lane visuals
    /// (input + output). Same shape as FourWaySplitter's equivalent.
    /// </summary>
    internal class SmartCutterBeltLaneRenderingDefinition : IBeltLaneRendererDefinition
    {
        public LocalVector ItemStartPos_L { get; }
        public LocalVector ItemEndPos_L { get; }

        public SmartCutterBeltLaneRenderingDefinition(LocalVector itemStartPos_L, LocalVector itemEndPos_L)
        {
            ItemStartPos_L = itemStartPos_L;
            ItemEndPos_L = itemEndPos_L;
        }
    }
}
