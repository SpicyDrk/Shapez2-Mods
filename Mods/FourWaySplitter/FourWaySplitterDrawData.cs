using Game.Core.Coordinates;

namespace FourWaySplitter
{
    /// <summary>
    /// Concrete <see cref="IFourWaySplitterDrawData"/> used by the game's
    /// renderer. Positions the 5 lanes on a 1-tile / 2-level FourWaySplitter:
    /// <list type="bullet">
    ///   <item>Input (south, level 0 — lower platform): edge-to-center on the south face.</item>
    ///   <item>Four outputs (N/E/S/W, level 1 — upper platform): center-to-edge on each cardinal.</item>
    /// </list>
    ///
    /// <para>
    /// Belt items visually "flow inward" on the input lane (start at the
    /// south edge, end at center) and "flow outward" on each output lane
    /// (start at center, end at the cardinal edge) — matches DiagonalCutter's
    /// input/output start/end convention.
    /// </para>
    ///
    /// <para>
    /// <see cref="LocalVector"/> Z encodes platform level (0 = lower, 1 = upper).
    /// Item positions are MVP-grade and may need tuning during
    /// <c>/oes:verify 2</c> after visual inspection — that's expected per the
    /// plan file (don't over-engineer now).
    /// </para>
    /// </summary>
    internal class FourWaySplitterDrawData : IFourWaySplitterDrawData
    {
        public IBeltLaneRendererDefinition InputLaneRenderingDefinition => new FourWayBeltLaneRenderingDefinition(
            new LocalVector(0.0f, -0.5f, 0.0f),
            new LocalVector(0.0f, 0.0f, 0.0f));

        public IBeltLaneRendererDefinition NorthLaneRenderingDefinition => new FourWayBeltLaneRenderingDefinition(
            new LocalVector(0.0f, 0.0f, 1.0f),
            new LocalVector(0.0f, 0.5f, 1.0f));

        public IBeltLaneRendererDefinition EastLaneRenderingDefinition => new FourWayBeltLaneRenderingDefinition(
            new LocalVector(0.0f, 0.0f, 1.0f),
            new LocalVector(0.5f, 0.0f, 1.0f));

        public IBeltLaneRendererDefinition SouthLaneRenderingDefinition => new FourWayBeltLaneRenderingDefinition(
            new LocalVector(0.0f, 0.0f, 1.0f),
            new LocalVector(0.0f, -0.5f, 1.0f));

        public IBeltLaneRendererDefinition WestLaneRenderingDefinition => new FourWayBeltLaneRenderingDefinition(
            new LocalVector(0.0f, 0.0f, 1.0f),
            new LocalVector(-0.5f, 0.0f, 1.0f));
    }
}
