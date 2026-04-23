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
    /// decision trail. If <c>AtomicBuildings.Extend().WithSimulation(...)</c>
    /// rejects this non-1In2Out shape, PLAN-P02-002 Task 5 will STOP rather
    /// than force it.
    ///
    /// Flow:
    ///   1. Input lane's AcceptHook fires when an upstream belt hands us a
    ///      ShapeItem — we run the split, cache the 4-way result, flag
    ///      HasPendingResult, and consume the incoming item (item = null).
    ///   2. On every Update tick, if we have a pending result AND all four
    ///      output lanes <c>CanAcceptItem</c>, we emit one ShapeItem per
    ///      cardinal and clear the pending flag.
    ///
    /// MVP scope: no delay / processing lane. The configuration's
    /// ProcessingDelay is still exposed for future expansion but is not
    /// wired into a DelayBeltLane here (CONSTRAINTS §5a: ship MVP first).
    ///
    /// Mirrors <c>DiagonalCutterSimulation</c>'s structure where possible
    /// (ctor signature, Traverse / ClearContent / Update shape, lane
    /// initialization order).
    /// </summary>
    public class FourWaySplitterSimulation : Simulation<FourWaySplitterSimulationState>, IItemSimulation, IUpdatableSimulation
    {
        public FourWaySplitResult CurrentResult => State.CurrentResult;
        public bool HasPendingResult => State.HasPendingResult;

        public readonly BeltLane InputLane;
        public readonly BeltLane NorthOutputLane;
        public readonly BeltLane EastOutputLane;
        public readonly BeltLane SouthOutputLane;
        public readonly BeltLane WestOutputLane;

        private readonly IShapeRegistry _ShapeRegistry;

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
            _ShapeRegistry = shapeRegistry;

            // Output lanes: 2-arg BeltLane (speed + state, no downstream
            // receiver). Same idiom as DiagonalCutter's OutputLane.
            NorthOutputLane = new BeltLane(configuration.BeltSpeed, simulationState.NorthOutputLaneState);
            EastOutputLane = new BeltLane(configuration.BeltSpeed, simulationState.EastOutputLaneState);
            SouthOutputLane = new BeltLane(configuration.BeltSpeed, simulationState.SouthOutputLaneState);
            WestOutputLane = new BeltLane(configuration.BeltSpeed, simulationState.WestOutputLaneState);

            // Input lane: 2-arg BeltLane (no downstream) — we consume
            // items in-place via the AcceptHook. Setting item = null at
            // end of the hook prevents forwarding.
            InputLane = new BeltLane(configuration.BeltSpeed, simulationState.InputLaneState);
            InputLane.AcceptHook = (IItemReceiver _, ref IBeltItem item, ref Ticks _) =>
            {
                // Guard: only standard solid shapes. Per CONSTRAINTS §5b
                // MUST: unsupported types have explicit handling. MVP choice
                // for Task 3: if not a ShapeItem, pass-through (leave item
                // as-is — upstream belt retains it). Reject vs. pass-through
                // policy is formally decided in P03 (STATE.md parking lot).
                if (item is not ShapeItem shapeItem)
                {
                    return;
                }

                // If we already have a pending split, don't accept another
                // input — leave the item on the upstream belt by setting
                // it back (the belt system will retry). We only accept
                // when we're idle.
                if (State.HasPendingResult)
                {
                    return;
                }

                ShapeDefinition definition = shapeItem.Definition;
                FourWaySplitResult result = fourWaySplit.Execute(definition);

                State.CurrentResult = result;
                State.HasPendingResult = true;

                // Consume the incoming item — we'll re-emit 4 items from
                // Update when the outputs have capacity. `null!` suppresses
                // CS8625: the AcceptHookDelegate's `ref IBeltItem` is
                // non-nullable by annotation but the belt system explicitly
                // supports null here as the "consume" signal (DiagonalCutter
                // uses this same pattern in its OutputLane.AcceptHook).
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
            State.HasPendingResult = false;
        }

        /// <inheritdoc />
        public void Update(Ticks startTicks, Ticks deltaTicks)
        {
            // Update all lanes first — this moves existing items along.
            NorthOutputLane.Update(deltaTicks);
            EastOutputLane.Update(deltaTicks);
            SouthOutputLane.Update(deltaTicks);
            WestOutputLane.Update(deltaTicks);
            InputLane.Update(deltaTicks);

            // If we have a pending split result and all 4 output lanes
            // have capacity, emit one shape item per cardinal.
            if (State.HasPendingResult)
            {
                TryEmitPendingResult(startTicks);
            }
        }

        private void TryEmitPendingResult(Ticks startTicks)
        {
            ShapeItem northItem = _ShapeRegistry.GetItem(State.CurrentResult.North?.Shape ?? ShapeId.Invalid);
            ShapeItem eastItem = _ShapeRegistry.GetItem(State.CurrentResult.East?.Shape ?? ShapeId.Invalid);
            ShapeItem southItem = _ShapeRegistry.GetItem(State.CurrentResult.South?.Shape ?? ShapeId.Invalid);
            ShapeItem westItem = _ShapeRegistry.GetItem(State.CurrentResult.West?.Shape ?? ShapeId.Invalid);

            // Gate: all four outputs must either (a) have capacity for
            // their non-empty item, or (b) be marked empty (null item -
            // ResultsInEmptyShape — we skip emission for that lane).
            // We check capacity only for the lanes that actually have an
            // item to push; empty-quadrant lanes don't need room.
            if (northItem != null && !NorthOutputLane.CanAcceptItem(northItem))
            {
                return;
            }
            if (eastItem != null && !EastOutputLane.CanAcceptItem(eastItem))
            {
                return;
            }
            if (southItem != null && !SouthOutputLane.CanAcceptItem(southItem))
            {
                return;
            }
            if (westItem != null && !WestOutputLane.CanAcceptItem(westItem))
            {
                return;
            }

            // All gates passed — emit the non-empty quadrants.
            if (northItem != null)
            {
                NorthOutputLane.HandOverItem(northItem, startTicks);
            }
            if (eastItem != null)
            {
                EastOutputLane.HandOverItem(eastItem, startTicks);
            }
            if (southItem != null)
            {
                SouthOutputLane.HandOverItem(southItem, startTicks);
            }
            if (westItem != null)
            {
                WestOutputLane.HandOverItem(westItem, startTicks);
            }

            State.HasPendingResult = false;
        }
    }
}

