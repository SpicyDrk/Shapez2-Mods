using JetBrains.Annotations;

namespace SmartCutter
{
    /// <summary>
    /// Per-frame renderer for a placed SmartCutter. Draws the belt item
    /// visuals on the input + output lanes each frame. Mirrors
    /// FourWaySplitterSimulationRenderer's shape: inherits the stateless
    /// renderer base, takes the standard DI ctor params, and calls
    /// DrawBeltItem for each lane.
    /// </summary>
    [UsedImplicitly]
    public class SmartCutterSimulationRenderer
        : StatelessBuildingSimulationRenderer<SmartCutterSimulation, ISmartCutterDrawData>
    {
        public SmartCutterSimulationRenderer(
            IMapModel map,
            IBuildingSoundManager soundManager,
            IShapeRegistry shapeRegistry) : base(map) { }

        public override void OnDrawDynamic(in Entity entity, FrameDrawOptions options)
        {
            SmartCutterSimulation simulation = entity.Simulation;

            DrawBeltItem(entity.Transform, options, simulation.InputLane, entity.DrawData.InputLaneRenderingDefinition);
            DrawBeltItem(entity.Transform, options, simulation.OutputLane, entity.DrawData.OutputLaneRenderingDefinition);
        }
    }
}
