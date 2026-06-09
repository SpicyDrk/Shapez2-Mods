using System;
using System.Collections.Generic;
using Game.Core.Simulation;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace AsteroidForge
{
    /// <summary>
    /// Captures the live space-map <c>ResourcesMap</c> for the placement flow.
    ///
    /// <para>Implements <see cref="ISimulationSystemsRewirer"/> purely to READ
    /// <see cref="SimulationSystemsDependencies.ResourcesMap"/> when the game builds its
    /// simulation systems — the one publicly-supported Shifter seam that hands a mod the
    /// live resource map — and stashes it in <see cref="AsteroidUiState.ResourcesMap"/>
    /// for <see cref="AsteroidPlacementController"/> / <see cref="AsteroidPlacer"/>.
    /// It never modifies the systems collection, so a vanilla space save loads untouched.</para>
    ///
    /// <para>Shape identity is resolved canonically at place-time via
    /// <see cref="CanonicalShapeResolver"/> (<c>GameHelper.Core.ShapeRegistry</c>), so no
    /// registry/id-manager handles are cached here — the Phase-1 capture of those, plus the
    /// hardcoded-shape F8 injection spike, were retired once the UI flow drove placement.</para>
    /// </summary>
    internal sealed class AsteroidCaptureRewirer : ISimulationSystemsRewirer
    {
        private readonly AsteroidUiState _ui;
        private readonly ILogger _logger;
        private bool _logged;

        public AsteroidCaptureRewirer(AsteroidUiState ui, ILogger logger)
        {
            _ui = ui;
            _logger = logger;
        }

        public void ModifySimulationSystems(
            ICollection<ISimulationSystem> simulationSystems,
            SimulationSystemsDependencies dependencies)
        {
            // Capture only — never touch `simulationSystems`.
            try
            {
                _ui.ResourcesMap = dependencies.ResourcesMap;

                // Loaded asteroids re-inject once the map is available (load order isn't guaranteed).
                _ui.Persistence?.OnResourcesMapReady();

                if (!_logged)
                {
                    _logged = true;
                    _logger.Info?.Log(
                        $"[AsteroidForge:capture] ResourcesMap " +
                        $"{(dependencies.ResourcesMap != null ? "captured" : "NULL")} (mode={dependencies.Mode}). " +
                        "Select the 'Asteroid Forge' build-menu entry to author + place.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error?.Log($"[AsteroidForge:capture] capture threw (non-fatal): {ex}");
            }
        }

        public bool Equals(IRewirer other) => ReferenceEquals(this, other);
    }
}
