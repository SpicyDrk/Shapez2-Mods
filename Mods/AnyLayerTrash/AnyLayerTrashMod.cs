using Game.Core.Modding;
using JetBrains.Annotations;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace AnyLayerTrash
{
    /// <summary>
    /// "Any Layer Trash" — delete shapes on any layer without dragging them down
    /// to layer 1 first.
    ///
    /// <para>Placing a vanilla Trash building automatically fills every layer of
    /// that tile with trash, and deleting any one removes the whole column. This
    /// is done by expanding the player's own build action
    /// (<see cref="TrashActionInterceptor"/>), so it stays a single undoable
    /// transaction and works with area-drag, blueprints, and platform deletes.
    /// Only empty layers are filled — layers already occupied by other buildings
    /// are left alone. The trash placed is plain vanilla trash, so nothing
    /// mod-specific is written to the save.</para>
    ///
    /// <para><see cref="TrashTrioRewirer"/> captures the vanilla trash building ids
    /// so the interceptor can recognise trash actions.</para>
    /// </summary>
    [UsedImplicitly]
    public class AnyLayerTrashMod : IMod
    {
        private readonly ILogger _logger;
        private readonly RewirerHandle _trioHandle;
        private readonly TrashActionInterceptor _actionInterceptor;

        public AnyLayerTrashMod(ILogger logger)
        {
            _logger = logger;

            var state = new TrashTrioState();

            // Capture the vanilla trash ids each time the game builds its building
            // set (so the interceptor can recognise trash actions in any game mode).
            _trioHandle = GameRewirers.AddRewirer(new TrashTrioRewirer(logger, state));

            // Expand the player's trash build action across all layers at commit time.
            _actionInterceptor = new TrashActionInterceptor(state, logger);
            _actionInterceptor.Install();

            _logger.Info?.Log(
                "[AnyLayerTrash] mod loaded — placing trash fills every layer of the tile, " +
                "deleting any removes the whole column (one undoable action; occupied layers skipped).");
        }

        public void Dispose()
        {
            GameRewirers.RemoveRewirer(_trioHandle);
            _actionInterceptor.Dispose();
        }
    }
}
