using System.Collections.Generic;
using Game.Core.Coordinates;
using ILogger = Core.Logging.ILogger;

namespace AsteroidForge
{
    /// <summary>
    /// PLAN-P02-001 Task 4 / PLAN-P03-001 — adds + removes custom-shape mineable asteroids on the
    /// live space map.
    ///
    /// <para><b>In-place insertion.</b> Decompiling <c>MapSuperChunk</c> shows it holds private
    /// collections — <c>AllResources</c>, <c>ResourcesLookup_GC</c> (what <c>GetResourceSource</c>
    /// reads) and a <c>CachedTypedResources</c> cache — plus a private <c>_Islands</c> set behind
    /// <c>ContainsIslands</c>. The two-arg ctor starts <c>_Islands</c> EMPTY, so rebuilding +
    /// swapping a chunk would orphan its platforms. We instead mirror the class's own
    /// <c>AddResourceSource</c>/<c>TryRegisterResource</c> on the LIVE chunk (publicized
    /// <c>Game.Core.Map</c>): register each footprint chunk in <c>ResourcesLookup_GC</c>, append to
    /// <c>AllResources</c>, clear the cache. Islands are untouched, so we can place next to / under
    /// platforms — which is how mining works. Removal (<see cref="TryRemoveAt"/>) is the reverse.</para>
    ///
    /// <para><b>Multi-tile patch.</b> One <c>ShapeMapResourceSource</c> spans a footprint — the click
    /// fallback is a 9×4 rectangle (a full space belt: 9 wide × 4 deep), or an explicit rectangle from
    /// the box-select drag (PLAN-P04-001). Boosting cap is <c>MaxShapeMinerChains + 1 = 4</c>, so 4-deep
    /// feeds a fully chained extractor across the 9-wide run; a single source draws ONE overview icon
    /// (<c>HUDShapeResourcesVisualization</c> draws one per source). The footprint is clipped to one
    /// super-chunk (<c>GetResourceAt_GC</c> resolves a source only within <c>gc.To_SC()</c>).</para>
    /// </summary>
    internal static class AsteroidPlacer
    {
        /// <summary>
        /// Default click-to-place footprint (chunks): a full space belt = 9 wide × 4 deep (36 chunks),
        /// centred on the clicked tile. This is the click fallback; a drag-box (PLAN-P04-001) sizes the
        /// patch explicitly. <see cref="DefaultWidth"/> runs along x, <see cref="DefaultDepth"/> along y.
        /// </summary>
        private const int DefaultWidth = 9;
        private const int DefaultDepth = 4;

        /// <summary>
        /// Placement entry (UI click): refuse a click on an existing patch, build the square
        /// footprint, add it in place, and report the offsets actually placed so persistence can
        /// record + restore the exact patch.
        /// </summary>
        public static bool TryInjectAt(
            GameResourcesMap grm,
            ShapeDefinition shape,
            GlobalChunkCoordinate targetTile_GC,
            ILogger logger,
            out string diag,
            out List<ChunkVector> placedOffsets)
        {
            placedOffsets = new List<ChunkVector>();

            MapSuperChunk chunk = grm.GetOrCreateSuperChunkAt_SC(targetTile_GC.To_SC());
            if (chunk.GetResourceSource(targetTile_GC) != null)
            {
                diag = $"{targetTile_GC} already holds a resource patch — pick clear space " +
                       "(replacing/editing is Phase 3)";
                return false;
            }

            // Centre the default 9×4 footprint on the clicked tile (origin included).
            int halfW = DefaultWidth / 2;   // 4 → x in [-4, 4] (9 wide)
            int halfD = DefaultDepth / 2;   // 2 → y in [-2, 1] (4 deep)
            var requested = new List<ChunkVector>();
            for (int dx = -halfW; dx <= DefaultWidth - 1 - halfW; dx++)
            {
                for (int dy = -halfD; dy <= DefaultDepth - 1 - halfD; dy++)
                {
                    requested.Add(new ChunkVector(dx, dy, 0));
                }
            }

            return TryAddSource(grm, shape, targetTile_GC, requested, logger, out diag, out placedOffsets);
        }

