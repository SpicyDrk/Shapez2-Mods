using Game.Core.Modding;
using JetBrains.Annotations;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace AnyLayerTrash
{
    /// <summary>
    /// AnyLayerTrash entry point — ghost-spawn approach (D5 in INTENT,
    /// PLAN-P01-006).
    ///
    /// <para>
    /// Wires the <see cref="TrashTrioRewirer"/> as a three-faced rewirer
    /// (buildings + simulation systems + prediction systems) so a single shared
    /// <see cref="TrashTrioState"/> can capture the vanilla trash group id and
    /// register matching observers on both sides. Task 1 of PLAN-P01-006 is
    /// diagnostic-only — the observer logs ADD / REM events but does not yet
    /// mutate the map.
    /// </para>
    ///
    /// <para>
    /// The pillar-approach source files (<see cref="TrashHijackRewirer"/>,
    /// <c>PredictionSimConnectorBypass</c>, <c>TrashSystemAnchorExpander</c>,
    /// <c>TrashSystemProbes</c>) stay in the repo as a reference catalogue —
    /// they document <c>TrashSystem</c> internals and the MonoMod cyclic-shim
    /// pattern. They are not registered at runtime. See
    /// <c>.oes/PILLAR-RETROSPECTIVE.md</c> for the full chain of engine walls.
    /// </para>
    /// </summary>
    [UsedImplicitly]
    public class AnyLayerTrashMod : IMod
    {
        private readonly ILogger _logger;
        private readonly RewirerHandle _trioHandle;
        private readonly RewirerHandle _mapSpawnerHandle;

        public AnyLayerTrashMod(ILogger logger)
        {
            _logger = logger;

            var state = new TrashTrioState();
            var trioRewirer = new TrashTrioRewirer(logger, state);

            // GameRewirers.AddRewirer stores the instance once and the various
            // interceptors (BuildingsInterceptor, SimulationSystemsInterceptor,
            // PredictionSystemsInterceptor) each pull it via
            // RewirerProvider.RewirersOfType<T>() — so a single registration
            // covers all three interfaces our rewirer implements.
            //
            // TrashTrioRewirer.ModifyGameBuildings still captures the vanilla
            // trash variant ids into the shared state (the map spawner reuses
            // them to filter). Its sim/prediction observers are now diagnostic
            // only — the authoritative ghost-spawn lives in TrashTrioMapSpawner
            // (PLAN-P01-007), which writes to the real IMapModel instead of the
            // simulator's downstream layout.
            _trioHandle = GameRewirers.AddRewirer(trioRewirer);

            // Map-model driver: acquires CurrentMap via Shifter's GameHelper on
            // the per-tick hook and subscribes to its building add/remove events.
            // Task 1 of PLAN-P01-007 is diagnostic only (logs island-relative +
            // global coords); spawn/remove mutation lands in Tasks 2-3.
            var mapSpawner = new TrashTrioMapSpawner(state, logger);
            _mapSpawnerHandle = GameRewirers.AddRewirer(mapSpawner);

            _logger.Info?.Log(
                "[AnyLayerTrash] mod loaded — map-model ghost-spawn driver active (PLAN-P01-007). " +
                "Placing a trash auto-spawns vanilla trashes on the other layers {0,1,2}; removing any " +
                "removes the trio. Activity logs to [AnyLayerTrash:map]. Sim-side observer retired.");
        }

        public void Dispose()
        {
            GameRewirers.RemoveRewirer(_trioHandle);
            GameRewirers.RemoveRewirer(_mapSpawnerHandle);
        }
    }
}
