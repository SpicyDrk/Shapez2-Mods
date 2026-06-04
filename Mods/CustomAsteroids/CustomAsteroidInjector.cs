using System;
using System.Collections.Generic;
using System.Linq;
using Game.Core.Coordinates;
using ShapezShifter.Hijack;
using ShapezShifter.Kit;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace CustomAsteroids
{
    /// <summary>
    /// PLAN-P01-001 Task 8 — the injection spike. An <see cref="ITickRewirer"/> that,
    /// on an opt-in hotkey (<see cref="InjectKey"/>), inserts a custom-shape
    /// <c>ShapeMapResourceSource</c> into the live space-map resources so a vanilla
    /// extractor placed over it mines exactly the hardcoded shape
    /// (<see cref="CustomAsteroidSpikeState.SpikeShapeCode"/>).
    ///
    /// <para><b>Insertion mechanism.</b> <c>MapSuperChunk</c> exposes no public
    /// "add resource", but the savegame loader builds a fully-populated chunk via the
    /// two-arg ctor <c>new MapSuperChunk(sc, resources)</c> (see
    /// <c>SuperChunkSerializer.TryDeserialize</c>). We mirror that: read the target
    /// chunk's existing resources, append ours, build a fresh chunk, and swap it into
    /// <c>GameResourcesMap.SuperChunksByCoordinate</c> (publicized). The
    /// <c>ResourcesOfType&lt;IShapeMapResourceSource&gt;()</c> accessor — the same one
    /// the extractor, renderer and serializer read — then returns our source.</para>
    ///
    /// <para><b>SC-03 safety.</b> Opt-in only (nothing happens without the hotkey), and
    /// we refuse to rebuild a chunk that <c>ContainsIslands</c> so player platforms are
    /// never disturbed. Everything is wrapped so a failure logs and no-ops.</para>
    /// </summary>
    internal sealed class CustomAsteroidInjector : ITickRewirer
    {
        // A GC known to sit in the hub's super-chunk; the search starts from its SC.
        private static readonly GlobalChunkCoordinate HubApprox_GC = new GlobalChunkCoordinate(-1, 0, 0);

        // How many super-chunk rings out from the hub to probe for open space.
        private const int MaxSearchRadius_SC = 8;

        private const KeyCode InjectKey = KeyCode.F8;

        private readonly CustomAsteroidSpikeState _state;
        private readonly ILogger _logger;
        private int _injectCount;
        private int _lastAttemptFrame = -1;
        private bool _loggedIdentity;
        private bool _loggedAlready;
        private GlobalChunkCoordinate? _injectedAt;

        public CustomAsteroidInjector(CustomAsteroidSpikeState state, ILogger logger)
        {
            _state = state;
            _logger = logger;
        }

        public void Tick(float deltaTime)
        {
            // Opt-in: do nothing unless the player explicitly presses the inject key.
            if (!Input.GetKeyDown(InjectKey)) return;

            // Tick can fire more than once per frame (multiple sim contexts); only act
            // on the first invocation of a given frame so one press = one inject.
            int frame = Time.frameCount;
            if (frame == _lastAttemptFrame) return;
            _lastAttemptFrame = frame;

            TryInject();
        }

        private void TryInject()
        {
            try
            {
                if (_injectedAt.HasValue)
                {
                    if (!_loggedAlready)
                    {
                        _loggedAlready = true;
                        _logger.Info?.Log(
                            $"[CustomAsteroids:inject] already injected this session at GC {_injectedAt.Value} " +
                            $"(world≈{_injectedAt.Value.ToCenter_W()}). Further F8 presses ignored.");
                    }
                    return;
                }
                if (!_state.Captured || _state.ResourcesMap == null)
                {
                    _logger.Warning?.Log("[CustomAsteroids:inject] F8 pressed but handles not captured yet (open a space-map game first).");
                    return;
                }
                // Resolve the shape through the CANONICAL registry (the one the miner
                // + HUD use). ShapeIds are sequential and per-ShapeIdManager-instance,
                // so a definition built from the captured (non-canonical) manager mines
                // as the wrong shape: its id means something else in the canonical
                // manager. See PLAN-P01-001 "Shape identity" note.
                if (!TryResolveCanonicalShape(out ShapeDefinition shape, out string shapeDiag))
                {
                    _logger.Warning?.Log($"[CustomAsteroids:inject] could not resolve the spike shape canonically ({shapeDiag}); aborting.");
                    return;
                }
                if (_state.ResourcesMap is not GameResourcesMap grm)
                {
                    _logger.Error?.Log($"[CustomAsteroids:inject] ResourcesMap is {_state.ResourcesMap.GetType().Name}, not GameResourcesMap — cannot reach the chunk dictionary.");
                    return;
                }

                // Find the nearest island-free super-chunk so we never rebuild a chunk
                // that overlaps a player platform (SC-03 safety), then place the asteroid
                // at that super-chunk's origin tile.
                SuperChunkCoordinate hubSc = HubApprox_GC.To_SC();
                if (!TryFindOpenChunk(grm, hubSc, out MapSuperChunk chunk, out SuperChunkCoordinate sc))
                {
                    _logger.Warning?.Log(
                        $"[CustomAsteroids:inject] no island-free super-chunk found within {MaxSearchRadius_SC} rings of {hubSc}. " +
                        "Explore outward and retry, or raise MaxSearchRadius_SC.");
                    return;
                }

                GlobalChunkCoordinate targetTile_GC = chunk.Origin_GC;
                List<IMapResourceSource> resources = chunk.ResourcesOfType<IMapResourceSource>().ToList();

                // Nudge to a free tile if the chunk origin already holds a vanilla patch.
                for (int bump = 0; bump < 4 && resources.Any(r => r.ChunksLookup_G.Contains(targetTile_GC)); bump++)
                {
                    targetTile_GC += new ChunkVector(1, 1, 0);
                }
                if (resources.Any(r => r.ChunksLookup_G.Contains(targetTile_GC)))
                {
                    _logger.Warning?.Log($"[CustomAsteroids:inject] couldn't find a free tile near {chunk.Origin_GC} in SC {sc}; aborting.");
                    return;
                }

                var mine = new ShapeMapResourceSource(
                    targetTile_GC,
                    new[] { new ChunkVector(0, 0, 0) },
                    new[] { shape });
                resources.Add(mine);

                var rebuilt = new MapSuperChunk(sc, resources);
                grm.SuperChunksByCoordinate[sc] = rebuilt;

                // Verify via the same accessor the extractor/renderer/serializer use.
                IReadOnlyList<IShapeMapResourceSource> shapesNow = rebuilt.ResourcesOfType<IShapeMapResourceSource>();
                bool present = shapesNow.Any(s => s.ChunksLookup_G.Contains(targetTile_GC));
                var worldPos = targetTile_GC.ToCenter_W();
                _injectCount++;
                _injectedAt = targetTile_GC;

                _logger.Info?.Log(
                    $"[CustomAsteroids:inject] INJECTED '{CustomAsteroidSpikeState.SpikeShapeCode}' (canonical {shapeDiag}) at GC {targetTile_GC} " +
                    $"(SC {sc}, world≈{worldPos}). Chunk now has {shapesNow.Count} shape resource(s); ourTilePresent={present}. " +
                    "Enable the shape-resources visualization (zoom out) to spot it, then build a platform + extractor over that tile.");
            }
            catch (Exception ex)
            {
                _logger.Error?.Log($"[CustomAsteroids:inject] threw (non-fatal): {ex}");
            }
        }

        /// <summary>
        /// Resolve <see cref="CustomAsteroidSpikeState.SpikeShapeCode"/> into a
        /// <see cref="ShapeDefinition"/> via the CANONICAL <c>ShapeRegistry</c>
        /// (<c>GameHelper.Core.ShapeRegistry</c>) and its paired <c>ShapeIdManager</c>,
        /// so the resulting ShapeId is the one the miner, HUD and serializer agree on.
        /// </summary>
        private bool TryResolveCanonicalShape(out ShapeDefinition shape, out string diag)
        {
            shape = null!;
#pragma warning disable CS0618 // IGameSessionManagers is [Obsolete] — a tick rewirer has no DI seam.
            IGameSessionManagers? sessions = GameHelper.Core;
#pragma warning restore CS0618
            if (sessions == null) { diag = "GameHelper.Core is null (not in a game session)"; return false; }

            ShapeRegistry registry = sessions.ShapeRegistry;
            if (registry == null) { diag = "canonical ShapeRegistry is null"; return false; }

            IShapeIdManager idMgr = registry.ShapeIdManager; // publicized private field
            if (idMgr == null) { diag = "canonical ShapeIdManager is null"; return false; }

            // One-time identity check: confirm the captured (rewirer) registry/manager
            // are or aren't the same instances as the canonical ones (the root-cause
            // diagnostic for the XgXg---- mismatch).
            if (!_loggedIdentity)
            {
                _loggedIdentity = true;
                _logger.Info?.Log(
                    "[CustomAsteroids:inject] registry identity — " +
                    $"ShapeRegistry captured==canonical:{ReferenceEquals(_state.ShapeRegistry, registry)}, " +
                    $"ShapeIdManager captured==canonical:{ReferenceEquals(_state.ShapeIdManager, idMgr)}.");
            }

            ShapeId id = idMgr.Resolve(CustomAsteroidSpikeState.SpikeShapeCode);
            if (!registry.TryGetDefinition(id, out shape))
            {
                diag = $"canonical TryGetDefinition failed for id #{id.Uid}";
                return false;
            }
            diag = $"id=#{id.Uid}, layers={shape.Layers.Length}";
            return true;
        }

        /// <summary>
        /// Spiral outward from <paramref name="hubSc"/> and return the first super-chunk
        /// that does not overlap any island (player platform).
        /// </summary>
        private bool TryFindOpenChunk(
            GameResourcesMap grm, SuperChunkCoordinate hubSc,
            out MapSuperChunk chunk, out SuperChunkCoordinate sc)
        {
            for (int r = 1; r <= MaxSearchRadius_SC; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dy = -r; dy <= r; dy++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue; // ring edge only
                        var candidate = new SuperChunkCoordinate(hubSc.x + dx, hubSc.y + dy);
                        MapSuperChunk c = grm.GetOrCreateSuperChunkAt_SC(candidate);
                        if (c.ContainsIslands) continue;
                        chunk = c;
                        sc = candidate;
                        return true;
                    }
                }
            }
            chunk = null!;
            sc = default;
            return false;
        }

        public bool Equals(IRewirer other) => ReferenceEquals(this, other);
    }
}
