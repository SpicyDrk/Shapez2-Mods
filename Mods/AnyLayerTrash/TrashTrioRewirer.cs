using System.Collections.Generic;
using Game.Core.Coordinates;
using Game.Core.Rendering.MeshGeneration;
using Game.Core.Simulation;
using ShapezShifter.Hijack;
using ShapezShifter.Hijack.Predictions;
using ILogger = Core.Logging.ILogger;

namespace AnyLayerTrash
{
    /// <summary>
    /// Shared state across the rewirer trio + observer instances. Populated by
    /// <see cref="TrashTrioRewirer.ModifyGameBuildings"/> when the vanilla trash
    /// group is resolved against <c>GameBuildings</c>. Read by
    /// <see cref="TrashTrioObserver"/> on every add/remove event to filter to
    /// trash variants only.
    ///
    /// <para>Tasks 2-4 will extend this with the trio registry
    /// (<c>_knownTrioLayers</c>), in-flight spawn/removal guards, and a
    /// reference to the live <c>IMapLayout</c> for sibling mutation.</para>
    /// </summary>
    internal sealed class TrashTrioState
    {
        public bool TrashGroupCaptured;
        public BuildingDefinitionGroupId TrashGroupId;
        public readonly HashSet<BuildingDefinitionId> VanillaTrashVariantIds = new();
    }

    /// <summary>
    /// Three-faced rewirer for the ghost-spawn approach:
    /// <list type="bullet">
    ///   <item><see cref="IBuildingsRewirer"/> — captures the vanilla trash
    ///     group id + variant ids into <see cref="TrashTrioState"/> so the
    ///     observer can filter cheaply at event time.</item>
    ///   <item><see cref="ISimulationSystemsRewirer"/> — registers a
    ///     <see cref="TrashTrioObserver"/> on the regular simulation side.</item>
    ///   <item><see cref="IPredictionSystemsRewirer"/> — registers a separate
    ///     observer on the prediction side. The prediction simulator routes
    ///     building events through the same <c>IBuildingObserverSimulationSystem</c>
    ///     interface, so the only difference is which collection we append to.</item>
    /// </list>
    ///
    /// <para>The same trio pattern is used by <c>SmartCutterMirrorRewirers</c>
    /// — shared state, one rewirer object implementing all three interfaces,
    /// staged work across <c>ModifyGameBuildings</c> → <c>ModifySimulationSystems</c>
    /// → <c>ModifyPredictionSystems</c>. <see cref="ModifyGameBuildings"/> runs
    /// first per Shifter's interceptor order, so by the time the sim/prediction
    /// rewirers fire, <see cref="TrashTrioState.TrashGroupCaptured"/> is true.</para>
    /// </summary>
    internal sealed class TrashTrioRewirer : IBuildingsRewirer, ISimulationSystemsRewirer, IPredictionSystemsRewirer
    {
        private readonly ILogger _logger;
        private readonly TrashTrioState _state;

        public TrashTrioRewirer(ILogger logger, TrashTrioState state)
        {
            _logger = logger;
            _state = state;
        }

        public GameBuildings ModifyGameBuildings(
            MetaGameModeBuildings metaBuildings,
            GameBuildings gameBuildings,
            IMeshCache meshCache,
            VisualThemeBaseResources theme)
        {
            BuildingDefinitionGroupId trashGroupId = gameBuildings.TrashBuildingId;
            _state.TrashGroupId = trashGroupId;
            _state.VanillaTrashVariantIds.Clear();

            if (!gameBuildings._VariantsById.TryGetValue(trashGroupId, out IBuildingDefinitionGroup? groupRef)
                || groupRef is not BuildingDefinitionGroup group)
            {
                _state.TrashGroupCaptured = false;
                _logger.Warning?.Log(
                    $"[AnyLayerTrash:trio] Trash group '{trashGroupId.Id}' not in gameBuildings; " +
                    "observer will not match any buildings.");
                return gameBuildings;
            }

            foreach (BuildingDefinition def in group._Definitions)
            {
                _state.VanillaTrashVariantIds.Add(def.Id);
            }
            _state.TrashGroupCaptured = true;

            _logger.Info?.Log(
                $"[AnyLayerTrash:trio] captured trash group '{trashGroupId.Id}' with " +
                $"{_state.VanillaTrashVariantIds.Count} variant(s); observer will filter on these ids.");
            return gameBuildings;
        }

        public void ModifySimulationSystems(
            ICollection<ISimulationSystem> simulationSystems,
            SimulationSystemsDependencies dependencies)
        {
            simulationSystems.Add(new TrashTrioObserver(_state, _logger, side: "sim", isAuthoritative: true));
            _logger.Info?.Log("[AnyLayerTrash:trio] registered observer on simulation side (authoritative).");
        }

        public void ModifyPredictionSystems(
            ICollection<ISimulationSystem> simulationSystems,
            PredictionSystemsDependencies dependencies)
        {
            // Prediction observer is diagnostic-only — it re-fires the whole
            // building set on every placement preview tick (Task 1 logs showed
            // pred event count > sim event count). Owning the trio registry
            // here would spawn duplicates on every cursor drag.
            simulationSystems.Add(new TrashTrioObserver(_state, _logger, side: "pred", isAuthoritative: false));
            _logger.Info?.Log("[AnyLayerTrash:trio] registered observer on prediction side (diagnostic only).");
        }

        public bool Equals(IRewirer other) => ReferenceEquals(this, other);
    }
}
