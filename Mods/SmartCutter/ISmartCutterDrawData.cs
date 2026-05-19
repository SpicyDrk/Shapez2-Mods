namespace SmartCutter
{
    /// <summary>
    /// Per-entity draw-data contract for the SmartCutter. Exposes the two
    /// belt-lane rendering definitions the renderer uses to position shape
    /// items visually: one west-face input + one east-face output.
    /// </summary>
    public interface ISmartCutterDrawData : IBuildingCustomDrawData
    {
        IBeltLaneRendererDefinition InputLaneRenderingDefinition { get; }
        IBeltLaneRendererDefinition OutputLaneRenderingDefinition { get; }
    }
}
