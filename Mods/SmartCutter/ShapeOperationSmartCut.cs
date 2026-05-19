using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SmartCutter
{
    /// <summary>
    /// Pure shape-mask math: given an input <see cref="ShapeDefinition"/> and a
    /// wire <see cref="ShapeDefinition"/>, produce a single
    /// <see cref="ShapeCollapseResult"/> equal to the input with non-kept
    /// quadrants cut away. The wire shape's layer 1 (bottom) determines the
    /// keep-mask: quadrants filled in the wire are kept, empty quadrants are
    /// discarded.
    ///
    /// <para>Phase 1 scope:</para>
    /// <list type="bullet">
    ///   <item>Reads only layer 1 of the wire shape. Multi-layer wire flatten
    ///     is Phase 2.</item>
    ///   <item>Applies the resulting 4-quadrant mask uniformly across every
    ///     layer of the input shape (falls out naturally from the
    ///     <c>Unfold → filter by PartIndex → Collapse</c> pipeline — references
    ///     preserve LayerIndex through the filter).</item>
    ///   <item>Empty wire = empty mask = nothing kept = empty output.
    ///     Stall-on-empty-wire is Phase 2; for Phase 1 the simulation can
    ///     either let the shape op produce an empty result or short-circuit
    ///     before calling Execute — the simulation chooses.</item>
    /// </list>
    ///
    /// <para>
    /// Bypasses the <see cref="ShapeOperation{TInput, TResult}"/> base class
    /// because that base caches on a single input — our operation has two
    /// (input + wire), so the cache key wouldn't match. Caching is a Phase 3
    /// polish item; for Phase 1 we recompute on every shape that enters the
    /// AcceptHook.
    /// </para>
    /// </summary>
    public class ShapeOperationSmartCut
    {
        private readonly int MaxShapeLayers;
        private readonly IShapeIdManager ShapeIdManager;

        // ShapeRegistry is retained for symmetry with the FourWaySplitter DI
        // shape and to make a future migration to ShapeOperation<,> base
        // mechanical. Currently unused inside Execute — items are wrapped via
        // the simulation-side ShapeRegistry instead.
#pragma warning disable IDE0052
        private readonly IShapeRegistry ShapeRegistry;
#pragma warning restore IDE0052

        public ShapeOperationSmartCut(
            int maxShapeLayers,
            [DisallowNull] IShapeRegistry shapeRegistry,
            [DisallowNull] IShapeIdManager shapeIdManager)
        {
            MaxShapeLayers = maxShapeLayers;
            ShapeRegistry = shapeRegistry;
            ShapeIdManager = shapeIdManager;
        }

        /// <summary>
        /// Apply the wire shape as a keep-mask to the input shape. Returns
        /// <c>null</c> when the inputs are degenerate (null inputs, no layers
        /// in the wire shape); the caller should treat that as "do nothing"
        /// (i.e. pass the original item through unmodified or reject — the
        /// simulation decides per the empty-wire policy).
        /// </summary>
        public ShapeCollapseResult Execute(ShapeDefinition inputShape, ShapeDefinition wireShape)
        {
            if (inputShape == null || wireShape == null) return null!;

            ShapeLayer[] wireLayers = wireShape.Layers;
            if (wireLayers == null || wireLayers.Length == 0) return null!;

            ShapeLayer wireBottom = wireLayers[0];

            // Build the per-quadrant occupancy vector for the wire's bottom
            // layer. Length = inputShape.PartCount so the mask matches the
            // input's quadrant count (typical Shapez 2 mode is 4).
            int partCount = inputShape.PartCount;
            System.Span<bool> occupied = stackalloc bool[partCount];
            ShapePart[] wireParts = wireBottom.Parts;
            int n = wireParts.Length < partCount ? wireParts.Length : partCount;
            for (int i = 0; i < n; i++)
            {
                occupied[i] = !wireParts[i].IsEmpty;
            }

            int keepMask = SmartCutMask.ComputeKeepMask(occupied);

            // Unfold the input shape into per-quadrant references (preserving
            // LayerIndex per reference), filter to references whose PartIndex
            // is "kept" under the mask, then Collapse back into a single shape.
            // The Collapse call mirrors ShapeOperationFourWaySplit's pattern:
            // partCount + maxShapeLayers + shape-id-manager + the original
            // fuseReferences so multi-part fusions stay coherent.
            ShapeLogic.UnfoldResult unfolded = ShapeLogic.Unfold(inputShape.Layers);

            var keptRefs = new List<ShapePartReference>();
            foreach (ShapePartReference reference in unfolded.References)
            {
                if (SmartCutMask.IsKept(keepMask, reference.PartIndex))
                {
                    keptRefs.Add(reference);
                }
            }

            return ShapeLogic.Collapse(
                keptRefs,
                partCount,
                MaxShapeLayers,
                ShapeIdManager,
                unfolded.FusedReferences);
        }
    }
}
