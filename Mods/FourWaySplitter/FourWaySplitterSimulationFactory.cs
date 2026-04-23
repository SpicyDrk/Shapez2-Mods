using Core.Factory;

namespace FourWaySplitter
{
    /// <summary>
    /// Configuration interface for the FourWaySplitter simulation. Kept in
    /// this file to match DiagonalCutter's layout (which co-locates
    /// <c>IDiagonalCutterConfiguration</c> with its factory).
    ///
    /// No research-speed member — v1 ships with no research gate
    /// (CONSTRAINTS §5b / R6).
    /// </summary>
    public interface IFourWaySplitterConfiguration
    {
        public BeltSpeed BeltSpeed { get; }
        public BeltDelay ProcessingDelay { get; }
    }

    /// <summary>
    /// Factory that produces a <see cref="FourWaySplitterSimulation"/> from a
    /// <see cref="FourWaySplitterSimulationState"/>. Mirrors
    /// <c>DiagonalCutterSimulationFactory</c> verbatim modulo our 4-output
    /// simulation type.
    /// </summary>
    public class FourWaySplitterSimulationFactory : IFactory<FourWaySplitterSimulationState, FourWaySplitterSimulation>
    {
        private readonly IFourWaySplitterConfiguration Configuration;
        private readonly IShapeRegistry ShapeRegistry;
        private readonly ShapeOperationFourWaySplit FourWaySplit;

        public FourWaySplitterSimulationFactory(
            IFourWaySplitterConfiguration configuration,
            IShapeRegistry shapeRegistry,
            ShapeOperationFourWaySplit fourWaySplit)
        {
            Configuration = configuration;
            ShapeRegistry = shapeRegistry;
            FourWaySplit = fourWaySplit;
        }

        public FourWaySplitterSimulation Produce(FourWaySplitterSimulationState simulationState)
        {
            return new FourWaySplitterSimulation(simulationState, Configuration, ShapeRegistry, FourWaySplit);
        }
    }
}

