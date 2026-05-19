using Core.Factory;
using Game.Content.Features.Predictions.Processing;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Hijack.Predictions;

namespace SmartCutter
{
    /// <summary>
    /// Prediction factory builder for the SmartCutter. Wraps the P01 identity
    /// adapter (see <see cref="ShapeOperationSmartCutPredictionAdapter"/>) in
    /// the game's 1-in-1-out prediction simulation framework.
    /// </summary>
    internal class Operation1In1OutPredictionFactoryBuilder
        : IBuildingPredictionFactoryBuilder<Processing1In1OutPredictionSimulation>
    {
        public IFactory<Processing1In1OutPredictionSimulation> BuildFactory(
            PredictionSystemsDependencies dependencies)
        {
            var op = new ShapeOperationSmartCutPredictionAdapter();
            return new Processing1In1OutPredictionSimulationFactory(op);
        }
    }
}
