using Game.Core.Simulation;

namespace FourWaySplitter
{
    /// <summary>
    /// FourWaySplitter runtime simulation. Consumes a shape from a single
    /// south-level-1 input and emits four per-quadrant shapes onto the four
    /// cardinal level-2 outputs (N/E/S/W — TR/BR/BL/TL clockwise-spatial).
    ///
    /// Structural note: the game's reusable operation framework caps at
    /// 2 outputs (<c>IItemOperation1In2Out</c> and
    /// <c>Processing1In2OutPredictionSimulation</c>), so this simulation
    /// deliberately bypasses that framework and inherits
    /// <see cref="Simulation{TState}"/>, <see cref="IItemSimulation"/>, and
    /// <see cref="IUpdatableSimulation"/> directly with
    /// <c>NumItemProviders = 4</c>. See STATE.md (2026-04-22) for the
    /// decision trail.
    ///
    /// Flow: the input lane's AcceptHook runs the split synchronously.
    /// Before consuming the incoming item, it checks <c>CanAcceptItem</c>
    /// on every non-empty output lane. If any output is full the hook
    /// returns without mutating <c>item</c> — the belt system retries on
    /// the next tick. If all outputs pass, the hook calls
    /// <c>HandOverItem</c> on each and consumes the input (<c>item = null</c>).
    /// No per-tick buffering, no HasPendingResult flag — the hook is
    /// all-or-nothing per call.
    ///
    /// Why synchronous instead of buffered: the prior design cached the
    /// split in state and emitted from Update() gated on
    /// <c>HasPendingResult</c>. That flag stalled forever if
    /// <c>CanAcceptItem</c> ever returned false for one tick, because the
    /// input AcceptHook would then refuse every subsequent item. Moving
    /// the gate onto the accept path removes the stuck-state edge.
    ///
    /// MVP scope: no delay / processing lane. The configuration's
    /// ProcessingDelay is still exposed for future expansion but is not
    /// wired into a DelayBeltLane here (CONSTRAINTS §5a: ship MVP first).
    /// </summary>
    public class FourWaySplitterSimulation : Simulation<FourWaySplitterSimulationState>, IItemSimulation, IUpdatableSimulation
    {
        public readonly BeltLane InputLane;
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
            NorthOutputLane = new BeltLane(configuration.BeltSpeed, simulationState.NorthOutputLaneState);
            EastOutputLane = new BeltLane(configuration.BeltSpeed, simulationState.EastOutputLaneState);
            SouthOutputLane = new BeltLane(configuration.BeltSpeed, simulationState.SouthOutputLaneState);
            WestOutputLane = new BeltLane(configuration.BeltSpeed, simulationState.WestOutputLaneState);

            InputLane = new BeltLane(configuration.BeltSpeed, simulationState.InputLaneState);
            InputLane.AcceptHook = (IItemReceiver _, ref IBeltItem item, ref Ticks ticks) =>
            {
                // Non-shape items: pass-through (leave item, upstream belt keeps it).
                // Formal policy for crystals/fluids/pins is deferred to P03.
                if (item is not ShapeItem shapeItem)
                {
                    return;
                }

                ShapeDefinition definition = shapeItem.Definition;
                FourWaySplitResult result = fourWaySplit.Execute(definition);

                // Empty quadrants surface as a non-null ShapeCollapseResult
                // with ResultsInEmptyShape=true (see ShapeLogic.Collapse).
                // Null-check too for defense against default(FourWaySplitResult).
                ShapeItem? northItem = result.North is { ResultsInEmptyShape: false } n ? shapeRegistry.GetItem(n.Shape) : null;
                ShapeItem? eastItem  = result.East  is { ResultsInEmptyShape: false } e ? shapeRegistry.GetItem(e.Shape) : null;
                ShapeItem? southItem = result.South is { ResultsInEmptyShape: false } s ? shapeRegistry.GetItem(s.Shape) : null;
                ShapeItem? westItem  = result.West  is { ResultsInEmptyShape: false } w ? shapeRegistry.GetItem(w.Shape) : null;

                // All-or-nothing gate: if any non-empty output is full, bail
                // without consuming. Belt system retries on the next tick.
                if (northItem != null && !NorthOutputLane.CanAcceptItem(northItem)) return;
                if (eastItem  != null && !EastOutputLane.CanAcceptItem(eastItem))   return;
                if (southItem != null && !SouthOutputLane.CanAcceptItem(southItem)) return;
                if (westItem  != null && !WestOutputLane.CanAcceptItem(westItem))   return;

                if (northItem != null) NorthOutputLane.HandOverItem(northItem, ticks);
                if (eastItem  != null) EastOutputLane.HandOverItem(eastItem, ticks);
                if (southItem != null) SouthOutputLane.HandOverItem(southItem, ticks);
                if (westItem  != null) WestOutputLane.HandOverItem(westItem, ticks);

                // Consume. `null!` suppresses CS8625 — the belt system
                // explicitly supports null here as the "consume" signal.
                item = null!;
            };
        }

        /// <inheritdoc />
        public IItemReceiver GetItemReceiver(int index)
        {
            // Only one receiver — the south level-1 input.
            return InputLane;
        }

        /// <inheritdoc />
        public IItemProvider GetItemProvider(int index)
        {
            // Four providers, one per cardinal. Index order matches the
            // hand-crafted BuildingConnectorData in Task 4:
            //   0 -> North, 1 -> East, 2 -> South, 3 -> West
            // (TR/BR/BL/TL clockwise-spatial per CONSTRAINTS §5b).
            // Caller is expected to use 0..3 (we advertise NumItemProviders=4).
            // Out-of-range is a programmer bug — return North as a safe fallback
            // rather than null to keep CS8603 quiet and avoid NRE at call site.
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
            // Output-to-input order matches DiagonalCutter — gives
            // downstream lanes a chance to drain before upstream pushes.
            NorthOutputLane.Update(deltaTicks);
            EastOutputLane.Update(deltaTicks);
            SouthOutputLane.Update(deltaTicks);
            WestOutputLane.Update(deltaTicks);
            InputLane.Update(deltaTicks);
        }
    }
}

