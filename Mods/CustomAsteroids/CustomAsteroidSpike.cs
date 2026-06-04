using System;
using System.Collections.Generic;
using Game.Core.Simulation;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace CustomAsteroids
{
    /// <summary>
    /// Shared spike state — the handles the injection (PLAN-P01-001 Task 8) needs,
    /// captured once by <see cref="CustomAsteroidSpikeRewirer"/> when the game
    /// builds its simulation systems. Kept separate from the rewirer so the Task-8
    /// debug trigger (a tick rewirer reading these cached refs) can act long after
    /// the capture frame.
    /// </summary>
    internal sealed class CustomAsteroidSpikeState
    {
        /// <summary>The live space-map resource map (where mineable asteroids live).</summary>
        public IGameResourcesMap? ResourcesMap;

        /// <summary>
        /// The game's shape registry. Backed by the configured shape factory —
        /// <see cref="IShapeRegistry.TryGetDefinition"/> CONSTRUCTS (and caches into
        /// the live registry) a definition for ANY valid hash, so arbitrary authored
        /// shapes resolve here without pre-registration.
        /// </summary>
        public IShapeRegistry? ShapeRegistry;

        /// <summary>Hash &lt;-&gt; ShapeId mapping; <c>Resolve(hash)</c> feeds the registry lookup.</summary>
        public IShapeIdManager? ShapeIdManager;

        public bool Captured;

        /// <summary>The hardcoded spike shape code (Task 8 injection payload).</summary>
        public const string SpikeShapeCode = "CrCgCbCy:P-P-P-P-:crcgcbcy";

        /// <summary>
        /// The parsed spike shape, resolved once at capture time. The injector
        /// (PLAN-P01-001 Task 8) puts this into a <c>ShapeMapResourceSource</c>.
        /// </summary>
        public ShapeDefinition? SpikeShape;

        /// <summary>
        /// Parse a shape code into a <see cref="ShapeDefinition"/> via the live
        /// registry (factory-backed). Returns null on invalid code or if the
        /// handles were never captured.
        /// </summary>
        public ShapeDefinition? TryParse(string shapeCode)
        {
            if (ShapeRegistry == null || ShapeIdManager == null) return null;
            ShapeId id = ShapeIdManager.Resolve(shapeCode);
            return ShapeRegistry.TryGetDefinition(id, out ShapeDefinition def) ? def : null;
        }
    }

    /// <summary>
    /// PLAN-P01-001 Task 7 — reachability proof for the Custom Asteroids spike.
    ///
    /// <para>
    /// Implements <see cref="ISimulationSystemsRewirer"/> purely to CAPTURE (it does
    /// not modify the systems collection). When the game builds its simulation
    /// systems, <see cref="SimulationSystemsDependencies"/> hands us — in one
    /// publicly-supported Shifter seam — the live <c>ResourcesMap</c> (space-map
    /// asteroid container), the <c>ShapeRegistry</c>, and the <c>ShapeIdManager</c>.
    /// We stash them in <see cref="CustomAsteroidSpikeState"/> for Task 8's injection
    /// and log a parse table proving arbitrary shape codes (incl. crystals/pins,
    /// multi-layer) resolve to <see cref="ShapeDefinition"/>s — the exact type the
    /// mineable <c>ShapeMapResourceSource</c> wants.
    /// </para>
    ///
    /// <para>This task does NOT mutate the map. Vanilla saves load untouched.</para>
    /// </summary>
    internal sealed class CustomAsteroidSpikeRewirer : ISimulationSystemsRewirer
    {
        // Candidate codes, simplest first. The simplest are almost-certainly valid
        // (baseline sanity); the later multi-layer crystal/pin entries are the real
        // Task-8 target — whichever parse OK here tells us the valid grammar for the
        // hardcoded injection shape (the exact part/color chars live in content
        // config, not in source, so we let the live parser adjudicate).
        private static readonly string[] CandidateCodes =
        {
            "CuCuCuCu",                          // baseline: 4 uncolored circles
            "CrWgSbCy",                          // colored, mixed shapes (1 layer)
            "CrCgCbCy:RrRgRbRy",                 // 2 layers, colored
            "crcrcrcr",                          // crystals
            "P-P-P-P-",                          // pins
            "CrCgCbCy:P-P-P-P-:crcgcbcy",        // TARGET: colored + pins + crystals, 3 layers
        };

        private readonly CustomAsteroidSpikeState _state;
        private readonly ILogger _logger;
        private bool _loggedParseTable;

        public CustomAsteroidSpikeRewirer(CustomAsteroidSpikeState state, ILogger logger)
        {
            _state = state;
            _logger = logger;
        }

        public void ModifySimulationSystems(
            ICollection<ISimulationSystem> simulationSystems,
            SimulationSystemsDependencies dependencies)
        {
            // Capture only — never touch `simulationSystems`.
            try
            {
                _state.ResourcesMap = dependencies.ResourcesMap;
                _state.ShapeRegistry = dependencies.ShapeRegistry;
                _state.ShapeIdManager = dependencies.ShapeIdManager;
                _state.Captured = _state.ResourcesMap != null
                                  && _state.ShapeRegistry != null
                                  && _state.ShapeIdManager != null;

                _logger.Info?.Log(
                    $"[CustomAsteroids:spike] ModifySimulationSystems fired (mode={dependencies.Mode}). " +
                    $"ResourcesMap={(dependencies.ResourcesMap != null ? "OK" : "NULL")}, " +
                    $"ShapeRegistry={(dependencies.ShapeRegistry != null ? "OK" : "NULL")}, " +
                    $"ShapeIdManager={(dependencies.ShapeIdManager != null ? "OK" : "NULL")}.");

                if (!_loggedParseTable && _state.Captured)
                {
                    _loggedParseTable = true;
                    LogParseTable();

                    // Resolve the Task-8 injection payload once and stash it.
                    _state.SpikeShape = _state.TryParse(CustomAsteroidSpikeState.SpikeShapeCode);
                    _logger.Info?.Log(_state.SpikeShape != null
                        ? $"[CustomAsteroids:spike] injection payload ready: '{CustomAsteroidSpikeState.SpikeShapeCode}' " +
                          $"(id=#{_state.SpikeShape.Id.Uid}, layers={_state.SpikeShape.Layers.Length}). Press F8 in-game to inject."
                        : $"[CustomAsteroids:spike] WARNING: injection payload '{CustomAsteroidSpikeState.SpikeShapeCode}' failed to parse.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error?.Log($"[CustomAsteroids:spike] capture/parse threw (non-fatal): {ex}");
            }
        }

        private void LogParseTable()
        {
            _logger.Info?.Log("[CustomAsteroids:spike] shape-code parse table (proving factory-backed registry is reachable):");
            foreach (string code in CandidateCodes)
            {
                ShapeDefinition? def = _state.TryParse(code);
                if (def != null)
                {
                    _logger.Info?.Log(
                        $"[CustomAsteroids:spike]   PARSE OK   '{code}' -> id=#{def.Id.Uid} " +
                        $"parts={def.PartCount} layers={def.Layers.Length} hash='{def.Hash}'");
                }
                else
                {
                    _logger.Info?.Log($"[CustomAsteroids:spike]   PARSE FAIL '{code}' (invalid code or handles missing)");
                }
            }
        }

        public bool Equals(IRewirer other) => ReferenceEquals(this, other);
    }
}
