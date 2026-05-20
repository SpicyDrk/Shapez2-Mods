using Game.Content.Features.Signals;
using Game.Content.Features.Signals.Conductor;
using Game.Content.Features.Signals.Connections;
using Game.Content.Features.Signals.Simulation;
using Game.Core.Simulation;

namespace SmartCutter
{
    /// <summary>
    /// SmartCutter runtime simulation. Reads a single-layer wire-input shape
    /// signal, interprets its bottom layer as a 4-quadrant keep-mask, and
    /// applies the mask uniformly to every layer of the incoming belt shape
    /// before emitting it on the output belt.
    ///
    /// Topology (3-lane, downstream-first):
    /// <code>
    ///   InputLane      (3-arg, downstream = ProcessingLane, no AcceptHook)
    ///     │ chain-forward
    ///     ▼
    ///   ProcessingLane (2-arg terminal, no AcceptHook — items land here and
    ///                   wait for the next Update() tick to drain)
    ///     │ DrainProcessingLane() in Update() — reads wire, masks, hands over
    ///     ▼
    ///   OutputLane     (2-arg terminal; game IItemProvider pulls)
    /// </code>
    ///
    /// <para>
    /// <b>Why Update()-driven drain (not AcceptHook)?</b> AcceptHook only
    /// fires once per item arrival (see SingleItemLane.HandOverItem). If the
    /// hook returned without consuming under a transient stall condition
    /// (empty wire / multi-layer wire / output-full), the item would land on
    /// ProcessingLane and the hook would never re-fire — even after the
    /// player fixed the condition. The Phase 2 UAT caught this as a
    /// permanent-wedge bug. Polling in Update() retries every tick, so
    /// stalls become transient.
    /// </para>
    ///
    /// <para>
    /// <b>Stall conditions (transient — wedge until resolved):</b> the held
    /// shape stays on ProcessingLane and the input belt backs up via
    /// CanAcceptItem (HasItem → false). Each tick re-checks; flow resumes
    /// as soon as the condition clears:
    /// </para>
    /// <list type="number">
    ///   <item>Wire signal is empty / null / non-shape.</item>
    ///   <item>Wire shape has more than one layer (invalid input per INTENT D3).</item>
    ///   <item>OutputLane can't accept the masked item (output belt full).</item>
    /// </list>
    ///
    /// <para>
    /// <b>Permanent wedges (item sits visibly on ProcessingLane forever):</b>
    /// non-ShapeItem inputs (e.g. fluid packages), and shapes containing
    /// crystal ('c') or pin ('P') sub-parts. The Unfold/Collapse pipeline
    /// can't reassemble lone crystal/pin references into valid ShapeIds, so
    /// they're never drainable. The player sees the offending item parked on
    /// the building and knows to remove it.
    /// </para>
    ///
    /// <para>
    /// <b>Empty-mask result:</b> when the wire is valid but every quadrant is
    /// empty (e.g. wire = `--------`), the mask keeps nothing. The
    /// implementation consumes the input and emits no output —
    /// interpretation is "cut everything away, valid result."
    /// </para>
    /// </summary>
    public class SmartCutterSimulation
        : Simulation<SmartCutterSimulationState>, IItemSimulation, ISignalSimulation, IUpdatableSimulation
    {
        public readonly BeltLane InputLane;
        public readonly BeltLane ProcessingLane;
        public readonly BeltLane OutputLane;
        public readonly SignalConductorInput WireInputConductor;

        private readonly IShapeRegistry _shapeRegistry;
        private readonly ShapeOperationSmartCut _smartCut;

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
            _shapeRegistry = shapeRegistry;
            _smartCut = smartCut;

            // Downstream-first construction.
            OutputLane = new BeltLane(configuration.BeltSpeed, simulationState.OutputLaneState);

            WireInputConductor = new SignalConductorInput(simulationState.WireInputConductorState);

            // ProcessingLane: terminal lane (no NextLane, no AcceptHook).
            // Items arrive via InputLane chain-forward, sit here until Update()
            // drains them. CanAcceptItem (HasItem → false) provides backpressure.
            ProcessingLane = new BeltLane(configuration.BeltSpeed, simulationState.ProcessingLaneState);

            // InputLane: 3-arg ctor with ProcessingLane as downstream.
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
            // Downstream-first lane updates.
            OutputLane.Update(deltaTicks);

            // Drain any drainable item held on ProcessingLane before its own
            // Update runs and before InputLane tries to push the next item in.
            DrainProcessingLane(deltaTicks);

            ProcessingLane.Update(deltaTicks);
            InputLane.Update(deltaTicks);
        }

        /// <summary>
        /// Per-tick drain. If ProcessingLane holds a drainable ShapeItem and
        /// all transient conditions are satisfied (wire valid + single-layer,
        /// output has room), apply the mask and hand over to OutputLane.
        /// Otherwise leave the item — next tick retries.
        /// </summary>
        private void DrainProcessingLane(Ticks deltaTicks)
        {
            if (!ProcessingLane.HasItem)
            {
                return;
            }

            if (ProcessingLane.Item is not ShapeItem heldShape)
            {
                // Non-shape (e.g. fluid package) — permanent wedge.
                return;
            }

            // Crystal/pin shapes are not drainable — the Unfold/Collapse
            // pipeline can't reassemble lone crystal/pin references. Permanent
            // wedge — leave the item on ProcessingLane for the player to see.
            foreach (ShapeLayer layer in heldShape.Definition.Layers)
            {
                foreach (ShapePart part in layer.Parts)
                {
                    if (part.IsEmpty) continue;
                    char code = part.Shape.Code;
                    if (code == 'c' || code == 'P')
                    {
                        return;
                    }
                }
            }

            // Read the most recent wire signal.
            ISignal wireSignal = WireInputConductor.GetMostRecent();
            ShapeDefinition? wireShape = wireSignal is BeltItemSignal beltSig && beltSig.Value is ShapeItem wireItem
                ? wireItem.Definition
                : null;

            // Transient stall #1: empty / null / non-shape wire signal.
            if (wireShape == null)
            {
                return;
            }

            // Transient stall #2: multi-layer wire is invalid input (per INTENT D3).
            if (wireShape.Layers.Length > 1)
            {
                return;
            }

            ShapeCollapseResult result = _smartCut.Execute(heldShape.Definition, wireShape);
            if (result is { ResultsInEmptyShape: false, Shape: { } shape })
            {
                ShapeItem maskedItem = _shapeRegistry.GetItem(shape);

                // Transient stall #3: output-full backpressure.
                if (!OutputLane.CanAcceptItem(maskedItem))
                {
                    return;
                }

                OutputLane.HandOverItem(maskedItem, deltaTicks);
            }
            // else: valid wire mask kept nothing → empty result → consume
            // the held shape, emit no output. See class docstring.

            ProcessingLane.Clear();
        }
    }
}
