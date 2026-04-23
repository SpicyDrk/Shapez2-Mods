using Game.Core.Serialization;
using Game.Core.Simulation;

namespace FourWaySplitter
{
    /// <summary>
    /// Simulation state for the FourWaySplitter. Mirrors
    /// <c>DiagonalCutterSimulationState</c> structure, but with one input
    /// lane and four cardinal-output lanes (N/E/S/W on level 2) plus a
    /// <see cref="FourWaySplitResult"/> buffer that holds the 4 computed
    /// outputs between capture (on input) and emission (onto the 4 output
    /// lanes).
    ///
    /// MVP: no processing/delay lane — we process instantaneously on
    /// input-accept and gate emission on all-four-outputs-have-capacity in
    /// the Simulation.Update tick. ProcessingDelay from the configuration
    /// is wired through for future expansion but does not route through a
    /// DelayBeltLane here (CONSTRAINTS §5a steering: ship MVP first).
    ///
    /// Save/load serialization via <see cref="Sync"/>: each BeltLaneState
    /// calls its own Sync, the pending result is serialized field-by-field
    /// using the registered <c>ShapeCollapseResult</c> serializer (same
    /// idiom as DiagonalCutter), and the presence flag is a single bool.
    /// </summary>
    [SyncableIdentifier("FourWaySplitterState")]
    public class FourWaySplitterSimulationState : ISimulationState
    {
        public readonly BeltLaneState InputLaneState = new();
        public readonly BeltLaneState NorthOutputLaneState = new();
        public readonly BeltLaneState EastOutputLaneState = new();
        public readonly BeltLaneState SouthOutputLaneState = new();
        public readonly BeltLaneState WestOutputLaneState = new();

        // Pending 4-way split result: filled when the input lane accepts an
        // item, consumed when all four output lanes have capacity. Fields
        // are nullable ShapeCollapseResult structs (same type DiagonalCutter
        // stores in CurrentCollapseResult) — the whole FourWaySplitResult
        // is the unit we buffer.
        public FourWaySplitResult CurrentResult;

        // True iff CurrentResult holds an un-emitted split. Cleared when the
        // 4 output items have been pushed onto their lanes.
        public bool HasPendingResult;

        public void Sync(ISerializationVisitor visitor)
        {
            InputLaneState.Sync(visitor);
            NorthOutputLaneState.Sync(visitor);
            EastOutputLaneState.Sync(visitor);
            SouthOutputLaneState.Sync(visitor);
            WestOutputLaneState.Sync(visitor);

            // ShapeCollapseResult serializer takes `ref`, but FourWaySplitResult
            // exposes readonly fields (matches the ShapeDiagonalCutResult idiom).
            // We stage through local vars and rebuild the struct after Sync so
            // the readonly contract of the prior-task artifact is preserved.
            var collapseResultSerializer = visitor.GetSerializer<ShapeCollapseResult>();
            ShapeCollapseResult north = CurrentResult.North;
            ShapeCollapseResult east = CurrentResult.East;
            ShapeCollapseResult south = CurrentResult.South;
            ShapeCollapseResult west = CurrentResult.West;
            collapseResultSerializer.Sync(ref north);
            collapseResultSerializer.Sync(ref east);
            collapseResultSerializer.Sync(ref south);
            collapseResultSerializer.Sync(ref west);
            CurrentResult = new FourWaySplitResult(north, east, south, west);

            visitor.SyncBool_1(ref HasPendingResult);
        }
    }
}
