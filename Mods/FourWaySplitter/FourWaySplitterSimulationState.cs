using Game.Core.Serialization;
using Game.Core.Simulation;

namespace FourWaySplitter
{
    /// <summary>
    /// Simulation state for the FourWaySplitter. Mirrors DiagonalCutter's
    /// pattern: items arrive at InputLane via external HandOverItem from the
    /// upstream belt, traverse InputLane via Update, chain-forward to
    /// ProcessingLane (the 3-arg downstream binding on InputLane drives this),
    /// ProcessingLane.AcceptHook fires in chain-forward context running the
    /// split + fan-out onto the four output lanes, and the game's
    /// IItemProvider polling drains the output lanes.
    ///
    /// <see cref="ProcessingLaneStagnantTicks"/> is the stagnation counter
    /// used for SC-09's wedge-reject auto-clear: it increments each Update
    /// that ProcessingLane has an item and resets to zero otherwise. Normal
    /// shape items never accumulate stagnation (they're consumed
    /// immediately in the AcceptHook). Crystals / pins that the hook
    /// wedges will accumulate until the counter exceeds the threshold at
    /// which point the Update clears the stuck item. See
    /// FourWaySplitterSimulation for the clear logic and threshold.
    /// </summary>
    [SyncableIdentifier("FourWaySplitterState")]
    public class FourWaySplitterSimulationState : ISimulationState
    {
        public readonly BeltLaneState InputLaneState = new();
        public readonly BeltLaneState ProcessingLaneState = new();

        public readonly BeltLaneState NorthOutputLaneState = new();
        public readonly BeltLaneState EastOutputLaneState = new();
        public readonly BeltLaneState SouthOutputLaneState = new();
        public readonly BeltLaneState WestOutputLaneState = new();

        // SC-09 wedge auto-clear counter. Plain int — Sync via SyncInt_4.
        public int ProcessingLaneStagnantTicks;

        public void Sync(ISerializationVisitor visitor)
        {
            InputLaneState.Sync(visitor);
            ProcessingLaneState.Sync(visitor);

            NorthOutputLaneState.Sync(visitor);
            EastOutputLaneState.Sync(visitor);
            SouthOutputLaneState.Sync(visitor);
            WestOutputLaneState.Sync(visitor);

            visitor.SyncInt_4(ref ProcessingLaneStagnantTicks);
        }
    }
}
