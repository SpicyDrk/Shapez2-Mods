namespace FourWaySplitter
{
    /// <summary>
    /// Result of a four-way split: one <see cref="ShapeCollapseResult"/> per
    /// cardinal output. Mirrors the upstream <c>ShapeDiagonalCutResult</c>
    /// pattern (readonly struct, public readonly fields, ctor-assigns-all).
    ///
    /// Quadrant-to-output mapping (CONSTRAINTS.md §5b, clockwise-spatial):
    ///   TR (PartIndex 0) -> North
    ///   BR (PartIndex 1) -> East
    ///   BL (PartIndex 2) -> South
    ///   TL (PartIndex 3) -> West
    ///
    /// Any field may have <c>ResultsInEmptyShape == true</c> when the input
    /// has no content in that quadrant's vertical column — the field is
    /// still present (never null) so downstream output lanes always see a
    /// well-defined value.
    /// </summary>
    public readonly struct FourWaySplitResult
    {
        public readonly ShapeCollapseResult North;
        public readonly ShapeCollapseResult East;
        public readonly ShapeCollapseResult South;
        public readonly ShapeCollapseResult West;

        public FourWaySplitResult(
            ShapeCollapseResult north,
            ShapeCollapseResult east,
            ShapeCollapseResult south,
            ShapeCollapseResult west)
        {
            North = north;
            East = east;
            South = south;
            West = west;
        }
    }
}
