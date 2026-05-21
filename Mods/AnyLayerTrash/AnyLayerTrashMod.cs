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
            _trioHandle = GameRewirers.AddRewirer(trioRewirer);

            _logger.Info?.Log(
                "[AnyLayerTrash] mod loaded — ghost-spawn observer registered (diagnostic mode). " +
                "ADD/REM events for vanilla trash variants will log to [AnyLayerTrash:trio:sim] " +
                "and [AnyLayerTrash:trio:pred].");
        }

        public void Dispose()
        {
            GameRewirers.RemoveRewirer(_trioHandle);
        }
    }
}
