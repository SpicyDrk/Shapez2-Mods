using Game.Core.Modding;
using JetBrains.Annotations;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace CustomAsteroids
{
    /// <summary>
    /// CustomAsteroids entry point.
    ///
    /// <para>
    /// Phase P01 (PLAN-P01-001) is a feasibility spike + scaffold. Task 7
    /// (reachability proof) registers <see cref="CustomAsteroidSpikeRewirer"/> —
    /// an <see cref="ISimulationSystemsRewirer"/> that CAPTURES the live
    /// space-map <c>ResourcesMap</c> + <c>ShapeRegistry</c> + <c>ShapeIdManager</c>
    /// and logs a shape-code parse table (no map mutation yet). Task 8 will add a
    /// debug trigger that uses the captured handles to inject a
    /// <c>ShapeMapResourceSource</c> so a vanilla extractor mines it.
    /// </para>
    ///
    /// <para>The spike captures handles only — a vanilla space save loads
    /// untouched (SC-03).</para>
    /// </summary>
    [UsedImplicitly]
    public class CustomAsteroidsMod : IMod
    {
        private readonly ILogger _logger;
<<<<<<< Updated upstream
        private readonly CustomAsteroidSpikeState _spikeState;
        private readonly RewirerHandle _spikeHandle;
        private readonly RewirerHandle _injectorHandle;
=======
        private readonly CustomAsteroidUiState _uiState;
        private readonly CustomAsteroidDialogCapture _dialogCapture;
        private readonly CustomAsteroidCameraLock _cameraLock;
        private readonly CustomAsteroidChainDrawFix _chainDrawFix;
        private readonly CustomAsteroidPersistence _persistence;
        private readonly RewirerHandle _captureHandle;
        private readonly RewirerHandle _islandHandle;
        private readonly RewirerHandle _toolbarHandle;
        private readonly RewirerHandle _placementHandle;
        private readonly RewirerHandle _saveDataHandle;
        private readonly RewirerHandle _persistTickHandle;
        private readonly RewirerHandle _undoHandle;
>>>>>>> Stashed changes

        public CustomAsteroidsMod(ILogger logger)
        {
            _logger = logger;

            _spikeState = new CustomAsteroidSpikeState();

<<<<<<< Updated upstream
            // Capture rewirer: grabs ResourcesMap/ShapeRegistry/ShapeIdManager on
            // space-map sim build and resolves the injection payload.
            _spikeHandle = GameRewirers.AddRewirer(
                new CustomAsteroidSpikeRewirer(_spikeState, logger));

            // Injector tick rewirer: opt-in (F8) insertion of the custom-shape
            // asteroid into the captured ResourcesMap. Nothing fires without the key,
            // so a vanilla space save loads untouched (SC-03).
            _injectorHandle = GameRewirers.AddRewirer(
                new CustomAsteroidInjector(_spikeState, logger));
=======
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
>>>>>>> Stashed changes

            // Session-only undo/redo (SC-09): the controller pushes place/delete ops onto this
            // stack; the controller-tick reverses them on Ctrl+Z / Ctrl+Y, but only when our stack
            // is non-empty (so vanilla undo is untouched when we have nothing to reverse).
            var undo = new CustomAsteroidUndo(_uiState, logger);
            _uiState.Undo = undo;
            _undoHandle = GameRewirers.AddRewirer(new CustomAsteroidUndoController(undo, logger));

            // While placement is armed, the left-drag is our box-select — lock the mouse-drag
            // camera pan (like vanilla platform placement) so dragging a box doesn't pan the map.
            _cameraLock = new CustomAsteroidCameraLock(_uiState, logger);

            // Suppress a vanilla per-frame draw NRE when an extractor/boost-chain tile over a custom
            // patch isn't on resource (thin patch, chain off the edge, or orphaned after delete).
            _chainDrawFix = new CustomAsteroidChainDrawFix(logger);

            _logger.Info?.Log(
<<<<<<< Updated upstream
                "[CustomAsteroids] mod loaded (Phase 1 spike). Registered capture + injector rewirers; " +
                "on space-map sim build it logs a shape-code parse table to [CustomAsteroids:spike]. " +
                "Press F8 in a space game to inject the custom-shape asteroid. No map mutation until then.");
=======
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
>>>>>>> Stashed changes
        }

        public void Dispose()
        {
<<<<<<< Updated upstream
            GameRewirers.RemoveRewirer(_spikeHandle);
            GameRewirers.RemoveRewirer(_injectorHandle);
=======
            GameRewirers.RemoveRewirer(_saveDataHandle);
            GameRewirers.RemoveRewirer(_persistTickHandle);
            GameRewirers.RemoveRewirer(_captureHandle);
            GameRewirers.RemoveRewirer(_islandHandle);
            GameRewirers.RemoveRewirer(_toolbarHandle);
            GameRewirers.RemoveRewirer(_placementHandle);
            GameRewirers.RemoveRewirer(_undoHandle);
            _dialogCapture.Dispose();
            _cameraLock.Dispose();
            _chainDrawFix.Dispose();
            _persistence.Dispose();
>>>>>>> Stashed changes
        }
    }
}
