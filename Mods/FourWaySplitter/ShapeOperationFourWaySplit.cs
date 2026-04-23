using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace FourWaySplitter
{
    /// <summary>
    /// Pure shape-split math: given a <see cref="ShapeDefinition"/>, produce
    /// four per-quadrant <see cref="ShapeCollapseResult"/>s (one per cardinal
    /// output). Mirrors <c>ShapeOperationDiagonalCut</c> from the official
    /// DiagonalCutter sample — same base class, same ShapeLogic primitives,
    /// same cache + DI wiring inherited from <see cref="ShapeOperation{TInput, TResult}"/>.
    ///
    /// Quadrant-to-output mapping (CONSTRAINTS.md §5b, clockwise-spatial):
    ///   TR (PartIndex 0) -> North
    ///   BR (PartIndex 1) -> East
    ///   BL (PartIndex 2) -> South
    ///   TL (PartIndex 3) -> West
    ///
    /// Multi-layer preservation: ShapeLogic.Collapse is called with the
    /// original <see cref="ShapeDefinition.PartCount"/> and the game's
    /// <c>MaxShapeLayers</c>, so per-quadrant references retain their
    /// <c>LayerIndex</c> — each output carries the full vertical column of
    /// its assigned quadrant. This matches DiagonalCutter's approach of
    /// filtering the unfolded references and re-collapsing each subset.
    ///
    /// Edge cases:
    ///   - All-empty input: Unfold produces zero references; Collapse of an
    ///     empty list returns a ShapeCollapseResult with ResultsInEmptyShape.
    ///     All four outputs are empty — acceptable (R1 input pass-through).
    ///   - Unsupported shape types (crystals, fluids, pins, painted shapes):
    ///     not handled here. The calling simulation decides reject vs.
    ///     pass-through based on <see cref="IItem"/> runtime type.
    /// </summary>
    public class ShapeOperationFourWaySplit : ShapeOperation<ShapeDefinition, FourWaySplitResult>
    {
        // Quadrant PartIndex constants (game convention: 0=TR, 1=BR, 2=BL, 3=TL).
        // Map to cardinal outputs per CONSTRAINTS §5b:
        //   TR -> North, BR -> East, BL -> South, TL -> West
        private const int QuadrantTR = 0; // -> North
        private const int QuadrantBR = 1; // -> East
        private const int QuadrantBL = 2; // -> South
        private const int QuadrantTL = 3; // -> West

        private readonly int MaxShapeLayers;

        public ShapeOperationFourWaySplit(
            int maxShapeLayers,
            [DisallowNull] IShapeRegistry shapeRegistry,
            [DisallowNull] IShapeIdManager shapeIdManager) : base(shapeRegistry, shapeIdManager)
        {
            MaxShapeLayers = maxShapeLayers;
        }

        public override FourWaySplitResult ExecuteInternal(ShapeDefinition shape)
        {
            ShapeLogic.UnfoldResult unfolded = ShapeLogic.Unfold(shape.Layers);

            // Filter the unfolded references by PartIndex == each quadrant.
            // The returned ShapePartReference list preserves LayerIndex, so
            // Collapse reassembles the full vertical column for that quadrant
            // — other quadrants are simply absent (blanked).
            var trRefs = unfolded.References.Where(r => r.PartIndex == QuadrantTR).ToList();
            var brRefs = unfolded.References.Where(r => r.PartIndex == QuadrantBR).ToList();
            var blRefs = unfolded.References.Where(r => r.PartIndex == QuadrantBL).ToList();
            var tlRefs = unfolded.References.Where(r => r.PartIndex == QuadrantTL).ToList();

            ShapeCollapseResult northResult = ShapeLogic.Collapse(
                trRefs,
                shape.PartCount,
                MaxShapeLayers,
                ShapeIdManager,
                unfolded.FusedReferences);
            ShapeCollapseResult eastResult = ShapeLogic.Collapse(
                brRefs,
                shape.PartCount,
                MaxShapeLayers,
                ShapeIdManager,
                unfolded.FusedReferences);
            ShapeCollapseResult southResult = ShapeLogic.Collapse(
                blRefs,
                shape.PartCount,
                MaxShapeLayers,
                ShapeIdManager,
                unfolded.FusedReferences);
            ShapeCollapseResult westResult = ShapeLogic.Collapse(
                tlRefs,
                shape.PartCount,
                MaxShapeLayers,
                ShapeIdManager,
                unfolded.FusedReferences);

            // TODO(P03): defined behavior per R5 — reject or pass-through
            // unsupported shape types (crystals, fluids, pins, painted).
            // The caller (FourWaySplitterSimulation) must check IItem type
            // before invoking Execute; this method assumes a standard solid.

            return new FourWaySplitResult(northResult, eastResult, southResult, westResult);
        }
    }
}
