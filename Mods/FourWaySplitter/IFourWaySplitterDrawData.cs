namespace FourWaySplitter
{
    /// <summary>
    /// Per-entity draw-data contract for the FourWaySplitter. Exposes the
    /// 5 belt-lane rendering definitions the renderer uses to position
    /// shape items visually: 1 south-level-0 input + 4 cardinal-level-1
    /// outputs (N/E/S/W).
    ///
    /// Mirrors <c>IDiagonalCutterDrawData</c>'s shape — it extends
    /// <see cref="IBuildingCustomDrawData"/> so the game's entity system
    /// knows how to bind this per-building draw configuration.
    /// </summary>
    public interface IFourWaySplitterDrawData : IBuildingCustomDrawData
    {
        IBeltLaneRendererDefinition InputLaneRenderingDefinition { get; }
        IBeltLaneRendererDefinition NorthLaneRenderingDefinition { get; }
        IBeltLaneRendererDefinition EastLaneRenderingDefinition { get; }
        IBeltLaneRendererDefinition SouthLaneRenderingDefinition { get; }
        IBeltLaneRendererDefinition WestLaneRenderingDefinition { get; }
    }
}
