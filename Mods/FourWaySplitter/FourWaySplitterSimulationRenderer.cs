using JetBrains.Annotations;

namespace FourWaySplitter
{
    /// <summary>
    /// Per-frame renderer for a placed FourWaySplitter. Draws the belt item
    /// visuals on the 5 lanes each frame: 1 south-level-0 input +
    /// 4 cardinal-level-1 outputs (N/E/S/W — TR/BR/BL/TL clockwise-spatial
    /// per CONSTRAINTS §5b).
    ///
    /// Mirrors <c>DiagonalCutterSimulationRenderer</c>'s shape: inherits
    /// <see cref="StatelessBuildingSimulationRenderer{TSim,TDrawData}"/>,
    /// takes <see cref="IMapModel"/> / <see cref="IBuildingSoundManager"/> /
    /// <see cref="IShapeRegistry"/> in the ctor, and calls <c>base(map)</c>.
    /// The sound manager and shape registry params are accepted for DI
    /// parity with DiagonalCutter even though they aren't referenced in the
    /// MVP render path.
    ///
    /// MVP scope: <c>DrawBeltItem</c> only — no shape-collapse waste /
    /// support-mesh overlays. Those are P03 polish (per the task brief).
    /// </summary>
    [UsedImplicitly]
    public class FourWaySplitterSimulationRenderer
        : StatelessBuildingSimulationRenderer<FourWaySplitterSimulation, IFourWaySplitterDrawData>
    {
        public FourWaySplitterSimulationRenderer(
            IMapModel map,
            IBuildingSoundManager soundManager,
            IShapeRegistry shapeRegistry) : base(map) { }

        public override void OnDrawDynamic(in Entity entity, FrameDrawOptions options)
        {
            FourWaySplitterSimulation simulation = entity.Simulation;

            // 5 DrawBeltItem calls — 1 input + 4 outputs. Each pairs the
            // BeltLane (item source-of-truth) with the matching rendering
            // definition (start/end positions in local space).
            DrawBeltItem(entity.Transform, options, simulation.InputLane, entity.DrawData.InputLaneRenderingDefinition);
            DrawBeltItem(entity.Transform, options, simulation.NorthOutputLane, entity.DrawData.NorthLaneRenderingDefinition);
            DrawBeltItem(entity.Transform, options, simulation.EastOutputLane, entity.DrawData.EastLaneRenderingDefinition);
            DrawBeltItem(entity.Transform, options, simulation.SouthOutputLane, entity.DrawData.SouthLaneRenderingDefinition);
            DrawBeltItem(entity.Transform, options, simulation.WestOutputLane, entity.DrawData.WestLaneRenderingDefinition);

            // TODO (P03 polish): draw the in-progress collapse result + waste
            // + support-mesh during the split animation, similar to
            // DiagonalCutter's DrawShapeCollapseResult / DrawShapeSupportMesh
            // usage. Requires a processing/delay lane in the simulation
            // (currently not modelled — see FourWaySplitterSimulation.cs MVP
            // notes).
        }
    }
}
