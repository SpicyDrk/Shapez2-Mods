using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace FourWaySplitter
{
    /// <summary>
    /// Prediction-only adapter for the FourWaySplitter. Extends
    /// <see cref="ShapeOperation{TInput, TResult}"/> with the
    /// <c>ShapeCollapseResult</c> result type that the game's
    /// <c>Processing1In1OutPredictionSimulation</c> expects (same base shape as
    /// <c>ShapeOperationDiagonalCut</c> in the official DiagonalCutter sample).
    ///
    /// WHY THIS EXISTS — Shifter's installed workshop build v1.0.0 has an
    /// unguarded NRE at <c>AtomicBuildingExtender.cs:158</c> when
    /// <c>LazyPredictionExtender</c> is null; our <c>.WithoutPrediction()</c>
    /// path left it null, which tanks the mod at load time. Providing a
    /// non-null 1-in-1-out prediction factory via
    /// <see cref="Operation1In4OutPredictionFactoryBuilder"/> sidesteps the
    /// bug until the workshop catches up to upstream fix <c>54d5e38</c>.
    /// See UAT-P02.md and PLAN-P02-005.
    ///
    /// STRATEGY — north-projection adapter (Plan-005 approach b, recommended).
    /// Runs the same Unfold → per-quadrant filter → Collapse pipeline as
    /// <see cref="ShapeOperationFourWaySplit"/>, but returns only the
    /// <em>north</em> quadrant (PartIndex 0 / TR) as the single output. This
    /// matches <c>ShapeOperationDiagonalCut</c>'s 1-in-1-out contract exactly
    /// (one PartIndex-masked Collapse) and is guaranteed to satisfy whatever
    /// invariant the game's prediction tick enforces.
    ///
    /// Prediction accuracy is <em>deliberately limited</em>: the HUD / belt-
    /// placement preview will hint "the north output yields one quadrant"
    /// instead of "four cardinal outputs each yield one quadrant". The real
    /// <see cref="FourWaySplitterSimulation"/> (which uses
    /// <see cref="ShapeOperationFourWaySplit"/>) still produces all four
    /// outputs at run-time. The prediction-vs-simulation disagreement is an
    /// accepted v1 limitation; a true 1-in-4-out prediction requires a custom
    /// <c>IItemPredictionSimulation</c> and is deferred to a future story.
    ///
    /// DO NOT use this adapter for the real simulation — it drops 3 of the 4
    /// quadrants. It is referenced only from
    /// <see cref="Operation1In4OutPredictionFactoryBuilder.BuildFactory"/>.
    /// </summary>
    public class ShapeOperationFourWaySplitPredictionAdapter : ShapeOperation<ShapeDefinition, ShapeCollapseResult>, IItemOperation1In1Out
    {
        // North output corresponds to PartIndex 0 (TR quadrant) per
        // CONSTRAINTS §5b clockwise-spatial mapping (TR→N / BR→E / BL→S / TL→W).
        // Kept as a local const to mirror ShapeOperationFourWaySplit's style.
        private const int QuadrantTR = 0; // -> North

        private readonly int MaxShapeLayers;

        public ShapeOperationFourWaySplitPredictionAdapter(
            int maxShapeLayers,
            [DisallowNull] IShapeRegistry shapeRegistry,
            [DisallowNull] IShapeIdManager shapeIdManager) : base(shapeRegistry, shapeIdManager)
        {
            MaxShapeLayers = maxShapeLayers;
        }

        public override ShapeCollapseResult ExecuteInternal(ShapeDefinition shape)
        {
            ShapeLogic.UnfoldResult unfolded = ShapeLogic.Unfold(shape.Layers);

            // Filter to the TR (north) quadrant only. LayerIndex is preserved
            // on each reference so Collapse reassembles the full vertical
            // column for the north quadrant — other quadrants are simply
            // absent (blanked). Same primitive call pattern as
            // ShapeOperationFourWaySplit's four-way Collapse, just one branch.
            var trRefs = unfolded.References.Where(r => r.PartIndex == QuadrantTR).ToList();

            return ShapeLogic.Collapse(
                trRefs,
                shape.PartCount,
                MaxShapeLayers,
                ShapeIdManager,
                unfolded.FusedReferences);
        }

        /// <summary>
        /// <see cref="IItemOperation1In1Out"/> entry point used by the game's
        /// 1-in-1-out prediction simulation. Unwraps the incoming
        /// <see cref="ShapeItem"/>, runs the north-projection via
        /// <see cref="ExecuteInternal"/>, and wraps the resulting
        /// <see cref="ShapeCollapseResult.Shape"/> back into a
        /// <see cref="ShapeItem"/> via <see cref="ShapeOperation{TInput, TResult}.ShapeRegistry"/>.
        ///
        /// Non-shape items (fluids, pins, crystals, painted shapes) are
        /// rejected: return false with a null outItem — matches the game's
        /// <c>IItemOperation1In1Out</c> convention of "this operation does
        /// not apply, hand the item back". Empty-shape results likewise
        /// return false (the prediction visualizer treats "no output" as
        /// a degenerate case, same as DiagonalCutter on an empty input).
        /// </summary>
        public bool TryExecute(IItem inItem, out IItem outItem)
        {
            if (inItem is not ShapeItem shapeItem)
            {
                outItem = null!;
                return false;
            }

            ShapeCollapseResult result = Execute(shapeItem.Definition);
            if (result == null || result.ResultsInEmptyShape)
            {
                outItem = null!;
                return false;
            }

            outItem = ShapeRegistry.GetItem(result.Shape);
            return true;
        }
    }
}
