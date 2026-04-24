using Game.Core.Simulation;

namespace FourWaySplitter
{
    /// <summary>
    /// FourWaySplitter runtime simulation. Consumes a shape from a single
    /// south-level-0 input and emits four per-quadrant shapes onto the four
    /// cardinal level-1 outputs (N/E/S/W — TR/BR/BL/TL clockwise-spatial).
    ///
    /// Structural note: the game's reusable operation framework caps at 2
    /// outputs (<c>IItemOperation1In2Out</c> and
    /// <c>Processing1In2OutPredictionSimulation</c>), so this simulation
    /// deliberately bypasses that framework and inherits
    /// <see cref="Simulation{TState}"/>, <see cref="IItemSimulation"/>, and
    /// <see cref="IUpdatableSimulation"/> directly with
    /// <c>NumItemProviders = 4</c>. See STATE.md (2026-04-22) for the
    /// decision trail.
    ///
    /// Topology (PLAN-P02-009, matches DiagonalCutter's pattern):
    /// <code>
    ///   InputLane      (3-arg, downstream = ProcessingLane, NO AcceptHook)
    ///     │ chain-forward (internal belt-system mechanism)
    ///     ▼
    ///   ProcessingLane (2-arg terminal, AcceptHook runs split + fan-out)
    ///     │ HandOverItem (fired in chain-forward context)
    ///     ▼
    ///   N/E/S/W OutputLane (2-arg terminal, NO AcceptHook; game IItemProvider pulls)
    /// </code>
    ///
    /// Why this topology: DiagonalCutter's items actually traverse all three
    /// of its lanes via chain-forward — the belt system's internal handoff
    /// machinery positions items for IItemProvider pickup at the end of
    /// OutputLane. Prior designs (Plan-007 / Plan-008) consumed items in an
    /// <c>InputLane.AcceptHook</c> fired by the upstream belt's external
    /// HandOverItem; that is a different timing phase than chain-forward,
    /// and HandOverItem calls made inside it placed items at non-providable
    /// positions on the output lanes, causing the documented break-stall
    /// bug where any transient back-up permanently wedged the sim. Moving
    /// the AcceptHook onto ProcessingLane puts its HandOverItem fan-out
    /// calls inside the chain-forward handoff phase — matching the phase
    /// DiagonalCutter's own lane-to-lane transitions use.
    ///
    /// No capacity gate: DiagonalCutter's AcceptHook does not check
    /// downstream capacity either — it relies on natural belt-system
    /// backpressure (full OutputLane → ProcessingLane can't forward →
    /// ProcessingLane fills → InputLane can't forward → InputLane fills
    /// → upstream belt blocked). Gating 1→4 fan-out inside an AcceptHook
    /// re-introduces the stuck-state bug that eliminated Plan-007's
    /// HasPendingResult flag and Plan-008's staging-lane gate. The
    /// trade-off: if any output is full when a split fires, that output's
    /// HandOverItem may silently drop the item (throughput loss during
    /// backpressure). Acceptable for v1. Re-entrancy-safe backpressure
    /// for 1→N fan-out is polish work for a future iteration.
    ///
    /// MVP scope: no delay / processing delay. The configuration's
    /// ProcessingDelay is still exposed for future expansion but is not
    /// wired into a DelayBeltLane here (CONSTRAINTS §5a: ship MVP first).
    ///
    /// <para>
    /// <b>Unsupported item types (SC-09, PLAN-P03-001):</b> non-<see cref="ShapeItem"/>
    /// inputs (crystals, fluids, pins, painted shapes) are rejected via
    /// wedge-style backpressure. The item is stored on the terminal
    /// ProcessingLane indefinitely; ProcessingLane fills up, InputLane
    /// chain-forward blocks, InputLane fills, upstream belt's
    /// HandOverItem fails CanAcceptItem, upstream backs up. The item is
    /// preserved (no silent loss per SC-09) but the building wedges
    /// until destroyed. Approximates classic Shapez "reject on input"
    /// UX; true never-enters-the-building reject isn't achievable
    /// without per-item-type CanAcceptItem customization, which the
    /// BeltLane API doesn't appear to expose.
    /// </para>
    /// </summary>
    public class FourWaySplitterSimulation : Simulation<FourWaySplitterSimulationState>, IItemSimulation, IUpdatableSimulation
    {
        public readonly BeltLane InputLane;
        public readonly BeltLane ProcessingLane;
        public readonly BeltLane NorthOutputLane;
        public readonly BeltLane EastOutputLane;
        public readonly BeltLane SouthOutputLane;
        public readonly BeltLane WestOutputLane;

        /// <inheritdoc />
        public int NumItemReceivers => 1;

        /// <inheritdoc />
        public int NumItemProviders => 4;

