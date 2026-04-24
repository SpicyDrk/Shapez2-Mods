using Game.Core.Simulation;

namespace FourWaySplitter
{
    /// <summary>
    /// FourWaySplitter runtime simulation. Consumes a shape from a single
    /// south-level-0 input and emits four per-quadrant shapes onto the four
    /// cardinal level-1 outputs (N/E/S/W — TR/BR/BL/TL clockwise-spatial).
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
    /// Topology (per PLAN-P02-008 — fixes break-stall):
    /// <code>
    ///   InputLane (2-arg terminal, AcceptHook runs split)
    ///     │ HandOverItem onto staging lanes
    ///     ▼
    ///   N/E/S/W StagingLane (3-arg, downstream = respective OutputLane)
    ///     │ chain-forward (internal belt-system mechanism)
    ///     ▼
    ///   N/E/S/W OutputLane  (2-arg terminal; game pulls via IItemProvider)
    /// </code>
    ///
    /// Why the staging lane is necessary: a prior design HandOverItem'd
    /// directly onto the 2-arg terminal output lanes. Items landed at a
    /// position that the game's IItemProvider polling does NOT drain
    /// from — so when an output chain break briefly backed the lane up,
    /// items stayed stuck even after downstream cleared. Routing through
    /// a 3-arg staging lane (downstream = output) delivers items to the
    /// output via the same internal chain-forward DiagonalCutter's
    /// ProcessingLane→OutputLane uses; that mechanism positions items
    /// correctly for IItemProvider pickup.
    ///
    /// MVP scope: no delay / processing lane. The configuration's
    /// ProcessingDelay is still exposed for future expansion but is not
    /// wired into a DelayBeltLane here (CONSTRAINTS §5a: ship MVP first).
    /// </summary>
    public class FourWaySplitterSimulation : Simulation<FourWaySplitterSimulationState>, IItemSimulation, IUpdatableSimulation
    {
        public readonly BeltLane InputLane;

        public readonly BeltLane NorthStagingLane;
        public readonly BeltLane EastStagingLane;
        public readonly BeltLane SouthStagingLane;
        public readonly BeltLane WestStagingLane;

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
            // Output lanes must be constructed before staging lanes so the
            // staging 3-arg ctor can reference them as `downstream`.
            NorthOutputLane = new BeltLane(configuration.BeltSpeed, simulationState.NorthOutputLaneState);
            EastOutputLane = new BeltLane(configuration.BeltSpeed, simulationState.EastOutputLaneState);
            SouthOutputLane = new BeltLane(configuration.BeltSpeed, simulationState.SouthOutputLaneState);
            WestOutputLane = new BeltLane(configuration.BeltSpeed, simulationState.WestOutputLaneState);

            // Staging lanes chain-forward into their paired output lane.
            NorthStagingLane = new BeltLane(configuration.BeltSpeed, simulationState.NorthStagingLaneState, NorthOutputLane);
            EastStagingLane  = new BeltLane(configuration.BeltSpeed, simulationState.EastStagingLaneState,  EastOutputLane);
            SouthStagingLane = new BeltLane(configuration.BeltSpeed, simulationState.SouthStagingLaneState, SouthOutputLane);
            WestStagingLane  = new BeltLane(configuration.BeltSpeed, simulationState.WestStagingLaneState,  WestOutputLane);

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
                ShapeItem? northItem = result.North is { ResultsInEmptyShape: false } n ? shapeRegistry.GetItem(n.Shape) : null;
                ShapeItem? eastItem  = result.East  is { ResultsInEmptyShape: false } e ? shapeRegistry.GetItem(e.Shape) : null;
                ShapeItem? southItem = result.South is { ResultsInEmptyShape: false } s ? shapeRegistry.GetItem(s.Shape) : null;
                ShapeItem? westItem  = result.West  is { ResultsInEmptyShape: false } w ? shapeRegistry.GetItem(w.Shape) : null;

                // Gate against STAGING capacity (not output). Staging lanes
                // feed outputs via chain-forward, so staging backs up first
                // when downstream stalls, and clears first when it resumes.
                if (northItem != null && !NorthStagingLane.CanAcceptItem(northItem)) return;
                if (eastItem  != null && !EastStagingLane.CanAcceptItem(eastItem))   return;
                if (southItem != null && !SouthStagingLane.CanAcceptItem(southItem)) return;
                if (westItem  != null && !WestStagingLane.CanAcceptItem(westItem))   return;

                if (northItem != null) NorthStagingLane.HandOverItem(northItem, ticks);
                if (eastItem  != null) EastStagingLane.HandOverItem(eastItem, ticks);
                if (southItem != null) SouthStagingLane.HandOverItem(southItem, ticks);
                if (westItem  != null) WestStagingLane.HandOverItem(westItem, ticks);

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
            traverser.Traverse(NorthStagingLane);
            traverser.Traverse(EastStagingLane);
            traverser.Traverse(SouthStagingLane);
            traverser.Traverse(WestStagingLane);
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
            // Strictly downstream-first: output drains first, giving staging
            // somewhere to chain-forward to; staging drains next, giving
            // input's AcceptHook an output gate that reflects reality; input
            // last, so new items see fresh-state capacity.
            NorthOutputLane.Update(deltaTicks);
            EastOutputLane.Update(deltaTicks);
            SouthOutputLane.Update(deltaTicks);
            WestOutputLane.Update(deltaTicks);

            NorthStagingLane.Update(deltaTicks);
            EastStagingLane.Update(deltaTicks);
            SouthStagingLane.Update(deltaTicks);
            WestStagingLane.Update(deltaTicks);

            InputLane.Update(deltaTicks);
        }
    }
}

