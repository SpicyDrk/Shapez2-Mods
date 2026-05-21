using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Content.Features.Predictions;
using Game.Core.Map.Simulation;
using Game.Core.Simulation;
using MonoMod.RuntimeDetour;
using ILogger = Core.Logging.ILogger;

namespace AnyLayerTrash
{
    /// <summary>
    /// Bypasses the connector-count limits inside the constructors of both
    /// <see cref="ConnectableBuildingSimulation"/> (regular sim) and
    /// <see cref="ConnectableBuildingPredictionSimulation"/> (prediction sim)
    /// so our 1×1×3 trash pillar (12 connectors) is fully represented even
    /// though vanilla <c>TrashSimulation</c> only exposes 4 receivers.
    ///
    /// <para><b>Why both:</b></para>
    /// <list type="bullet">
    ///   <item><b>Prediction</b> ctor throws when input count ≠ receiver count.
    ///     We wrap the sim in a cyclic shim with inflated counts so the throw
    ///     is bypassed and the <c>Connectors</c> list ends up with one entry
    ///     per pillar connector (each backed by one of the 4 real receivers
    ///     via index modulo).</item>
    ///   <item><b>Regular</b> ctor doesn't throw — it loops
    ///     <c>min(NumItemReceivers, connectors.Count)</c>, which silently builds
    ///     only the first 4 connectors and drops the layer-2/3 ones. From the
    ///     placement system's POV the upper-layer connectors don't exist, so
    ///     belts can't snap there (red indicator). Same cyclic-shim trick
    ///     inflates the loop bound and registers all 12 connectors, each
    ///     pointing at a cyclic-mapped real receiver.</item>
    /// </list>
    ///
    /// <para>
    /// Trash is a pure sink, so cyclic-sharing of receivers across the extra
    /// connectors is functionally identical to having distinct per-layer
    /// receivers — shapes arriving on layer 2 or 3 end up at receiver index
    /// <c>i mod 4</c>, which is one of the real vanilla receivers, which
    /// consumes the shape.
    /// </para>
    ///
    /// <para>
    /// Vanilla-count case (input == receiver count) is detected up front in
    /// both hooks and passes through unchanged. Zero behavior change for any
    /// non-hijacked building.
    /// </para>
    ///
    /// <para>
    /// After each ctor runs against its shim, the <c>Simulation</c> property
    /// is reflectively restored to the real inner sim via the
    /// <c>&lt;Simulation&gt;k__BackingField</c> auto-prop backing field, so
    /// any downstream code that downcasts <c>connectableSim.Simulation</c>
    /// still sees the concrete trash sim type.
    /// </para>
    /// </summary>
    internal static class PredictionSimConnectorBypass
    {
        private static readonly FieldInfo? PredictionSimulationBackingField =
            typeof(ConnectableBuildingPredictionSimulation)
                .GetField("<Simulation>k__BackingField",
                    BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo? RegularSimulationBackingField =
            typeof(ConnectableBuildingSimulation)
                .GetField("<Simulation>k__BackingField",
                    BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// Install both hooks. Caller must dispose each returned
        /// <see cref="Hook"/> at shutdown.
        /// </summary>
        public static IReadOnlyList<Hook> Install(ILogger logger)
        {
            return new[]
            {
                InstallPredictionHook(logger),
                InstallRegularHook(logger),
            };
        }

        private static Hook InstallPredictionHook(ILogger logger)
        {
            ConstructorInfo ctor = typeof(ConnectableBuildingPredictionSimulation)
                .GetConstructor(new[] { typeof(BuildingInstance), typeof(IItemPredictionSimulation) })
                ?? throw new InvalidOperationException(
                    "[AnyLayerTrash] Could not locate ConnectableBuildingPredictionSimulation(BuildingInstance, IItemPredictionSimulation) constructor.");

            if (PredictionSimulationBackingField == null)
            {
                logger.Warning?.Log(
                    "[AnyLayerTrash] <Simulation>k__BackingField not found on ConnectableBuildingPredictionSimulation; cyclic shim will remain in .Simulation. Downstream downcasts to the concrete inner sim type will fail.");
            }

            Action<Action<ConnectableBuildingPredictionSimulation, BuildingInstance, IItemPredictionSimulation>,
                ConnectableBuildingPredictionSimulation, BuildingInstance, IItemPredictionSimulation> patch =
                (orig, self, building, simulation) =>
                {
                    IBuildingConnectorData connectorData = building.Definition.CustomData.Get<IBuildingConnectorData>();
                    int inputCount =
                        connectorData.BuildingConnectorsOfType<BuildingItemInput>().Count
                        + connectorData.BuildingConnectorsOfType<BuildingFluidInput>().Count;
                    int outputCount =
                        connectorData.BuildingConnectorsOfType<BuildingItemOutput>().Count
                        + connectorData.BuildingConnectorsOfType<BuildingFluidOutput>().Count;

                    if (inputCount == simulation.NumItemReceivers
                        && outputCount == simulation.NumItemProviders)
                    {
                        orig(self, building, simulation);
                        return;
                    }

                    var shim = new CyclicPredictionReceiverShim(simulation, inputCount, outputCount);
                    orig(self, building, shim);
                    PredictionSimulationBackingField?.SetValue(self, simulation);
                };

            logger.Info?.Log("[AnyLayerTrash:bypass] installed hook on ConnectableBuildingPredictionSimulation..ctor.");
            return new Hook((MethodBase)ctor, (Delegate)patch);
        }

        private static Hook InstallRegularHook(ILogger logger)
        {
            ConstructorInfo ctor = typeof(ConnectableBuildingSimulation)
                .GetConstructor(new[] { typeof(BuildingInstance), typeof(ISimulation) })
                ?? throw new InvalidOperationException(
                    "[AnyLayerTrash] Could not locate ConnectableBuildingSimulation(BuildingInstance, ISimulation) constructor.");

            if (RegularSimulationBackingField == null)
            {
                logger.Warning?.Log(
                    "[AnyLayerTrash] <Simulation>k__BackingField not found on ConnectableBuildingSimulation; cyclic shim will remain in .Simulation. Downstream downcasts to the concrete inner sim type will fail.");
            }

            Action<Action<ConnectableBuildingSimulation, BuildingInstance, ISimulation>,
                ConnectableBuildingSimulation, BuildingInstance, ISimulation> patch =
                (orig, self, building, simulation) =>
                {
                    // Only the item-sim path needs cyclic expansion (trash is item-only).
                    // Fluid / signal builders use the same min() pattern; if a future mod
                    // hijacks a fluid building the same way, add parallel shims here.
                    if (simulation is IItemSimulation itemSim)
                    {
                        IBuildingConnectorData connectorData = building.Definition.CustomData.Get<IBuildingConnectorData>();
                        int inputCount = connectorData.BuildingConnectorsOfType<BuildingItemInput>().Count;
                        int outputCount = connectorData.BuildingConnectorsOfType<BuildingItemOutput>().Count;

                        bool inputsMismatch = inputCount > itemSim.NumItemReceivers;
                        bool outputsMismatch = outputCount > itemSim.NumItemProviders;

                        if (inputsMismatch || outputsMismatch)
                        {
                            int virtualReceivers = inputsMismatch ? inputCount : itemSim.NumItemReceivers;
                            int virtualProviders = outputsMismatch ? outputCount : itemSim.NumItemProviders;

                            logger.Info?.Log(
                                $"[AnyLayerTrash:bypass-regular] cyclic item-sim shim engaged for '{building.Definition.Id.Name}': " +
                                $"inputs={inputCount} (sim has {itemSim.NumItemReceivers}), " +
                                $"outputs={outputCount} (sim has {itemSim.NumItemProviders}).");

                            var shim = new CyclicItemSimulationShim(itemSim, virtualReceivers, virtualProviders);
                            orig(self, building, shim);
                            RegularSimulationBackingField?.SetValue(self, simulation);
                            return;
                        }
                    }

                    orig(self, building, simulation);
                };

            logger.Info?.Log("[AnyLayerTrash:bypass] installed hook on ConnectableBuildingSimulation..ctor.");
            return new Hook((MethodBase)ctor, (Delegate)patch);
        }

        /// <summary>
        /// Transparent <see cref="IItemPredictionSimulation"/> wrapper that
        /// reports inflated counts and dispatches index lookups cyclically
        /// onto the inner sim.
        /// </summary>
        private sealed class CyclicPredictionReceiverShim : IItemPredictionSimulation
        {
            private readonly IItemPredictionSimulation _inner;
            private readonly int _virtualReceivers;
            private readonly int _virtualProviders;

            public CyclicPredictionReceiverShim(IItemPredictionSimulation inner, int virtualReceivers, int virtualProviders)
            {
                _inner = inner;
                _virtualReceivers = virtualReceivers;
                _virtualProviders = virtualProviders;
            }

            public int NumItemReceivers => _virtualReceivers;
            public int NumItemProviders => _virtualProviders;

            public IItemPredictionReceiver GetItemReceiver(int index)
            {
                int innerCount = _inner.NumItemReceivers;
                return innerCount <= 0 ? _inner.GetItemReceiver(index) : _inner.GetItemReceiver(index % innerCount);
            }

            public IItemPredictionProvider GetItemProvider(int index)
            {
                int innerCount = _inner.NumItemProviders;
                return innerCount <= 0 ? _inner.GetItemProvider(index) : _inner.GetItemProvider(index % innerCount);
            }

            public void ClearContent() => _inner.ClearContent();
        }

        /// <summary>
        /// Transparent <see cref="IItemSimulation"/> wrapper for the regular
        /// (non-prediction) sim. Same cyclic dispatch pattern; delegates
        /// <see cref="IItemSimulation.TraverseLanes{TTraverser}"/> straight
        /// through so belt-lane traversal stays correct.
        /// </summary>
        private sealed class CyclicItemSimulationShim : IItemSimulation
        {
            private readonly IItemSimulation _inner;
            private readonly int _virtualReceivers;
            private readonly int _virtualProviders;

            public CyclicItemSimulationShim(IItemSimulation inner, int virtualReceivers, int virtualProviders)
            {
                _inner = inner;
                _virtualReceivers = virtualReceivers;
                _virtualProviders = virtualProviders;
            }

            public int NumItemReceivers => _virtualReceivers;
            public int NumItemProviders => _virtualProviders;

            public IItemReceiver GetItemReceiver(int index)
            {
                int innerCount = _inner.NumItemReceivers;
                return innerCount <= 0 ? _inner.GetItemReceiver(index) : _inner.GetItemReceiver(index % innerCount);
            }

            public IItemProvider GetItemProvider(int index)
            {
                int innerCount = _inner.NumItemProviders;
                return innerCount <= 0 ? _inner.GetItemProvider(index) : _inner.GetItemProvider(index % innerCount);
            }

            public void TraverseLanes<TTraverser>(TTraverser traverser) where TTraverser : IItemLaneTraverser
            {
                _inner.TraverseLanes(traverser);
            }

            public void ClearContent() => _inner.ClearContent();
        }
    }
}
