using Game.Content.Features.Signals;
using Game.Content.Features.Signals.Conductor;
using Game.Content.Features.Signals.Connections;
using Game.Content.Features.Signals.Simulation;
using Game.Core.Simulation;

namespace SmartCutter
{
    /// <summary>
    /// SmartCutter runtime simulation. Reads a wire-input shape signal,
    /// interprets the wire shape's bottom layer as a 4-quadrant keep-mask, and
    /// applies that mask to every layer of the incoming belt shape before
    /// emitting it on the output belt.
    ///
    /// Topology (mirrors DiagonalCutter / FourWaySplitter's 3-lane pattern):
    /// <code>
    ///   InputLane      (3-arg, downstream = ProcessingLane, NO AcceptHook)
    ///     │ chain-forward
    ///     ▼
    ///   ProcessingLane (2-arg terminal; AcceptHook reads wire, applies mask,
    ///                   HandOvers to OutputLane, then consumes the item)
    ///     │ HandOverItem
    ///     ▼
    ///   OutputLane     (2-arg terminal; game IItemProvider pulls)
    /// </code>
    ///
    /// <para>
    /// <b>Empty-wire behaviour (Phase 1):</b> if the wire input has no signal
    /// (null), or the signal is not a shape, or the wire's bottom layer has
    /// no filled quadrants, the AcceptHook discards the input shape (output is
    /// empty). Stall-on-empty-wire is Phase 2's job — for Phase 1 the SC tests
    /// only assert basic single-layer mask behaviour, so an empty-mask result
    /// here is acceptable.
    /// </para>
    ///
    /// <para>
    /// <b>Unsupported shape inputs:</b> non-ShapeItem items (e.g. fluids) are
    /// wedge-rejected — same backpressure pattern FourWaySplitter uses. Pins
    /// and crystals are also wedge-rejected because the Unfold/Collapse
    /// pipeline can't reassemble lone pin/crystal references into valid
    /// shapes. Standard solid shapes (including painted) flow through normally.
    /// </para>
    /// </summary>
    public class SmartCutterSimulation
        : Simulation<SmartCutterSimulationState>, IItemSimulation, ISignalSimulation, IUpdatableSimulation
    {
        public readonly BeltLane InputLane;
        public readonly BeltLane ProcessingLane;
        public readonly BeltLane OutputLane;
        public readonly SignalConductorInput WireInputConductor;

        /// <inheritdoc />
        public int NumItemReceivers => 1;

        /// <inheritdoc />
        public int NumItemProviders => 1;

        /// <inheritdoc />
        public int NumSignalProviders => 0;

        /// <inheritdoc />
        public int NumSignalReceivers => 1;

        public SmartCutterSimulation(
            SmartCutterSimulationState simulationState,
            ISmartCutterConfiguration configuration,
            IShapeRegistry shapeRegistry,
            ShapeOperationSmartCut smartCut) : base(simulationState)
        {
            // Downstream-first construction.
            OutputLane = new BeltLane(configuration.BeltSpeed, simulationState.OutputLaneState);

            // Wire conductor initialized before the AcceptHook closure captures
            // `this` — keeps the compiler's nullable analysis happy and ensures
            // the field is observable from inside the hook with no race window.
            WireInputConductor = new SignalConductorInput(simulationState.WireInputConductorState);

            // ProcessingLane is the transformation point — AcceptHook fires in
            // chain-forward context as items arrive from InputLane.
            ProcessingLane = new BeltLane(configuration.BeltSpeed, simulationState.ProcessingLaneState);
            ProcessingLane.AcceptHook = (IItemReceiver _, ref IBeltItem item, ref Ticks ticks) =>
            {
                // Reject non-shape items (e.g. fluid packages) via the
                // wedge-rejection pattern — leave `item` untouched so it stays
                // on this terminal lane. Backpressure propagates upstream
                // naturally via CanAcceptItem.
                if (item is not ShapeItem shapeItem)
                {
                    return;
                }

                // Reject crystals / pins for the same reason FourWaySplitter
                // does — ShapeLogic.Unfold/Collapse can't reassemble lone
                // crystal-or-pin references into valid ShapeIds, so a split
                // would silently consume the input. Wedge so the upstream
                // belt backs up visibly.
                foreach (ShapeLayer layer in shapeItem.Definition.Layers)
                {
                    foreach (ShapePart part in layer.Parts)
                    {
                        if (part.IsEmpty) continue;
                        char code = part.Shape.Code;
                        if (code == 'c' || code == 'P')
                        {
                            return; // wedge-reject
                        }
                    }
                }

                // Read the most recent wire signal. NullSignal / non-shape
                // signals produce a null wire shape → null mask result → drop
                // the item (empty output). Phase 2 will stall here instead.
                ISignal wireSignal = WireInputConductor.GetMostRecent();
                ShapeDefinition? wireShape = wireSignal is BeltItemSignal beltSig && beltSig.Value is ShapeItem wireItem
                    ? wireItem.Definition
                    : null;

                if (wireShape != null)
                {
                    ShapeCollapseResult result = smartCut.Execute(shapeItem.Definition, wireShape);
                    if (result is { ResultsInEmptyShape: false, Shape: { } shape })
                    {
                        ShapeItem maskedItem = shapeRegistry.GetItem(shape);
                        OutputLane.HandOverItem(maskedItem, ticks);
                    }
                    // else: mask kept nothing → no output emitted; the input
                    // shape is consumed below.
                }
                // else: empty / non-shape wire signal → drop the input. Phase 2
                // changes this to stall (don't consume).

                // Consume from ProcessingLane.
                item = null!;
            };

            // InputLane last — 3-arg ctor, downstream = ProcessingLane.
            InputLane = new BeltLane(configuration.BeltSpeed, simulationState.InputLaneState, ProcessingLane);
        }

        /// <inheritdoc />
        public IItemReceiver GetItemReceiver(int index) => InputLane;

        /// <inheritdoc />
        public IItemProvider GetItemProvider(int index) => OutputLane;

        /// <inheritdoc />
        public ISignalReceiver GetSignalReceiver(int index) => WireInputConductor;

        /// <inheritdoc />
        public void TraverseLanes<TTraverser>(TTraverser traverser)
            where TTraverser : IItemLaneTraverser
        {
            traverser.Traverse(InputLane);
            traverser.Traverse(ProcessingLane);
            traverser.Traverse(OutputLane);
        }

        /// <inheritdoc />
        public void ClearContent()
        {
            TraverseLanes(ClearItemsItemLaneTraverser.Default);
        }

        /// <inheritdoc />
        public void Update(Ticks startTicks, Ticks deltaTicks)
        {
            // Downstream-first.
            OutputLane.Update(deltaTicks);
            ProcessingLane.Update(deltaTicks);
            InputLane.Update(deltaTicks);
        }
    }
}
