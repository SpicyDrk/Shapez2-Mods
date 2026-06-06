using System;
using System.Collections.Generic;
using Game.Core.Coordinates;
using Game.HUD.CameraManager;
using ShapezShifter.Hijack;
using ShapezShifter.Kit;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace CustomAsteroids
{
    /// <summary>
    /// PLAN-P02-001 Task 3 + PLAN-P03-001 Task 2 + PLAN-P04-001 Task 1 — the cursor for both
    /// placement and removal. This <see cref="ITickRewirer"/> tracks the hovered space-map tile via
    /// <c>ScreenUtils.TryGetChunkCoordinateAtCursor(viewport, …)</c> (viewport from
    /// <c>GameHelper.Core.Viewport</c>); Esc / right-click cancels.
    ///
    /// <list type="bullet">
    ///   <item><b>Placement</b> (<see cref="CustomAsteroidUiState.PlacementArmed"/>, set by the
    ///   authoring dialog): a plain click drops the default fixed patch via
    ///   <see cref="CustomAsteroidPlacer.TryInjectAt"/>; <b>dragging a box</b> (mouse-down → drag →
    ///   release) places one multi-tile source spanning the dragged rectangle via
    ///   <see cref="CustomAsteroidPlacer.TryAddSource"/> (SC-10). Either way it's recorded for
    ///   persistence and pushed onto the undo stack.</item>
    ///   <item><b>Delete</b> (<see cref="CustomAsteroidUiState.DeleteArmed"/>, set by the
    ///   "Remove Custom Asteroid" entry): left-click on a tile covered by one of OUR placed
    ///   asteroids removes it (registry + live chunk); vanilla patches are never touched.</item>
    /// </list>
    ///
    /// <para>The placer adds/removes the resource source on the live super-chunk in place, so player
    /// platforms (islands) are never disturbed. Persistence, undo/redo and delete are all
    /// offset-agnostic — they operate on the recorded footprint — so box-selected patches save,
    /// reload, undo and delete exactly like the default ones.</para>
    /// </summary>
    internal sealed class CustomAsteroidPlacementController : ITickRewirer
    {
        // Defensive per-axis cap on a dragged footprint (in chunks). The real bound is the
        // super-chunk clip in TryAddSource; this just stops a runaway drag from building a huge
        // offset list. A full space belt (9×4) is far under this.
        private const int MaxDragDim = 64;

        private readonly CustomAsteroidUiState _ui;
        private readonly ILogger _logger;

        private int _lastFrame = -1;
        private bool _loggedArm;
        private bool _haveHover;
        private GlobalChunkCoordinate _lastHover;

        // Box-select drag state (PLAN-P04-001): set on left-mouse-down over the map, consumed on
        // left-mouse-up. A release on the same tile (or off-map) falls back to the default patch.
        private bool _dragging;
        private GlobalChunkCoordinate _dragAnchor;

        public CustomAsteroidPlacementController(CustomAsteroidUiState ui, ILogger logger)
        {
            _ui = ui;
            _logger = logger;
        }

        public void Tick(float deltaTime)
        {
            if (!_ui.PlacementArmed && !_ui.DeleteArmed)
            {
                _loggedArm = false;
                _haveHover = false;
                _dragging = false;
                _ui.DragPreviewActive = false;
                return;
            }

            // Tick can fire more than once per frame; act once per frame.
            int frame = Time.frameCount;
            if (frame == _lastFrame) return;
            _lastFrame = frame;

            try
            {
                if (_ui.PlacementArmed) UpdatePlacement();
                else UpdateDelete();
            }
            catch (Exception ex)
            {
                _logger.Error?.Log($"[CustomAsteroids:place] tick threw (non-fatal); disarming: {ex}");
                _ui.PlacementArmed = false;
                _ui.DeleteArmed = false;
                _dragging = false;
            }
        }

        private void UpdatePlacement()
        {
            if (!_loggedArm)
            {
                _loggedArm = true;
                _logger.Info?.Log(
                    $"[CustomAsteroids:place] placement armed for '{_ui.AuthoredCode}'. " +
                    "Click to place the default patch, or drag a box to size it; Esc / right-click to cancel.");
            }

            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
            {
                _ui.PlacementArmed = false;
                _dragging = false;
                _ui.DragPreviewActive = false;
                _logger.Info?.Log("[CustomAsteroids:place] placement cancelled.");
                return;
            }

            // Track the hovered tile every frame (drives the change-logging + supplies the anchor /
            // release tiles for the box-select drag).
            bool haveGc = TryGetHoverGC(out GlobalChunkCoordinate gc, "place");

            // Drag start: anchor on the tile under the cursor when the button goes down.
            if (Input.GetMouseButtonDown(0) && haveGc)
            {
                _dragging = true;
                _dragAnchor = gc;
                _logger.Info?.Log($"[CustomAsteroids:place] drag start at {gc} (SC {gc.To_SC()}).");
            }

            // While dragging, publish the live box (anchor → current hover) so the preview drawer can
            // render the footprint before release. Falls back to the anchor when the cursor is off-map.
            if (_dragging)
            {
                _ui.DragPreviewActive = true;
                _ui.DragAnchor = _dragAnchor;
                _ui.DragCurrent = haveGc ? gc : _dragAnchor;
            }

            // Drag end: release tile defines the far corner of the box. If the cursor left the map,
            // fall back to the anchor (→ a plain click = default patch). Handles a same-frame
            // down+up (fast click) too, since both checks run in one pass.
            if (Input.GetMouseButtonUp(0) && _dragging)
            {
                _dragging = false;
                _ui.DragPreviewActive = false;
                GlobalChunkCoordinate release = haveGc ? gc : _dragAnchor;
                PlaceFootprint(_dragAnchor, release);
            }
        }

        private void UpdateDelete()
        {
            if (!_loggedArm)
            {
                _loggedArm = true;
                _logger.Info?.Log(
                    "[CustomAsteroids:delete] delete armed. Left-click one of your custom asteroids to remove it; " +
                    "Esc / right-click to cancel.");
            }

            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
            {
                _ui.DeleteArmed = false;
                _logger.Info?.Log("[CustomAsteroids:delete] delete cancelled.");
                return;
            }

            if (!TryGetHoverGC(out GlobalChunkCoordinate gc, "delete")) return;
            if (Input.GetMouseButtonDown(0)) TryDeleteAt(gc);
        }

        private bool TryGetHoverGC(out GlobalChunkCoordinate gc, string tag)
        {
            gc = default;
#pragma warning disable CS0618 // IGameSessionManagers is [Obsolete] — a mod has no DI seam.
            IGameSessionManagers? sessions = GameHelper.Core;
#pragma warning restore CS0618
            Viewport? viewport = sessions?.Viewport;
            if (viewport == null) return false;

            if (!ScreenUtils.TryGetChunkCoordinateAtCursor(viewport, out gc)) return false;

            // Log only when the hovered tile changes, so HITL can watch the cursor track
            // without flooding the log every frame.
            if (!_haveHover || !gc.Equals(_lastHover))
            {
                _haveHover = true;
                _lastHover = gc;
                _logger.Info?.Log($"[CustomAsteroids:{tag}] hovering GC {gc} (SC {gc.To_SC()}).");
            }
            return true;
        }

        /// <summary>
        /// Place the authored asteroid. A plain click (<paramref name="release"/> == <paramref
        /// name="anchor"/>) drops the default fixed patch via <see cref="CustomAsteroidPlacer.TryInjectAt"/>;
        /// a drag builds a rectangle of offsets spanning anchor→release and places one multi-tile source
        /// via <see cref="CustomAsteroidPlacer.TryAddSource"/> (which clips to the anchor's super-chunk +
        /// free tiles). Either way the placement is recorded for persistence + pushed onto the undo stack.
        /// </summary>
        private void PlaceFootprint(GlobalChunkCoordinate anchor, GlobalChunkCoordinate release)
        {
            if (_ui.ResourcesMap is not GameResourcesMap grm)
            {
                _logger.Error?.Log("[CustomAsteroids:place] no GameResourcesMap captured; cannot place. Disarming.");
                _ui.PlacementArmed = false;
                return;
            }

            // No super-chunk island guard: the placer adds the source to the live MapSuperChunk in
            // place (never rebuilds), so platforms are preserved. Placing a resource next to / under
            // a platform is how the game mines. Tiles already occupied (incl. existing patches) are
            // clipped out by the placer.

            // Re-resolve the shape canonically at place-time (ShapeIds are sequential per registry
            // instance, so resolve at the moment of use).
            if (!CanonicalShapeResolver.TryResolve(_ui.AuthoredCode, out ShapeDefinition shape, out string shapeDiag))
            {
                _logger.Error?.Log($"[CustomAsteroids:place] could not resolve '{_ui.AuthoredCode}' at place-time ({shapeDiag}). Disarming.");
                _ui.PlacementArmed = false;
                return;
            }

            bool isDrag = !release.Equals(anchor);
            List<ChunkVector> placedOffsets;
            string injDiag;
            bool ok;
            string footprintDesc;

            if (isDrag)
            {
                List<ChunkVector> rect = BuildRectOffsets(anchor, release, MaxDragDim, out int spanX, out int spanY);
                footprintDesc = $"box {spanX}×{spanY}";
                ok = CustomAsteroidPlacer.TryAddSource(grm, shape, anchor, rect, _logger, out injDiag, out placedOffsets);
            }
            else
            {
                footprintDesc = "default patch";
                ok = CustomAsteroidPlacer.TryInjectAt(grm, shape, anchor, _logger, out injDiag, out placedOffsets);
            }

            if (ok)
            {
                _ui.LastTargetGC = anchor;
                _ui.PlacementArmed = false;
                // Record for save/reload persistence (re-injected on load) AND push onto the
                // undo stack so Ctrl+Z can reverse this placement. Origin = anchor for both paths.
                PlacedAsteroidRecord? placed = _ui.Persistence?.RecordPlacement(anchor, placedOffsets, _ui.AuthoredCode ?? string.Empty);
                if (placed != null) _ui.Undo?.RecordPlace(placed);
                _logger.Info?.Log(
                    $"[CustomAsteroids:place] PLACED '{_ui.AuthoredCode}' ({shapeDiag}) as {footprintDesc} at {anchor} " +
                    $"(world≈{anchor.ToCenter_W()}). {injDiag}. Build a platform + extractor over it to mine the shape.");
            }
            else
            {
                _logger.Warning?.Log(
                    $"[CustomAsteroids:place] placement ({footprintDesc}) at {anchor} failed ({injDiag}); still armed — try again.");
            }
        }

        /// <summary>
        /// Build the offsets for a rectangle spanning <paramref name="anchor"/>→<paramref name="release"/>,
        /// relative to the anchor (so origin = anchor and offset (0,0) is always included). Each axis is
        /// clamped to <paramref name="maxDim"/> tiles, growing in the drag direction; the real bound is the
        /// super-chunk clip the placer applies. Reports the actual per-axis span used.
        /// </summary>
        private static List<ChunkVector> BuildRectOffsets(
            GlobalChunkCoordinate anchor, GlobalChunkCoordinate release, int maxDim, out int spanX, out int spanY)
        {
            int dirX = release.x >= anchor.x ? 1 : -1;
            int dirY = release.y >= anchor.y ? 1 : -1;
            spanX = Math.Min(Math.Abs(release.x - anchor.x) + 1, maxDim);
            spanY = Math.Min(Math.Abs(release.y - anchor.y) + 1, maxDim);

            var offsets = new List<ChunkVector>(spanX * spanY);
            for (int i = 0; i < spanX; i++)
            {
                for (int j = 0; j < spanY; j++)
                {
                    offsets.Add(new ChunkVector(dirX * i, dirY * j, 0));
                }
            }
            return offsets;
        }

        private void TryDeleteAt(GlobalChunkCoordinate gc)
        {
            if (_ui.ResourcesMap is not GameResourcesMap grm)
            {
                _logger.Error?.Log("[CustomAsteroids:delete] no GameResourcesMap captured; cannot delete. Disarming.");
                _ui.DeleteArmed = false;
                return;
            }

            // Only remove asteroids WE placed: the registry is authoritative. RemoveRecordCovering
            // removes + returns the record if the clicked tile is inside one of ours; null otherwise
            // (a vanilla patch or empty space) — in which case we touch nothing and stay armed.
            PlacedAsteroidRecord? record = _ui.Persistence?.RemoveRecordCovering(gc);
            if (record == null)
            {
                _logger.Warning?.Log(
                    $"[CustomAsteroids:delete] {gc} isn't one of your custom asteroids — nothing removed. Still armed.");
                return;
            }

            bool removed = CustomAsteroidPlacer.TryRemoveAt(grm, gc, _logger, out string diag);
            _ui.DeleteArmed = false;
            // Push onto the undo stack so Ctrl+Z can restore this deleted asteroid.
            _ui.Undo?.RecordDelete(record);
            if (removed)
            {
                _logger.Info?.Log(
                    $"[CustomAsteroids:delete] REMOVED custom asteroid '{record.Code}' covering {gc} ({diag}). " +
                    "An extractor over it will stop producing.");
            }
            else
            {
                // Registry said ours but the live source was already gone — registry now consistent.
                _logger.Warning?.Log(
                    $"[CustomAsteroids:delete] registry record for {gc} removed, but no live source was found ({diag}).");
            }
        }

        public bool Equals(IRewirer other) => ReferenceEquals(this, other);
    }
}
