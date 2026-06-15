using Game.Core.Modding;
using JetBrains.Annotations;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace AnyLayerTrash
{
    /// <summary>
    /// "Any Layer Trash" — placing a vanilla Trash fills every (empty) layer of the
    /// tile; deleting one removes the whole column. <see cref="TrashTrioRewirer"/>
    /// captures the trash ids; <see cref="TrashActionInterceptor"/> expands the
    /// player's build action. Design notes: see CODE-NOTES.md.
    /// </summary>
    [UsedImplicitly]
    public class AnyLayerTrashMod : IMod
    {
        private readonly ILogger _logger;
        private readonly RewirerHandle _trioHandle;
        private readonly TrashActionInterceptor _actionInterceptor;
        private readonly VoidTileTrackerGuard _voidTileGuard;

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

            // Keep the render-only void-tile tracker from throwing (and aborting the
            // surrounding action) when a stacked trash column is deleted — e.g. on a
            // platform-blueprint paste. See VoidTileTrackerGuard / CODE-NOTES.md.
            _voidTileGuard = new VoidTileTrackerGuard(logger);
            _voidTileGuard.Install();

            _logger.Info?.Log(
                "[AnyLayerTrash] mod loaded — placing trash fills every layer of the tile, " +
                "deleting any removes the whole column (one undoable action; occupied layers skipped).");
        }

        public void Dispose()
        {
            GameRewirers.RemoveRewirer(_trioHandle);
            _actionInterceptor.Dispose();
            _voidTileGuard.Dispose();
        }
    }
}
