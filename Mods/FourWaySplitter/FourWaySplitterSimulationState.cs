using Game.Core.Serialization;
using Game.Core.Simulation;

namespace FourWaySplitter
{
    /// <summary>
    /// Simulation state for the FourWaySplitter. One input lane and four
    /// cardinal-output lanes (N/E/S/W on level 2). The split itself is
    /// computed and emitted synchronously in the input AcceptHook, so no
    /// result needs to be buffered in state.
    /// </summary>
    [SyncableIdentifier("FourWaySplitterState")]
    public class FourWaySplitterSimulationState : ISimulationState
    {
        public readonly BeltLaneState InputLaneState = new();
        public readonly BeltLaneState NorthOutputLaneState = new();
        public readonly BeltLaneState EastOutputLaneState = new();
        public readonly BeltLaneState SouthOutputLaneState = new();
        public readonly BeltLaneState WestOutputLaneState = new();

        public void Sync(ISerializationVisitor visitor)
        {
            InputLaneState.Sync(visitor);
            NorthOutputLaneState.Sync(visitor);
            EastOutputLaneState.Sync(visitor);
            SouthOutputLaneState.Sync(visitor);
            WestOutputLaneState.Sync(visitor);
        }
    }
}
