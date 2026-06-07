using System.Collections.Generic;
using Game.Core.Coordinates;
using Game.Core.Rendering.MeshGeneration;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace AnyLayerTrash
{
    /// <summary>
    /// Shared state between <see cref="TrashTrioRewirer"/> (which fills it as the
    /// game builds its building set) and <see cref="TrashActionInterceptor"/>
    /// (which reads it at action time). Holds the vanilla trash group id and the
    /// set of trash variant definition ids, so the interceptor can recognise a
    /// trash placement/deletion cheaply. ("Trio" = the three layers a trash column
    /// spans.)
    /// </summary>
    internal sealed class TrashTrioState
    {
        public bool TrashGroupCaptured;
        public BuildingDefinitionGroupId TrashGroupId;
        public readonly HashSet<BuildingDefinitionId> VanillaTrashVariantIds = new();
    }

    /// <summary>
    /// Captures the vanilla trash group id and its variant definition ids into
    /// <see cref="TrashTrioState"/> every time the game (re)builds its building
    /// set, so <see cref="TrashActionInterceptor"/> can identify trash actions.
    /// Pure capture — it returns the building set unchanged.
    /// </summary>
    internal sealed class TrashTrioRewirer : IBuildingsRewirer
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
                    $"[AnyLayerTrash] trash group '{trashGroupId.Id}' not found; " +
                    "trash actions will not be expanded this session.");
                return gameBuildings;
            }

            foreach (BuildingDefinition def in group._Definitions)
            {
                _state.VanillaTrashVariantIds.Add(def.Id);
            }
            _state.TrashGroupCaptured = true;

            _logger.Info?.Log(
                $"[AnyLayerTrash] captured trash group '{trashGroupId.Id}' " +
                $"({_state.VanillaTrashVariantIds.Count} variant(s)).");
            return gameBuildings;
        }

        public bool Equals(IRewirer other) => ReferenceEquals(this, other);
    }
}
