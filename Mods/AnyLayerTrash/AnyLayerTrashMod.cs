using Game.Core.Modding;
using JetBrains.Annotations;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace AnyLayerTrash
{
    /// <summary>
    /// AnyLayerTrash entry point — coexist redesign (2026-06-06).
    ///
    /// <para>
    /// The original vanilla Trash building is left untouched. A CLONE of it is
    /// registered as a second variant ("Any Layer Trash") in the same trash group
    /// via <see cref="TrashVariantRegistrar"/> — reachable in the build menu through
    /// the placement flip key, the same way the cutter/stacker variants are. The
    /// modded variant never lands on the map: <see cref="TrashActionInterceptor"/>
    /// swaps it for a column of plain vanilla trash on every layer at commit time,
    /// so it needs no simulation wiring and existing trash placements are unaffected.
    /// </para>
    ///
    /// <para>
    /// The earlier hijack/ghost-spawn source files (<c>TrashTrioRewirer</c>,
    /// <c>TrashTrioObserver</c>, <c>TrashTrioMapSpawner</c>, <c>TrashHijackRewirer</c>,
    /// <c>PredictionSimConnectorBypass</c>, <c>TrashSystemAnchorExpander</c>,
    /// <c>TrashSystemProbes</c>) stay in the repo as a reference catalogue — they
    /// document <c>TrashSystem</c> internals and the MonoMod cyclic-shim pattern.
    /// They are not registered at runtime.
    /// </para>
    /// </summary>
    [UsedImplicitly]
    public class AnyLayerTrashMod : IMod
    {
        private readonly ILogger _logger;
        private readonly RewirerHandle _registrarHandle;
        private readonly TrashActionInterceptor _actionInterceptor;

        public AnyLayerTrashMod(ILogger logger)
        {
            _logger = logger;

            var state = new TrashTrioState();

            // Registrar clones the vanilla trash default into the modded "Any Layer
            // Trash" variant, cross-links the two as a flip pair, and captures the
            // vanilla group id / variant ids / default definition into the shared
            // state. It implements both ISimulationSystemsRewirer and
            // IPredictionSystemsRewirer; a single AddRewirer registration covers both
            // (the interceptors pull it via RewirerProvider.RewirersOfType<T>()).
            var registrar = new TrashVariantRegistrar(state, logger);
            _registrarHandle = GameRewirers.AddRewirer(registrar);

            // Interceptor watches player actions: placing the modded variant stamps a
            // vanilla trash column across all layers (one undoable transaction);
            // vanilla trash placements pass straight through.
            _actionInterceptor = new TrashActionInterceptor(state, logger);
            _actionInterceptor.Install();

            _logger.Info?.Log(
                "[AnyLayerTrash] mod loaded — adds an 'Any Layer Trash' variant to the trash group " +
                "(flip while placing to select it). Placing it stamps vanilla trash on every layer " +
                "{0,1,2} as one undoable transaction; the original Trash building is unchanged. " +
                "Activity logs to [AnyLayerTrash:variant] and [AnyLayerTrash:action].");
        }

        public void Dispose()
        {
            GameRewirers.RemoveRewirer(_registrarHandle);
            _actionInterceptor.Dispose();
        }
    }
}
