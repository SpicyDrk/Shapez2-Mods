using Core.Factory;

namespace SmartCutter
{
    /// <summary>
    /// Configuration interface for the SmartCutter simulation. Mirrors
    /// IFourWaySplitterConfiguration / IDiagonalCutterConfiguration.
    /// </summary>
    public interface ISmartCutterConfiguration
    {
        public BeltSpeed BeltSpeed { get; }
        public BeltDelay ProcessingDelay { get; }
    }

    /// <summary>
    /// Produces a <see cref="SmartCutterSimulation"/> from a
    /// <see cref="SmartCutterSimulationState"/>. Holds the configuration, the
    /// shape registry (for wrapping masked ShapeDefinitions back into
    /// ShapeItems), and the keep-mask shape operation.
    /// </summary>
    public class SmartCutterSimulationFactory : IFactory<SmartCutterSimulationState, SmartCutterSimulation>
    {
        private readonly ISmartCutterConfiguration Configuration;
        private readonly IShapeRegistry ShapeRegistry;
        private readonly ShapeOperationSmartCut SmartCut;

        public SmartCutterSimulationFactory(
            ISmartCutterConfiguration configuration,
            IShapeRegistry shapeRegistry,
            ShapeOperationSmartCut smartCut)
        {
            Configuration = configuration;
            ShapeRegistry = shapeRegistry;
            SmartCut = smartCut;
        }

        public SmartCutterSimulation Produce(SmartCutterSimulationState simulationState)
        {
            return new SmartCutterSimulation(simulationState, Configuration, ShapeRegistry, SmartCut);
        }
    }
}
