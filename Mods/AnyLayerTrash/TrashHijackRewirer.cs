using System.Collections.Generic;
using System.Linq;
using Game.Core.Coordinates;
using Game.Core.Rendering.MeshGeneration;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace AnyLayerTrash
{
    /// <summary>
    /// Replaces the vanilla <c>Trash</c> building's connector data and footprint
    /// with a <c>1 × 1 × <see cref="PillarHeight"/></c> pillar that has belt-input
    /// connectors on all four sides of every layer. The shape-eating simulation,
    /// renderer, sound, draw data, and toolbar entry all stay vanilla — we only
    /// swap the <see cref="IBuildingConnectorData"/> attached to the
    /// <see cref="BuildingDefinition"/>.
    ///
    /// <para>
    /// Implementation pattern:
    /// </para>
    /// <list type="number">
    ///   <item>Locate the vanilla Trash <see cref="BuildingDefinitionGroup"/> in
    ///     <see cref="GameBuildings"/> (publicized field
    ///     <c>_VariantsById</c>) using the well-known group id
    ///     <see cref="GameBuildings.TrashBuildingId"/>.</item>
    ///   <item>For each variant in the group (typically one — the trash is
    ///     symmetric so vanilla doesn't generate a mirror), construct a new
    ///     <see cref="BuildingConnectorData"/> with multi-layer connectors +
    ///     multi-tile footprint, build a new <see cref="BuildingDefinition"/>
    ///     copying every non-connector <c>CustomData</c> entry over, and slot
    ///     it back into the group's variant list + <c>_DefinitionsById</c>.</item>
    /// </list>
    ///
    /// <para>
    /// Layer cap: <see cref="PillarHeight"/> is hardcoded to 6 — the highest
    /// plausible cap (MoreLayers raises vanilla's 3 → 6 via
    /// <c>GameScenario.Mechanics.BuildingLayerUnlocks</c>). On vanilla without
    /// MoreLayers, connectors on layers 4-6 are inert because no belt can be
    /// placed up there. Phase 2 makes this cap-aware if needed.
    /// </para>
    /// </summary>
    internal sealed class TrashHijackRewirer : IBuildingsRewirer
    {
        /// <summary>
        /// How tall the trash pillar is. Locked to 3 — the vanilla layer cap.
        /// MoreLayers compatibility is explicitly out of scope for this story
        /// (the user opted to assume MoreLayers is not installed; see INTENT
        /// redirect R3). If MoreLayers shows up later, raise this constant or
        /// make it cap-aware.
        /// </summary>
        public const short PillarHeight = 3;

        private static readonly TileDirection[] AllFourSides =
        {
            TileDirection.North,
            TileDirection.East,
            TileDirection.South,
            TileDirection.West,
        };

        private readonly ILogger _logger;

        public TrashHijackRewirer(ILogger logger)
        {
            _logger = logger;
        }

        public GameBuildings ModifyGameBuildings(
            MetaGameModeBuildings metaBuildings,
            GameBuildings gameBuildings,
            IMeshCache meshCache,
            VisualThemeBaseResources theme)
        {
            BuildingDefinitionGroupId trashGroupId = gameBuildings.TrashBuildingId;
            if (!gameBuildings._VariantsById.TryGetValue(trashGroupId, out IBuildingDefinitionGroup? groupRef)
                || groupRef is not BuildingDefinitionGroup group)
            {
                _logger.Warning?.Log($"[AnyLayerTrash] Trash group '{trashGroupId.Id}' not in gameBuildings; skipping hijack.");
                return gameBuildings;
            }

            // Snapshot the variants — we're about to mutate the underlying list.
            var originalDefs = group._Definitions.ToList();
            if (originalDefs.Count == 0)
            {
                _logger.Warning?.Log("[AnyLayerTrash] Trash group has no variants; skipping hijack.");
                return gameBuildings;
            }

            // Diagnostic: dump pre-hijack vanilla shape so we can sanity-check
            // our pillar against what the engine considers a "valid" connector.
            foreach (var def in originalDefs)
            {
                int connectorCount = def.ConnectorData.AllBuildingConnectors.Length;
                int tileCount = (def.ConnectorData as BuildingConnectorData)?.Tiles.Length ?? -1;
                _logger.Info?.Log(
                    $"[AnyLayerTrash:pre-diag] vanilla '{def.Id.Name}' has {connectorCount} connectors across {tileCount} tiles. " +
                    $"TileDimensions={def.ConnectorData.TileDimensions}, TileBounds={def.ConnectorData.TileBounds}.");
                foreach (var io in def.ConnectorData.AllBuildingConnectors)
                {
                    _logger.Info?.Log($"[AnyLayerTrash:pre-diag]   {io.GetType().Name} pivot={io.Pivot()}");
                }
            }

            group._Definitions.Clear();
            int replaced = 0;
            foreach (BuildingDefinition oldDef in originalDefs)
            {
                BuildingDefinition newDef = BuildPillarVariant(oldDef);
                group._Definitions.Add(newDef);
                gameBuildings._DefinitionsById[newDef.Id] = newDef;
                replaced++;
            }

            _logger.Info?.Log(
                $"[AnyLayerTrash] Hijacked {replaced} trash variant(s) → 1×1×{PillarHeight} pillar; " +
                $"belt input on all 4 sides × every layer. Vanilla mesh/sound/sim preserved.");

            // Diagnostic: enumerate the new connectors so Player.log can confirm
            // multi-layer connector data is actually present after the swap.
            foreach (var def in group._Definitions)
            {
                int connectorCount = def.ConnectorData.AllBuildingConnectors.Length;
                int tileCount = (def.ConnectorData as BuildingConnectorData)?.Tiles.Length ?? -1;
                _logger.Info?.Log(
                    $"[AnyLayerTrash:diag] '{def.Id.Name}' now has {connectorCount} connectors across {tileCount} tiles. " +
                    $"TileDimensions={def.ConnectorData.TileDimensions}, TileBounds={def.ConnectorData.TileBounds}.");
                foreach (var io in def.ConnectorData.AllBuildingConnectors)
                {
                    if (io is BuildingItemInput input)
                    {
                        _logger.Info?.Log(
                            $"[AnyLayerTrash:diag]   input pos_L={input.Position_L} dir={input.TileDirection} ioType={input.IOType} standType={input.StandType}");
                    }
                    else
                    {
                        _logger.Info?.Log($"[AnyLayerTrash:diag]   other connector: {io.GetType().Name} pivot={io.Pivot()}");
                    }
                }
            }

            return gameBuildings;
        }

        private BuildingDefinition BuildPillarVariant(BuildingDefinition old)
        {
            IBuildingConnectorData newConnectors = BuildPillarConnectorData(old.ConnectorData);
            BuildingDefinition newDef = new BuildingDefinition(old.Id, newConnectors);

            // Carry over everything CustomData held EXCEPT the old IBuildingConnectorData
            // attachment — that's the one we replaced. Preserves draw data, sound,
            // mirror cross-links, sim config providers, placement flags, etc.
            foreach (var data in old.CustomData.All)
            {
                if (data is IBuildingConnectorData) continue;
                newDef.CustomData.Attach(data);
            }
            newDef.CustomData.Attach(newConnectors);

            return newDef;
        }

        private IBuildingConnectorData BuildPillarConnectorData(IBuildingConnectorData old)
        {
            // Use the vanilla layer-0 input as the template for StandType /
            // IOType / Seperators so the pillar's connectors behave identically
            // to vanilla on every layer.
            BuildingItemInput? template = old.AllBuildingConnectors
                .OfType<BuildingItemInput>()
                .FirstOrDefault();

            var connectors = new List<IBuildingIO>();
            // Preserve any non-shape-input connectors from vanilla as-is (e.g.,
            // wire or fluid connectors, if vanilla ever adds them). We only
            // replace the shape-input set.
            foreach (var c in old.AllBuildingConnectors)
            {
                if (c is BuildingItemInput) continue;
                connectors.Add(c);
            }

            var tiles = new List<TileVector>();
            for (short z = 0; z < PillarHeight; z++)
            {
                tiles.Add(new TileVector(0, 0, z));
                foreach (TileDirection side in AllFourSides)
                {
                    BuildingItemInput input = new BuildingItemInput
                    {
                        Position_L = new TileVector(0, 0, z),
                        IOType = template?.IOType ?? BuildingItemIOType.Regular,
                        StandType = template?.StandType ?? BuildingBeltStandType.Normal,
                        Seperators = template?.Seperators ?? true,
                    };
                    input.TileDirection = side;
                    connectors.Add(input);
                }
            }

            TileVector min = new TileVector(0, 0, 0);
            TileVector max = new TileVector(0, 0, (short)(PillarHeight - 1));
            LocalTileBounds bounds = new LocalTileBounds(min, max);
            LocalVector center = LocalVector.Lerp((LocalVector)min, (LocalVector)max, 0.5f);
            TileDimensions dims = bounds.Dimensions;

            return new BuildingConnectorData(connectors, tiles, bounds, center, dims);
        }

        public bool Equals(IRewirer other) => ReferenceEquals(this, other);
    }
}
