using Core.Factory;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Hijack;

namespace FourWaySplitter
{
    /// <summary>
    /// Factory-builder that Shifter's atomic pipeline calls at simulation-system
    /// wire-up time to produce a concrete <see cref="FourWaySplitterSimulationFactory"/>.
    /// Mirrors <c>DiagonalCutterFactoryBuilder</c> verbatim modulo:
    ///   - no <c>ResearchSpeedId</c> (FourWaySplitter ships without a research
    ///     gate per CONSTRAINTS §5b / R6);
    ///   - our 4-output simulation type (<see cref="FourWaySplitterSimulation"/>).
    ///
    /// <para>
    /// Note on generic arity: <see cref="IBuildingSimulationFactoryBuilder{TSimulation,TState,TConfig}"/>
    /// takes the <b>concrete</b> configuration class as <c>TConfig</c>, not the
    /// <see cref="IFourWaySplitterConfiguration"/> interface. Matches
    /// DiagonalCutterFactoryBuilder's choice of <c>DiagonalCutterConfiguration</c>.
    /// The <c>out TConfig</c> parameter lets Shifter's <c>BuffablesExtender</c>
    /// reach into the config's buffable fields (speed / delay) for runtime tuning.
    /// </para>
    /// </summary>
    internal class FourWaySplitterFactoryBuilder
        : IBuildingSimulationFactoryBuilder<FourWaySplitterSimulation, FourWaySplitterSimulationState,
            FourWaySplitterConfiguration>
    {
        public IFactory<FourWaySplitterSimulationState, FourWaySplitterSimulation> BuildFactory(
            SimulationSystemsDependencies dependencies,
            out FourWaySplitterConfiguration config)
        {
            // Belt speed + processing delay: copy DiagonalCutter defaults for
            // parity. Tune in P03 after in-game UAT (SC-06/SC-07). No research
            // speed id — CONSTRAINTS §5b MUST NOT introduce a research gate in v1.
            config = new FourWaySplitterConfiguration(
                BuffableBeltSpeed.DiscreteSpeed.OneSecondPerTile,
                BuffableBeltDelay.DiscreteDuration.OnePointFiveSeconds);

            // Build the pure split-math operation. Same DI shape as
            // ShapeOperationDiagonalCut: max layers + shape registry + id manager,
            // all sourced from the game's SimulationSystemsDependencies.
            var fourWaySplit = new ShapeOperationFourWaySplit(
                dependencies.Mode.MaxShapeLayers,
                dependencies.ShapeRegistry,
                dependencies.ShapeIdManager);

            return new FourWaySplitterSimulationFactory(config, dependencies.ShapeRegistry, fourWaySplit);
        }
    }
}
