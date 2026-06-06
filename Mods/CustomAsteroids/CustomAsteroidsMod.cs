using Game.Core.Modding;
using JetBrains.Annotations;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace CustomAsteroids
{
    /// <summary>
    /// CustomAsteroids entry point — author a shape via shape code and place mineable
    /// custom-shape asteroids on the space map.
    ///
    /// <para>The flow (PLAN-P02-001): a <see cref="CustomAsteroidCaptureRewirer"/> grabs the
    /// live space-map <c>ResourcesMap</c> at session init; a <see cref="CustomAsteroidToolbarRewirer"/>
    /// adds a "Custom Asteroid" entry under the space-platforms build menu, bound to a custom
    /// <see cref="CustomAsteroidPlacementInitiator"/> registered by
    /// <see cref="CustomAsteroidIslandPlacementRewirer"/>; selecting it opens the
    /// <see cref="CustomAsteroidAuthoringDialog"/> (shape-code entry + canonical validation),
    /// which arms <see cref="CustomAsteroidPlacementController"/> — left-click places the
    /// authored asteroid via <see cref="CustomAsteroidPlacer"/>. The HUD dialog stack is
    /// captured by <see cref="CustomAsteroidDialogCapture"/> (an <c>IMod</c> has no DI access).</para>
    ///
    /// <para>Capture + placement only mutate the resource map on an explicit placement, so a
    /// vanilla space save loads untouched.</para>
    /// </summary>
    [UsedImplicitly]
    public class CustomAsteroidsMod : IMod
    {
        private readonly ILogger _logger;
        private readonly CustomAsteroidUiState _uiState;
        private readonly CustomAsteroidDialogCapture _dialogCapture;
        private readonly CustomAsteroidCameraLock _cameraLock;
        private readonly CustomAsteroidChainDrawFix _chainDrawFix;
        private readonly CustomAsteroidPlacementPreview _placementPreview;
        private readonly CustomAsteroidPersistence _persistence;
        private readonly RewirerHandle _captureHandle;
        private readonly RewirerHandle _islandHandle;
        private readonly RewirerHandle _toolbarHandle;
        private readonly RewirerHandle _placementHandle;
        private readonly RewirerHandle _saveDataHandle;
        private readonly RewirerHandle _persistTickHandle;
        private readonly CustomAsteroidUndoHook _undoHook;

        public CustomAsteroidsMod(ILogger logger)
        {
            _logger = logger;

            _uiState = new CustomAsteroidUiState();

            // Save/reload persistence (PLAN-P03-001): owns a per-save JSON registry of placed
            // asteroids and re-injects them on load (open-space asteroids aren't in the vanilla
            // serializer). Registered as a Shifter ISaveDataRewirer + a settle-window tick rewirer.
            _persistence = new CustomAsteroidPersistence(_uiState, logger);
            _uiState.Persistence = _persistence;
            _saveDataHandle = GameRewirers.AddRewirer(_persistence.SaveRewirer);
            _persistTickHandle = GameRewirers.AddRewirer(_persistence.SettleTick);

            // Capture rewirer: grabs the live space-map ResourcesMap on sim build and shares
            // it with the placement flow. Capture-only — a vanilla space save loads untouched.
            _captureHandle = GameRewirers.AddRewirer(
                new CustomAsteroidCaptureRewirer(_uiState, logger));

            // Two space-map build-menu entries, each backed by a custom IPlacementInitiator
            // registered into the platform-island placement system; a toolbar entry binds to each id.
            //  - "Custom Asteroid"        → opens the shape-code authoring dialog (then arms placement).
            //  - "Remove Custom Asteroid" → arms delete mode (click one of ours to remove it).
            var authoringDialog = new CustomAsteroidAuthoringDialog(_uiState, logger);
            var placeInitiator = new CustomAsteroidPlacementInitiator(logger, "Custom Asteroid", authoringDialog.Open);
            var removeInitiator = new CustomAsteroidPlacementInitiator(logger, "Remove Custom Asteroid", ArmDelete);

            // Capture the HUD dialog stack (no DI/global access from an IMod) so the dialog
            // can be shown. MonoMod ctor hook on HUDDialogStack.
            _dialogCapture = new CustomAsteroidDialogCapture(_uiState, logger);

            _islandHandle = GameRewirers.AddRewirer(
                new CustomAsteroidIslandPlacementRewirer(_uiState, placeInitiator, removeInitiator, logger));
            _toolbarHandle = GameRewirers.AddRewirer(
                new CustomAsteroidToolbarRewirer(_uiState, logger));

            // Once a shape is authored, this tick rewirer turns the cursor into a placement
            // cursor — left-click places at the hovered space-map tile.
            _placementHandle = GameRewirers.AddRewirer(
                new CustomAsteroidPlacementController(_uiState, logger));

            // Session-only undo/redo (SC-09): the controller pushes place/delete ops onto this stack;
            // CustomAsteroidUndoHook hooks PlayerActionManager.CanUndo/CanRedo + ScheduleUndo/ScheduleRedo
            // so a single Ctrl+Z performs exactly ONE reversal — vanilla actions first, our asteroids as the
            // fallback once the engine's stack is empty (the old input-poll version double-fired with vanilla).
            var undo = new CustomAsteroidUndo(_uiState, logger);
            _uiState.Undo = undo;
            _undoHook = new CustomAsteroidUndoHook(undo, logger);

            // While placement is armed, the left-drag is our box-select — lock the mouse-drag
            // camera pan (like vanilla platform placement) so dragging a box doesn't pan the map.
            _cameraLock = new CustomAsteroidCameraLock(_uiState, logger);

            // Suppress a vanilla per-frame draw NRE when an extractor/boost-chain tile over a custom
            // patch isn't on resource (thin patch, chain off the edge, or orphaned after delete).
            _chainDrawFix = new CustomAsteroidChainDrawFix(logger);

            // Drag preview: while a box-drag is in progress, paint a translucent rectangle over the
            // footprint that would be placed, so the player sees the size before releasing the mouse.
            _placementPreview = new CustomAsteroidPlacementPreview(_uiState, logger);

            _logger.Info?.Log(
                "[CustomAsteroids] mod loaded. Registered persistence (save-data + settle tick) + capture + " +
                "island-placement + toolbar + placement-cursor + undo rewirers + dialog-stack hook. Select the " +
                "'Custom Asteroid' build-menu entry → enter a shape code → click to place the default patch or " +
                "drag a box to size it; 'Remove Custom Asteroid' deletes one; Ctrl+Z / Ctrl+Y undo/redo your " +
                "placements; placed asteroids persist across save/reload.");
        }

        // Handler for the "Remove Custom Asteroid" entry — arm delete mode (the controller's
        // delete cursor takes over). Clears any pending placement so the modes never overlap.
        private void ArmDelete()
        {
            _uiState.PlacementArmed = false;
            _uiState.DeleteArmed = true;
            _logger.Info?.Log("[CustomAsteroids:delete] 'Remove Custom Asteroid' selected — delete mode armed.");
        }

        public void Dispose()
        {
            GameRewirers.RemoveRewirer(_saveDataHandle);
            GameRewirers.RemoveRewirer(_persistTickHandle);
            GameRewirers.RemoveRewirer(_captureHandle);
            GameRewirers.RemoveRewirer(_islandHandle);
            GameRewirers.RemoveRewirer(_toolbarHandle);
            GameRewirers.RemoveRewirer(_placementHandle);
            _undoHook.Dispose();
            _dialogCapture.Dispose();
            _cameraLock.Dispose();
            _chainDrawFix.Dispose();
            _placementPreview.Dispose();
            _persistence.Dispose();
        }
    }
}
