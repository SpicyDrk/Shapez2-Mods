using Game.Content.Features.Signals.Conductor;
using Game.Core.Serialization;
using Game.Core.Simulation;

namespace SmartCutter
{
    /// <summary>
    /// Simulation state for the SmartCutter. Holds:
    /// <list type="bullet">
    ///   <item><see cref="InputLaneState"/> — incoming shape belt (west face).</item>
    ///   <item><see cref="OutputLaneState"/> — outgoing shape belt (east face).</item>
    ///   <item><see cref="WireInputConductorState"/> — wire-signal input (north face).</item>
    /// </list>
    /// Phase P01 is a pass-through with wire signal stored but not yet applied;
    /// the keep-mask logic arrives in Task 4 of P01.
    /// </summary>
    [SyncableIdentifier("SmartCutterState")]
    public class SmartCutterSimulationState : ISimulationState
    {
        public readonly BeltLaneState InputLaneState = new();
        public readonly BeltLaneState ProcessingLaneState = new();
        public readonly BeltLaneState OutputLaneState = new();
        public readonly SignalConductorInputState WireInputConductorState = new();

        public void Sync(ISerializationVisitor visitor)
        {
            InputLaneState.Sync(visitor);
            ProcessingLaneState.Sync(visitor);
            OutputLaneState.Sync(visitor);
            WireInputConductorState.Sync(visitor);
        }
    }
}