        public FourWaySplitterSimulation(
            FourWaySplitterSimulationState simulationState,
            IFourWaySplitterConfiguration configuration,
            IShapeRegistry shapeRegistry,
            ShapeOperationFourWaySplit fourWaySplit) : base(simulationState)
        {
            // Construction order is downstream-first so each 3-arg ctor can
            // reference its already-constructed downstream receiver.
            NorthOutputLane = new BeltLane(configuration.BeltSpeed, simulationState.NorthOutputLaneState);
            EastOutputLane = new BeltLane(configuration.BeltSpeed, simulationState.EastOutputLaneState);
            SouthOutputLane = new BeltLane(configuration.BeltSpeed, simulationState.SouthOutputLaneState);
            WestOutputLane = new BeltLane(configuration.BeltSpeed, simulationState.WestOutputLaneState);

            // ProcessingLane is 2-arg terminal; its AcceptHook handles the
            // 1→4 fan-out. Items arrive here via chain-forward from InputLane
            // — the belt system fires AcceptHook in that handoff context.
            ProcessingLane = new BeltLane(configuration.BeltSpeed, simulationState.ProcessingLaneState);
            ProcessingLane.AcceptHook = (IItemReceiver _, ref IBeltItem item, ref Ticks ticks) =>
            {
                if (item is not ShapeItem shapeItem)
                {
                    // SC-09 (PLAN-P03-001): non-shape items rejected via
                    // wedge-style backpressure. Leave `item` unchanged —
                    // stored on this terminal lane with no downstream
                    // drain. ProcessingLane fills → InputLane can't
                    // chain-forward → upstream belt backs up via natural
                    // CanAcceptItem propagation. Item preserved (no
                    // silent loss per SC-09) at the cost of wedging the
                    // building until the player destroys it. See class
                    // docstring for the full rationale.
                    return;
                }

                FourWaySplitResult result = fourWaySplit.Execute(shapeItem.Definition);

                // Empty quadrants: ShapeCollapseResult with
                // ResultsInEmptyShape=true. No item to emit for that lane.
                ShapeItem? northItem = result.North is { ResultsInEmptyShape: false } n ? shapeRegistry.GetItem(n.Shape) : null;
                ShapeItem? eastItem  = result.East  is { ResultsInEmptyShape: false } e ? shapeRegistry.GetItem(e.Shape) : null;
                ShapeItem? southItem = result.South is { ResultsInEmptyShape: false } s ? shapeRegistry.GetItem(s.Shape) : null;
                ShapeItem? westItem  = result.West  is { ResultsInEmptyShape: false } w ? shapeRegistry.GetItem(w.Shape) : null;

                // Fan out. HandOverItem fires in chain-forward context —
                // if an output is full it may silently drop; we trade the
                // stuck-state bug for throughput-loss-under-backpressure.
                if (northItem != null) NorthOutputLane.HandOverItem(northItem, ticks);
                if (eastItem  != null) EastOutputLane.HandOverItem(eastItem, ticks);
                if (southItem != null) SouthOutputLane.HandOverItem(southItem, ticks);
                if (westItem  != null) WestOutputLane.HandOverItem(westItem, ticks);

                // Consume from ProcessingLane — we've dispatched the split
                // to the 4 outputs, ProcessingLane should not retain it.
                // `null!` suppresses CS8625; the belt system supports null
                // here as the consume signal.
                item = null!;
            };

            // InputLane last — 3-arg ctor, downstream=ProcessingLane. No
            // AcceptHook on InputLane: items traverse via Update and
            // chain-forward to ProcessingLane naturally.
            InputLane = new BeltLane(configuration.BeltSpeed, simulationState.InputLaneState, ProcessingLane);
        }

        /// <inheritdoc />
        public IItemReceiver GetItemReceiver(int index)
        {
            return InputLane;
        }

        /// <inheritdoc />
        public IItemProvider GetItemProvider(int index)
        {
            // Index order matches the hand-crafted BuildingConnectorData:
            //   0 -> North, 1 -> East, 2 -> South, 3 -> West
            // (TR/BR/BL/TL clockwise-spatial per CONSTRAINTS §5b). Out-of-range
            // is a programmer bug — default to North to keep CS8603 quiet.
            return index switch
            {
                0 => NorthOutputLane,
                1 => EastOutputLane,
                2 => SouthOutputLane,
                3 => WestOutputLane,
                _ => NorthOutputLane
            };
        }

        /// <inheritdoc />
        public void TraverseLanes<TTraverser>(TTraverser traverser)
            where TTraverser : IItemLaneTraverser
        {
            traverser.Traverse(InputLane);
            traverser.Traverse(ProcessingLane);
            traverser.Traverse(NorthOutputLane);
            traverser.Traverse(EastOutputLane);
            traverser.Traverse(SouthOutputLane);
            traverser.Traverse(WestOutputLane);
        }

        /// <inheritdoc />
        public void ClearContent()
        {
            TraverseLanes(ClearItemsItemLaneTraverser.Default);
        }

        /// <inheritdoc />
        public void Update(Ticks startTicks, Ticks deltaTicks)
        {
            // Strictly downstream-first, matching DiagonalCutter's pattern.
            NorthOutputLane.Update(deltaTicks);
            EastOutputLane.Update(deltaTicks);
            SouthOutputLane.Update(deltaTicks);
            WestOutputLane.Update(deltaTicks);
            ProcessingLane.Update(deltaTicks);
            InputLane.Update(deltaTicks);
        }
    }
}
