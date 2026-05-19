using Core.Factory;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Hijack;

namespace SmartCutter
{
    /// <summary>
    /// Factory-builder that Shifter's atomic pipeline calls at simulation-system
    /// wire-up time to produce a concrete <see cref="SmartCutterSimulationFactory"/>.
    /// Mirrors FourWaySplitterFactoryBuilder modulo our 1-in-1-out + wire-input
    /// shape operation.
    /// </summary>
    internal class SmartCutterFactoryBuilder
        : IBuildingSimulationFactoryBuilder<SmartCutterSimulation, SmartCutterSimulationState,
            SmartCutterConfiguration>
    {
        public IFactory<SmartCutterSimulationState, SmartCutterSimulation> BuildFactory(
            SimulationSystemsDependencies dependencies,
            out SmartCutterConfiguration config)
        {
            // Defaults copied from FourWaySplitter / DiagonalCutter for parity.
            config = new SmartCutterConfiguration(
                BuffableBeltSpeed.DiscreteSpeed.OneSecondPerTile,
                BuffableBeltDelay.DiscreteDuration.OnePointFiveSeconds);

            var smartCut = new ShapeOperationSmartCut(
                dependencies.Mode.MaxShapeLayers,
                dependencies.ShapeRegistry,
                dependencies.ShapeIdManager);

            return new SmartCutterSimulationFactory(config, dependencies.ShapeRegistry, smartCut);
        }
    }
}
