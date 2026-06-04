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
        private readonly CustomAsteroidSpikeState _spikeState;
        private readonly RewirerHandle _spikeHandle;
        private readonly RewirerHandle _injectorHandle;

        public CustomAsteroidsMod(ILogger logger)
        {
            _logger = logger;

            _spikeState = new CustomAsteroidSpikeState();

            // Capture rewirer: grabs ResourcesMap/ShapeRegistry/ShapeIdManager on
            // space-map sim build and resolves the injection payload.
            _spikeHandle = GameRewirers.AddRewirer(
                new CustomAsteroidSpikeRewirer(_spikeState, logger));

            // Injector tick rewirer: opt-in (F8) insertion of the custom-shape
            // asteroid into the captured ResourcesMap. Nothing fires without the key,
            // so a vanilla space save loads untouched (SC-03).
            _injectorHandle = GameRewirers.AddRewirer(
                new CustomAsteroidInjector(_spikeState, logger));

            _logger.Info?.Log(
                "[CustomAsteroids] mod loaded (Phase 1 spike). Registered capture + injector rewirers; " +
                "on space-map sim build it logs a shape-code parse table to [CustomAsteroids:spike]. " +
                "Press F8 in a space game to inject the custom-shape asteroid. No map mutation until then.");
        }

        public void Dispose()
        {
            GameRewirers.RemoveRewirer(_spikeHandle);
            GameRewirers.RemoveRewirer(_injectorHandle);
        }
    }
}