        /// <summary>
        /// Shared in-place add with EXPLICIT offsets — used by placement and by persistence
        /// re-injection. Filters the requested offsets to free, same-super-chunk tiles, builds one
        /// <c>ShapeMapResourceSource</c>, and registers it on the live chunk (never rebuilds).
        /// <paramref name="placedOffsets"/> returns the offsets actually added.
        /// </summary>
        public static bool TryAddSource(
            GameResourcesMap grm,
            ShapeDefinition shape,
            GlobalChunkCoordinate origin_GC,
            IReadOnlyList<ChunkVector> requestedOffsets,
            ILogger logger,
            out string diag,
            out List<ChunkVector> placedOffsets)
        {
            SuperChunkCoordinate sc = origin_GC.To_SC();
            MapSuperChunk chunk = grm.GetOrCreateSuperChunkAt_SC(sc);

            placedOffsets = new List<ChunkVector>();
            var definitions = new List<ShapeDefinition>();
            int clippedToSC = 0;
            int clippedOccupied = 0;
            foreach (ChunkVector offset in requestedOffsets)
            {
                GlobalChunkCoordinate tile = origin_GC + offset;
                if (!tile.To_SC().Equals(sc)) { clippedToSC++; continue; }
                if (chunk.ResourcesLookup_GC.ContainsKey(tile)) { clippedOccupied++; continue; }
                placedOffsets.Add(offset);
                definitions.Add(shape);
            }

            if (placedOffsets.Count == 0)
            {
                diag = $"no free tiles at {origin_GC} (clipped: {clippedToSC} cross-SC, {clippedOccupied} occupied)";
                return false;
            }

            var source = new ShapeMapResourceSource(origin_GC, placedOffsets, definitions);
            foreach (GlobalChunkCoordinate g in source.Chunks_G)
            {
                chunk.ResourcesLookup_GC.Add(g, source);
            }
            chunk.AllResources.Add(source);
            chunk.CachedTypedResources.Clear();

            bool present = ReferenceEquals(chunk.GetResourceSource(origin_GC), source);
            diag = $"origin={origin_GC}, footprint={placedOffsets.Count} chunks " +
                   $"(clipped: {clippedToSC} cross-SC, {clippedOccupied} occupied), " +
                   $"islandsPreserved={chunk.ContainsIslands}, present={present}";
            return present;
        }

        /// <summary>
        /// Remove the resource source covering <paramref name="tile"/> from the live chunk (the
        /// reverse of <see cref="TryAddSource"/>): drop every one of its <c>Chunks_G</c> from
        /// <c>ResourcesLookup_GC</c>, remove it from <c>AllResources</c>, clear the type cache. Used
        /// by delete (SC-07) / undo (SC-09). Returns false if no source covers the tile.
        /// </summary>
        public static bool TryRemoveAt(
            GameResourcesMap grm,
            GlobalChunkCoordinate tile,
            ILogger logger,
            out string diag)
        {
            MapSuperChunk chunk = grm.GetOrCreateSuperChunkAt_SC(tile.To_SC());
            IMapResourceSource source = chunk.GetResourceSource(tile);
            if (source == null)
            {
                diag = $"no resource source at {tile}";
                return false;
            }

            int removedTiles = 0;
            foreach (GlobalChunkCoordinate g in source.Chunks_G)
            {
                if (chunk.ResourcesLookup_GC.Remove(g)) removedTiles++;
            }
            chunk.AllResources.Remove(source);
            chunk.CachedTypedResources.Clear();

            bool gone = chunk.GetResourceSource(tile) == null;
            diag = $"removed source covering {tile} ({removedTiles} tiles), gone={gone}";
            return gone;
        }
    }
}
