using Game.Core.Serialization;
using Game.Core.Simulation;

namespace FourWaySplitter
{
    /// <summary>
    /// Simulation state for the FourWaySplitter. One input lane, four staging
    /// lanes, and four cardinal output lanes (N/E/S/W on level 2). The split
    /// is computed synchronously in the input AcceptHook, then items are
    /// HandOverItem'd onto the staging lanes, which chain-forward to their
    /// respective output lanes via the 3-arg BeltLane ctor. Chain-forwarding
    /// lands items at the providable position on the output lane — the
    /// position the game's IItemProvider polling drains from. Direct
    /// HandOverItem onto a 2-arg terminal output lane does NOT produce a
    /// providable-position item, which caused the break-stall bug fixed in
    /// PLAN-P02-008.
    /// </summary>
    [SyncableIdentifier("FourWaySplitterState")]
    public class FourWaySplitterSimulationState : ISimulationState
    {
        public readonly BeltLaneState InputLaneState = new();

        public readonly BeltLaneState NorthStagingLaneState = new();
        public readonly BeltLaneState EastStagingLaneState = new();
        public readonly BeltLaneState SouthStagingLaneState = new();
        public readonly BeltLaneState WestStagingLaneState = new();

        public readonly BeltLaneState NorthOutputLaneState = new();
        public readonly BeltLaneState EastOutputLaneState = new();
        public readonly BeltLaneState SouthOutputLaneState = new();
        public readonly BeltLaneState WestOutputLaneState = new();

        public void Sync(ISerializationVisitor visitor)
        {
            InputLaneState.Sync(visitor);

            NorthStagingLaneState.Sync(visitor);
            EastStagingLaneState.Sync(visitor);
            SouthStagingLaneState.Sync(visitor);
            WestStagingLaneState.Sync(visitor);

            NorthOutputLaneState.Sync(visitor);
            EastOutputLaneState.Sync(visitor);
            SouthOutputLaneState.Sync(visitor);
            WestOutputLaneState.Sync(visitor);
        }
    }
}
