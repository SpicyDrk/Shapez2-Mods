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
    /// <b>Unsupported item types (SC-09):</b> three categories of input
    /// are handled differently from the standard solid shape path:
    /// </para>
    /// <list type="number">
    ///   <item><b>Non-ShapeItem IBeltItem</b> (e.g. <c>FluidPackageItem</c>) —
    ///     wedge-rejected at the ProcessingLane AcceptHook.</item>
    ///   <item><b>ShapeItems containing crystal (code 'c') or pin
    ///     (code 'P') sub-parts</b> — also wedge-rejected. The split math
    ///     can't produce valid ShapeIds from lone crystal/pin quadrants
    ///     (crystals have special fusion rules, pins have special
    ///     connectivity rules — per the decompiled <c>ShapeLogic</c>
    ///     source), so running the split would produce all-empty
    ///     results and silently consume the item. Early reject avoids
    ///     that.</item>
    ///   <item><b>Standard solid shapes</b> (including painted) — split
    ///     normally into four per-quadrant outputs.</item>
    /// </list>
    /// <para>
    /// Wedge-reject mechanism: AcceptHook leaves <c>item</c> unchanged,
    /// so the belt system stores it on the terminal ProcessingLane.
    /// ProcessingLane has no downstream, so the item can't advance;
    /// CanAcceptItem returns false once it's there; InputLane can't
    /// chain-forward further items; InputLane fills; upstream
    /// HandOverItem fails CanAcceptItem; upstream belt backs up
    /// visibly. Classic Shapez "reject on input" UX.
    /// </para>
    /// <para>
    /// <b>Wedge auto-clear (stagnation timeout).</b> A permanently
    /// wedged item would force the player to destroy the building to
    /// recover. We avoid that via
    /// <see cref="FourWaySplitterSimulationState.ProcessingLaneStagnantTicks"/>:
    /// Update increments the counter each tick ProcessingLane has an
    /// item and resets it whenever ProcessingLane is empty. Standard
    /// shapes are consumed in-hook (item never "stays" on
    /// ProcessingLane) so the counter never accumulates for them.
    /// Wedged crystals / pins accumulate ticks and are dropped once the
    /// counter exceeds <see cref="WedgeStagnationTickLimit"/>, which is
    /// tuned to a few seconds — long enough for the player to see the
    /// upstream backup, short enough that removing the crystal source
    /// recovers the building without destroying it.
    /// </para>
    /// </summary>
    public class FourWaySplitterSimulation : Simulation<FourWaySplitterSimulationState>, IItemSimulation, IUpdatableSimulation
    {
        /// <summary>
        /// Number of Update ticks a wedged item is allowed to stay on
        /// ProcessingLane before auto-clear drops it. See SC-09 auto-clear
        /// documentation in the class-level docstring for the full rationale.
        /// Tuned conservatively — short enough that removing the crystal
        /// source recovers the building within a few seconds, long enough
        /// that the upstream backup is visible to the player.
        /// </summary>
        private const int WedgeStagnationTickLimit = 240;

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
                    // SC-09: non-shape IBeltItem (FluidPackageItem, etc.)
                    // rejected via wedge-style backpressure. Leave `item`
                    // unchanged — stored on this terminal lane with no
                    // downstream drain. ProcessingLane fills → InputLane
                    // can't chain-forward → upstream belt backs up via
                    // natural CanAcceptItem propagation.
                    return;
                }

                // SC-09 continued: reject non-standard shape sub-parts
                // (crystals and pins) BEFORE running the split. Crystals
                // (code 'c') and pins (code 'P') are the two known
                // non-standard IShapeSubPart implementations per the
                // decompiled ShapeLogic source — both have special
                // connection/fusion rules that the regular split math
                // doesn't handle. Running the split on a crystal-bearing
                // shape produces four ShapeCollapseResults with
                // Shape.IsInvalid (because a lone crystal part can't be
                // registered as a valid ShapeId) → ResultsInEmptyShape
                // returns true → our filter produces no output items →
                // the item would be silently consumed. Instead, wedge-
                // reject here so the upstream belt backs up like any
                // other unsupported item type. Painted shapes are NOT
                // caught here because paint is a color applied to
                // standard shape sub-parts (still code 'C' / 'R' / 'W' /
                // 'S' etc.) — the split math handles them correctly.
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

            // SC-09 wedge auto-clear. Track how long ProcessingLane has held
            // an item. Standard shapes are consumed in the AcceptHook so
            // never accumulate; only wedged crystal/pin items or non-shape
            // IBeltItems build up stagnation time. Once past the threshold,
            // drop the wedged item so the building recovers without the
            // player having to destroy and rebuild it.
            if (ProcessingLane.HasItem)
            {
                State.ProcessingLaneStagnantTicks++;
                if (State.ProcessingLaneStagnantTicks > WedgeStagnationTickLimit)
                {
                    ProcessingLane.Clear();
                    State.ProcessingLaneStagnantTicks = 0;
                }
            }
            else
            {
                State.ProcessingLaneStagnantTicks = 0;
            }
        }
    }
}
