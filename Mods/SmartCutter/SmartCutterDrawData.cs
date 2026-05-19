using Game.Core.Coordinates;

namespace SmartCutter
{
    /// <summary>
    /// Concrete <see cref="ISmartCutterDrawData"/> used by the game's renderer.
    /// Positions the two belt-lane visuals on a 1×1 SmartCutter:
    /// <list type="bullet">
    ///   <item>Input lane: west edge → center (item flows inward).</item>
    ///   <item>Output lane: center → east edge (item flows outward).</item>
    /// </list>
    /// Wire-input visual rendering is handled by the game's signal-port renderer
    /// based on the connector data alone — no per-entity draw-data entry needed.
    /// </summary>
    internal class SmartCutterDrawData : ISmartCutterDrawData
    {
        public IBeltLaneRendererDefinition InputLaneRenderingDefinition => new SmartCutterBeltLaneRenderingDefinition(
            new LocalVector(-0.5f, 0.0f, 0.0f),
            new LocalVector(0.0f, 0.0f, 0.0f));

        public IBeltLaneRendererDefinition OutputLaneRenderingDefinition => new SmartCutterBeltLaneRenderingDefinition(
            new LocalVector(0.0f, 0.0f, 0.0f),
            new LocalVector(0.5f, 0.0f, 0.0f));
    }
}
