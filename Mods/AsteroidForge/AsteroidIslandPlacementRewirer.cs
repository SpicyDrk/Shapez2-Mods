using System;
using ShapezShifter;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace AsteroidForge
{
    /// <summary>
    /// PLAN-P02-001 Task 1 — registers our <see cref="AsteroidPlacementInitiator"/>
    /// into the space-map placement system so the toolbar entry has an id to bind to.
    ///
    /// <para>Implements <see cref="IPlatformIslandPlacementRewirers"/> (the platform/space
    /// island variant). Shifter's <c>PlacementInitiatorsInterceptor</c> postfix-hooks
    /// <c>PlatformIslandsPlacersCreators.RegisterPlacers</c> and calls
    /// <see cref="ModifyIslandPlacers"/> with the live <c>IPlacementInitiatorIdRegistry</c>.
    /// Unlike <c>DefaultIslandPlacementExtender</c>, we do NOT call
    /// <c>CreateDefaultPlacer</c> (that places an island) — we register our own initiator
    /// and stash its id for the toolbar.</para>
    /// </summary>
    internal sealed class AsteroidIslandPlacementRewirer : IPlatformIslandPlacementRewirers
    {
        private const string PlacerId = "AsteroidInitiator";
        private const string RemovePlacerId = "AsteroidRemoveInitiator";

        private readonly AsteroidUiState _ui;
        private readonly AsteroidPlacementInitiator _initiator;
        private readonly AsteroidPlacementInitiator _removeInitiator;
        private readonly ILogger _logger;
        private bool _logged;

        public AsteroidIslandPlacementRewirer(
            AsteroidUiState ui,
            AsteroidPlacementInitiator initiator,
            AsteroidPlacementInitiator removeInitiator,
            ILogger logger)
        {
            _ui = ui;
            _initiator = initiator;
            _removeInitiator = removeInitiator;
            _logger = logger;
        }

        public void ModifyIslandPlacers(
            IslandInitiatorsParams islandInitiatorsParams, IPlacementInitiatorIdRegistry placementRegistry)
        {
            try
            {
                PlacementInitiatorId id = placementRegistry.RegisterInitiator(
                    new SerializedPlacerId(PlacerId), _initiator);
                _ui.InitiatorId = id;
                _ui.InitiatorRegistered = true;

                PlacementInitiatorId removeId = placementRegistry.RegisterInitiator(
                    new SerializedPlacerId(RemovePlacerId), _removeInitiator);
                _ui.RemoveInitiatorId = removeId;
                _ui.RemoveInitiatorRegistered = true;

                if (!_logged)
                {
                    _logged = true;
                    _logger.Info?.Log(
                        $"[AsteroidForge:ui] registered placement initiators '{PlacerId}' (id={id}) + " +
                        $"'{RemovePlacerId}' (id={removeId}).");
                }
            }
            catch (Exception ex)
            {
                _logger.Error?.Log($"[AsteroidForge:ui] ModifyIslandPlacers threw (non-fatal): {ex}");
            }
        }

        public bool Equals(IRewirer other) => ReferenceEquals(this, other);
    }
}
