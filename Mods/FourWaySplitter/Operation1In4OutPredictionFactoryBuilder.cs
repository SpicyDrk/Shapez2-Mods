using Core.Factory;
using Game.Content.Features.Predictions.Processing;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Hijack.Predictions;

namespace FourWaySplitter
{
    /// <summary>
    /// Prediction factory builder for the FourWaySplitter. Mirrors the shape
    /// of <c>DiagonalCutterPredictionFactoryBuilder</c> from the official
    /// DiagonalCutter sample — a single-generic
    /// <see cref="IBuildingPredictionFactoryBuilder{TPrediction}"/> bound to
    /// <see cref="Processing1In1OutPredictionSimulation"/>.
    ///
    /// The installed ShapezShifter workshop build (3542611357 v1.0.0) has an
    /// unguarded null-deref at <c>AtomicBuildingExtender.cs:158</c> that
    /// crashes mod load when <c>LazyPredictionExtender</c> is null (i.e. when
    /// the building chain used <c>.WithoutPrediction()</c>). Upstream fix
    /// <c>54d5e38</c> (2026-04-12) added a <c>?.</c> guard but has not shipped
    /// to Workshop. Until it does, every Shifter-registered building must
    /// provide a non-null prediction factory; this class is ours.
    ///
    /// The game's prediction framework tops out at 1-in-2-out, so a real
    /// 4-output prediction requires a custom
    /// <c>IItemPredictionSimulation</c> — deliberately deferred. Instead we
    /// adapt our 4-way split to a 1-output prediction by projecting to the
    /// north quadrant only (<see cref="ShapeOperationFourWaySplitPredictionAdapter"/>).
    /// The HUD / belt-preview will hint one quadrant where our real
    /// simulation yields four; the disagreement is cosmetic — the actual
    /// simulation still produces all four outputs at run-time.
    ///
    /// TODO(v2): real 1-in-4-out prediction via a custom
    /// <c>IItemPredictionSimulation</c>, so the belt-placement preview
    /// reflects all four cardinal outputs. Tracked against a future story —
    /// not required for v1 (UAT-P02 closes once the mod loads cleanly).
    /// </summary>
    internal class Operation1In4OutPredictionFactoryBuilder
        : IBuildingPredictionFactoryBuilder<Processing1In1OutPredictionSimulation>
    {
        public IFactory<Processing1In1OutPredictionSimulation> BuildFactory(
            PredictionSystemsDependencies dependencies)
        {
            var op = new ShapeOperationFourWaySplitPredictionAdapter(
                dependencies.Mode.MaxShapeLayers,
                dependencies.ShapeRegistry,
                dependencies.ShapeIdManager);
            return new Processing1In1OutPredictionSimulationFactory(op);
        }
    }
}
