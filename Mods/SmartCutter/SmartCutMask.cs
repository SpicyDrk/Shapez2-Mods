using System;

namespace SmartCutter
{
    /// <summary>
    /// Pure helper for the SmartCutter's keep-mask math. Decoupled from any
    /// game type so it can be unit-tested in a plain console-app context
    /// (Tests/SmartCutter.Tests links this file directly — no game runtime
    /// is involved in the test).
    ///
    /// Semantics (Phase 1):
    /// <list type="bullet">
    ///   <item>The wire shape's layer 1 (bottom) is the source of truth.</item>
    ///   <item>Each quadrant (PartIndex 0..3) contributes one bit to the mask:
    ///     set if the wire's quadrant at that index is non-empty, clear if
    ///     empty.</item>
    ///   <item>Phase 2 extends <see cref="ComputeKeepMask(ReadOnlySpan{bool})"/>
    ///     to flatten across multiple wire layers — but the caller-facing
    ///     contract stays the same: bool[] in, int bitmask out.</item>
    /// </list>
    /// </summary>
    public static class SmartCutMask
    {
        /// <summary>
        /// Builds a 4-bit (per-quadrant) keep-mask from a layer's occupancy
        /// vector. Bit i = 1 means PartIndex i is "kept" (filled in the wire
        /// shape's bottom layer); bit i = 0 means "cut away".
        /// Input lengths &gt; 4 are truncated — only the first 4 quadrants
        /// participate, matching Shapez 2's standard 4-quadrant shape model.
        /// </summary>
        public static int ComputeKeepMask(ReadOnlySpan<bool> quadrantOccupied)
        {
            int mask = 0;
            int n = quadrantOccupied.Length < 4 ? quadrantOccupied.Length : 4;
            for (int i = 0; i < n; i++)
            {
                if (quadrantOccupied[i]) mask |= 1 << i;
            }
            return mask;
        }

        /// <summary>
        /// True iff PartIndex <paramref name="partIndex"/> is kept under the
        /// given mask. Out-of-range part indices return false (defensive — the
        /// caller should already be operating on PartIndex 0..PartCount-1).
        /// </summary>
        public static bool IsKept(int keepMask, int partIndex)
        {
            if (partIndex < 0 || partIndex >= 32) return false;
            return (keepMask & (1 << partIndex)) != 0;
        }
    }
}
