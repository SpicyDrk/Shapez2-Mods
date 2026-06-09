using System;
using Game.Core.Modding;
using JetBrains.Annotations;
using ShapezShifter.Hijack;
using ShapezShifter.Kit;
using ShapezShifter.Textures;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace AsteroidForge
{
    /// <summary>
    /// AsteroidForge entry point — author a shape via shape code and place mineable
    /// custom-shape asteroids on the space map.
    ///
    /// <para>The flow (PLAN-P02-001): a <see cref="AsteroidCaptureRewirer"/> grabs the
    /// live space-map <c>ResourcesMap</c> at session init; a <see cref="AsteroidToolbarRewirer"/>
    /// adds a "Asteroid Forge" entry under the space-platforms build menu, bound to a custom
    /// <see cref="AsteroidPlacementInitiator"/> registered by
    /// <see cref="AsteroidIslandPlacementRewirer"/>; selecting it opens the
    /// <see cref="AsteroidAuthoringDialog"/> (shape-code entry + canonical validation),
    /// which arms <see cref="AsteroidPlacementController"/> — left-click places the
    /// authored asteroid via <see cref="AsteroidPlacer"/>. The HUD dialog stack is
    /// captured by <see cref="AsteroidDialogCapture"/> (an <c>IMod</c> has no DI access).</para>
    ///
    /// <para>Capture + placement only mutate the resource map on an explicit placement, so a
    /// vanilla space save loads untouched.</para>
    /// </summary>
    [UsedImplicitly]
    public class AsteroidForgeMod : IMod
    {
        private readonly ILogger _logger;
        private readonly AsteroidUiState _uiState;
        private readonly AsteroidDialogCapture _dialogCapture;
        private readonly AsteroidCameraLock _cameraLock;
        private readonly AsteroidChainDrawFix _chainDrawFix;
        private readonly AsteroidPlacementPreview _placementPreview;
        private readonly AsteroidPersistence _persistence;
        private readonly RewirerHandle _captureHandle;
        private readonly RewirerHandle _islandHandle;
        private readonly RewirerHandle _toolbarHandle;
        private readonly RewirerHandle _placementHandle;
        private readonly RewirerHandle _saveDataHandle;
        private readonly RewirerHandle _persistTickHandle;
        private readonly AsteroidUndoHook _undoHook;

        public AsteroidForgeMod(ILogger logger)
        {
            _logger = logger;

            _uiState = new AsteroidUiState();

            // Build-menu entry icons (placeholder art under Resources/, swappable). Loaded once here;
            // a missing/unreadable PNG just leaves the entry icon-less — never blocks load.
            ModFolderLocator resources = ModDirectoryLocator.CreateLocator<AsteroidForgeMod>().SubLocator("Resources");
            _uiState.PlaceIcon = TryLoadSprite(resources, "AsteroidForge_Icon.png", logger);
            _uiState.RemoveIcon = TryLoadSprite(resources, "AsteroidForge_Remove_Icon.png", logger);

            // Save/reload persistence (PLAN-P03-001): owns a per-save JSON registry of placed
            // asteroids and re-injects them on load (open-space asteroids aren't in the vanilla
            // serializer). Registered as a Shifter ISaveDataRewirer + a settle-window tick rewirer.
            _persistence = new AsteroidPersistence(_uiState, logger);
            _uiState.Persistence = _persistence;
            _saveDataHandle = GameRewirers.AddRewirer(_persistence.SaveRewirer);
            _persistTickHandle = GameRewirers.AddRewirer(_persistence.SettleTick);

            // Capture rewirer: grabs the live space-map ResourcesMap on sim build and shares
            // it with the placement flow. Capture-only — a vanilla space save loads untouched.
            _captureHandle = GameRewirers.AddRewirer(
                new AsteroidCaptureRewirer(_uiState, logger));

            // Two space-map build-menu entries, each backed by a custom IPlacementInitiator
            // registered into the platform-island placement system; a toolbar entry binds to each id.
            //  - "Asteroid Forge"        → opens the shape-code authoring dialog (then arms placement).
            //  - "Remove Asteroid" → arms delete mode (click one of ours to remove it).
            var authoringDialog = new AsteroidAuthoringDialog(_uiState, logger);
            var placeInitiator = new AsteroidPlacementInitiator(logger, "Asteroid Forge", authoringDialog.Open);
            var removeInitiator = new AsteroidPlacementInitiator(logger, "Remove Asteroid", ArmDelete);

            // Capture the HUD dialog stack (no DI/global access from an IMod) so the dialog
            // can be shown. MonoMod ctor hook on HUDDialogStack.
            _dialogCapture = new AsteroidDialogCapture(_uiState, logger);

            _islandHandle = GameRewirers.AddRewirer(
                new AsteroidIslandPlacementRewirer(_uiState, placeInitiator, removeInitiator, logger));
            _toolbarHandle = GameRewirers.AddRewirer(
                new AsteroidToolbarRewirer(_uiState, logger));

            // Once a shape is authored, this tick rewirer turns the cursor into a placement
            // cursor — left-click places at the hovered space-map tile.
            _placementHandle = GameRewirers.AddRewirer(
                new AsteroidPlacementController(_uiState, logger));

            // Session-only undo/redo (SC-09): the controller pushes place/delete ops onto this stack;
            // AsteroidUndoHook hooks PlayerActionManager.CanUndo/CanRedo + ScheduleUndo/ScheduleRedo
            // so a single Ctrl+Z performs exactly ONE reversal — vanilla actions first, our asteroids as the
            // fallback once the engine's stack is empty (the old input-poll version double-fired with vanilla).
            var undo = new AsteroidUndo(_uiState, logger);
            _uiState.Undo = undo;
            _undoHook = new AsteroidUndoHook(undo, logger);

            // While placement is armed, the left-drag is our box-select — lock the mouse-drag
            // camera pan (like vanilla platform placement) so dragging a box doesn't pan the map.
            _cameraLock = new AsteroidCameraLock(_uiState, logger);

            // Suppress a vanilla per-frame draw NRE when an extractor/boost-chain tile over a custom
            // patch isn't on resource (thin patch, chain off the edge, or orphaned after delete).
            _chainDrawFix = new AsteroidChainDrawFix(logger);

            // Drag preview: while a box-drag is in progress, paint a translucent rectangle over the
            // footprint that would be placed, so the player sees the size before releasing the mouse.
            _placementPreview = new AsteroidPlacementPreview(_uiState, logger);

            _logger.Info?.Log(
                "[AsteroidForge] mod loaded. Registered persistence (save-data + settle tick) + capture + " +
                "island-placement + toolbar + placement-cursor + undo rewirers + dialog-stack hook. Select the " +
                "'Asteroid Forge' build-menu entry → enter a shape code → click to place the default patch or " +
                "drag a box to size it; 'Remove Asteroid' deletes one; Ctrl+Z / Ctrl+Y undo/redo your " +
                "placements; placed asteroids persist across save/reload.");
        }

        // Handler for the "Remove Asteroid" entry — arm delete mode (the controller's
        // delete cursor takes over). Clears any pending placement so the modes never overlap.
        private void ArmDelete()
        {
            _uiState.PlacementArmed = false;
            _uiState.DeleteArmed = true;
            _logger.Info?.Log("[AsteroidForge:delete] 'Remove Asteroid' selected — delete mode armed.");
        }

        // Load a build-menu icon from Resources/ as a Unity Sprite (Shifter's FileTextureLoader).
        // Returns null (entry shows no icon) if the file is missing or fails to decode — never throws.
        private static Sprite? TryLoadSprite(ModFolderLocator resources, string file, ILogger logger)
        {
            try
            {
                string path = resources.SubPath(file);
                if (!System.IO.File.Exists(path))
                {
                    logger.Warning?.Log($"[AsteroidForge:ui] icon '{file}' not found at {path}; entry will be icon-less.");
                    return null;
                }
                return FileTextureLoader.LoadTextureAsSprite(path, out _);
            }
            catch (Exception ex)
            {
                logger.Warning?.Log($"[AsteroidForge:ui] failed to load icon '{file}' (non-fatal): {ex.Message}");
                return null;
            }
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
