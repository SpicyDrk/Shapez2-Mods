using System;
using Core.Events;
using Game.Core.Coordinates;
using ILogger = Core.Logging.ILogger;

namespace AsteroidMaker
{
    /// <summary>
    /// PLAN-P02-001 Task 1 — a CUSTOM <see cref="IPlacementInitiator"/> that backs our
    /// space-map build-menu entry.
    ///
    /// <para>The vanilla flow (<c>DefaultIslandPlacementExtender</c>) builds an initiator
    /// from <c>IslandPlacersCreator.CreateDefaultPlacer(IIslandDefinition, ...)</c> — i.e.
    /// it places an <i>island/platform</i>. A custom asteroid is NOT an island; it's a
    /// <c>ShapeMapResourceSource</c> injected into the resource map. So we implement the
    /// small <see cref="IPlacementInitiator"/> contract ourselves and use it purely as the
    /// hook the toolbar button binds to: selecting the entry calls
    /// <see cref="RequestStartPlacement()"/>, which fires the <see cref="Action"/> callback this
    /// initiator was constructed with (the authoring dialog for "place", arm-delete for "remove").</para>
    ///
    /// <para>The lifecycle is trivial (start → fire callback → immediately end) — the actual
    /// authoring/placement/delete flow runs in our own UI state machine, not the engine's. The
    /// same class backs both build-menu entries (place + remove): each is constructed with its own
    /// label + <see cref="Action"/> callback.</para>
    /// </summary>
    internal sealed class AsteroidPlacementInitiator : IPlacementInitiator
    {
        private readonly ILogger _logger;
        private readonly string _label;
        private readonly Action _onSelected;

        private readonly MultiRegisterEvent _onAvailable = new MultiRegisterEvent();
        private readonly MultiRegisterEvent _onUnavailable = new MultiRegisterEvent();
        private readonly MultiRegisterEvent _onStarts = new MultiRegisterEvent();
        private readonly MultiRegisterEvent _onEnds = new MultiRegisterEvent();

        private bool _isPlacing;

        public AsteroidPlacementInitiator(ILogger logger, string label, Action onSelected)
        {
            _logger = logger;
            _label = label;
            _onSelected = onSelected;
        }

        public IEvent OnPlacementBecomesAvailable => _onAvailable;
        public IEvent OnPlacementBecomesUnavailable => _onUnavailable;
        public IEvent OnPlacementStarts => _onStarts;
        public IEvent OnPlacementEnds => _onEnds;

        public bool IsPlacing => _isPlacing;

        // Our asteroid placement is always available; gating happens later in the flow
        // (valid shape code + island-free target tile).
        public bool CanStartPlacement() => true;

        public void RequestStartPlacement() => StartPlacement();

        // Asteroids have no flip/rotation; ignore the placement hints and start normally.
        public void RequestStartPlacement(bool startFlipped, GridRotation preferredRotation) => StartPlacement();

        private void StartPlacement()
        {
            _logger.Info?.Log($"[AsteroidMaker:ui] '{_label}' selected (RequestStartPlacement).");
            _isPlacing = true;
            _onStarts.Invoke();

            try
            {
                _onSelected?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.Error?.Log($"[AsteroidMaker:ui] '{_label}' handler threw (non-fatal): {ex}");
            }

            // End immediately — the engine shouldn't believe a placement is in progress; our own
            // UI state machine (dialog / cursor) drives the rest.
            _isPlacing = false;
            _onEnds.Invoke();
        }

        public void RequestEndPlacement()
        {
            if (!_isPlacing) return;
            _isPlacing = false;
            _onEnds.Invoke();
        }
    }
}
