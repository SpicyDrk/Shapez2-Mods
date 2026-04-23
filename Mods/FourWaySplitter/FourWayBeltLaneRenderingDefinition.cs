using Game.Core.Coordinates;

namespace FourWaySplitter
{
    /// <summary>
    /// Minimal <see cref="IBeltLaneRendererDefinition"/> implementation used by
    /// <see cref="FourWaySplitterDrawData"/> to position the 5 lane visuals
    /// (1 input + 4 outputs). Mirrors the DiagonalCutter sample's
    /// <c>MyBeltLaneRenderingDefinition</c> verbatim, renamed here to avoid
    /// confusion with the sample's name (CONSTRAINTS §5a: match DiagonalCutter
    /// structure, but namespaced to this mod).
    ///
    /// Level offsets are encoded via the <see cref="LocalVector"/> Z component
    /// (0 = lower platform, 1 = upper platform) — the same idiom the
    /// DiagonalCutter sample uses for its single-level rendering.
    /// </summary>
    internal class FourWayBeltLaneRenderingDefinition : IBeltLaneRendererDefinition
    {
        public LocalVector ItemStartPos_L { get; }
        public LocalVector ItemEndPos_L { get; }

        public FourWayBeltLaneRenderingDefinition(LocalVector itemStartPos_L, LocalVector itemEndPos_L)
        {
            ItemStartPos_L = itemStartPos_L;
            ItemEndPos_L = itemEndPos_L;
        }
    }
}
