using System;
using Game.Core.Coordinates;
using Game.Core.Simulation;

namespace CustomAsteroids
{
    /// <summary>
    /// Shared state for the Phase-2 authoring + placement UI flow, threaded between
    /// the three cooperating rewirers:
    ///
    /// <list type="bullet">
    ///   <item><see cref="CustomAsteroidIslandPlacementRewirer"/> registers our custom
    ///   place + remove <see cref="CustomAsteroidPlacementInitiator"/>s at session init and
    ///   stashes the resulting <c>PlacementInitiatorId</c>s here.</item>
    ///   <item><see cref="CustomAsteroidToolbarRewirer"/> reads <see cref="InitiatorId"/> /
    ///   <see cref="RemoveInitiatorId"/> (once registered) to bind build-menu entries to them.</item>
    ///   <item>Each initiator's callback arms a mode flag — the authoring dialog sets
    ///   <see cref="PlacementArmed"/>; the remove handler sets <see cref="DeleteArmed"/> —
    ///   which <see cref="CustomAsteroidPlacementController"/> consumes.</item>
    /// </list>
    ///
    /// <para>The toolbar build pass and the placer-registration pass are independent
    /// engine callbacks; this holder lets the toolbar entry pick up the id lazily once
    /// it exists (<see cref="InitiatorRegistered"/>). It also carries the live
    /// <see cref="ResourcesMap"/>, the <see cref="Persistence"/> registry, and the
    /// session <see cref="Undo"/> stack so the cooperating rewirers share one source of truth.</para>
    /// </summary>
    internal sealed class CustomAsteroidUiState
    {
        /// <summary>
        /// The shape code pre-filled in the authoring dialog the first time it opens
        /// (a colored circles + pins + crystals 3-layer shape — a good "shows everything"
        /// default). After the player authors a code, <see cref="AuthoredCode"/> is reused
        /// as the default instead.
        /// </summary>
        public const string DefaultShapeCode = "CrCgCbCy:P-P-P-P-:crcgcbcy";

        /// <summary>
        /// The id returned by <c>IPlacementInitiatorIdRegistry.RegisterInitiator</c> for
        /// our custom initiator. Valid only once <see cref="InitiatorRegistered"/> is true.
        /// Stored by value alongside a guard flag to stay agnostic about whether
        /// <c>PlacementInitiatorId</c> is a struct or a class.
        /// </summary>
        public PlacementInitiatorId InitiatorId;

        public bool InitiatorRegistered;

        /// <summary>
        /// The id for the "Remove Custom Asteroid" build-menu entry (its own initiator).
        /// Valid only once <see cref="RemoveInitiatorRegistered"/> is true.
        /// </summary>
        public PlacementInitiatorId RemoveInitiatorId;

        public bool RemoveInitiatorRegistered;

        /// <summary>
        /// The HUD dialog stack, captured from <c>HUDDialogStack</c>'s constructor by
        /// <see cref="CustomAsteroidDialogCapture"/> (an <c>IMod</c> has no DI access to it).
        /// Null until the HUD builds it in-session.
        /// </summary>
        public IHUDDialogStack? DialogStack;

        /// <summary>The last shape code the player confirmed in the authoring dialog.</summary>
        public string? AuthoredCode;

        /// <summary>
        /// The canonical <see cref="ShapeDefinition"/> for <see cref="AuthoredCode"/>,
        /// resolved via <see cref="CanonicalShapeResolver"/>. This is what Task 3/4 place +
        /// inject so a vanilla extractor mines exactly the authored shape.
        /// </summary>
        public ShapeDefinition? AuthoredShape;

        /// <summary>
        /// True while the player is in placement mode: a valid shape was authored and we're
        /// waiting for a left-click on the space map (Esc / right-click cancels). Set by the
        /// authoring dialog, consumed + cleared by <see cref="CustomAsteroidPlacementController"/>.
        /// </summary>
        public bool PlacementArmed;

        /// <summary>
        /// True while the player is in delete mode (selected "Remove Custom Asteroid"): a left-click
        /// on a tile covered by one of OUR placed asteroids removes it (Esc / right-click cancels).
        /// Set by the remove-entry handler, consumed + cleared by <see cref="CustomAsteroidPlacementController"/>.
        /// </summary>
        public bool DeleteArmed;

        /// <summary>The most recent tile the player clicked to place at (Task 4 injects here).</summary>
        public GlobalChunkCoordinate? LastTargetGC;

        /// <summary>
        /// The live space-map resource map, captured from <c>SimulationSystemsDependencies</c>
        /// by <see cref="CustomAsteroidCaptureRewirer"/>. Needed for the placement guard and
        /// the in-place asteroid injection (<see cref="CustomAsteroidPlacer"/>).
        /// </summary>
        public IGameResourcesMap? ResourcesMap;

        /// <summary>
        /// Save/reload persistence for placed asteroids (PLAN-P03-001). The placement controller
        /// records each placement here; <see cref="CustomAsteroidCaptureRewirer"/> nudges it when the
        /// ResourcesMap becomes available so loaded asteroids re-inject. Set by the mod entry point.
        /// </summary>
        public CustomAsteroidPersistence? Persistence;

        /// <summary>
        /// Session-only undo/redo stack for custom-asteroid place/delete ops (PLAN-P03-001 Task 3,
        /// SC-09). The placement controller pushes a Place op on placement and a Delete op on delete;
        /// <see cref="CustomAsteroidUndoController"/> drives Ctrl+Z / Ctrl+Y. Set by the mod entry point.
        /// </summary>
        public CustomAsteroidUndo? Undo;
    }
}
