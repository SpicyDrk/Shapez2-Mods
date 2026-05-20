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
            // Throughput target: one SmartCutter per lane = full belt
            // throughput, never a bottleneck. Wire-routing complexity makes
            // cutter-class throughput (3 per belt, vanilla half-destroyer
            // style) too punishing — the player would need 3× as many wires.
            //
            // BeltSpeed = OneSecondPerTile matches the vanilla unbuffed belt
            // (per BuffableBeltSpeed.cs, the default BaseSpeed is also
            // OneSecondPerTile). The previous HalfSecondPerTile was literally
            // twice the belt speed, which caused shapes to visibly accelerate
            // as they entered the building's three lanes and decelerate again
            // on exit. Matching the belt's speed makes the building's three
            // lanes flow seamlessly with the surrounding belts.
            //
            // ProcessingDelay = HalfSecond (the minimum). Note: this field is
            // currently vestigial — SmartCutterConfiguration exposes it on
            // ISmartCutterConfiguration but SmartCutterSimulation reads only
            // BeltSpeed when building its three BeltLane instances. Set to
            // the minimum for hygiene; a future cleanup pass could remove
            // the field entirely.
            config = new SmartCutterConfiguration(
                BuffableBeltSpeed.DiscreteSpeed.OneSecondPerTile,
                BuffableBeltDelay.DiscreteDuration.HalfSecond);

            var smartCut = new ShapeOperationSmartCut(
                dependencies.Mode.MaxShapeLayers,
                dependencies.ShapeRegistry,
                dependencies.ShapeIdManager);

            return new SmartCutterSimulationFactory(config, dependencies.ShapeRegistry, smartCut);
        }
    }
}
