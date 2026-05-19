using Core.Localization;
using Game.Content.Features;
using Game.Core.Coordinates;
using Game.Core.Modding;
using Game.Core.Rendering;
using Game.Core.Research;
using JetBrains.Annotations;
using ShapezShifter.Flow;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Flow.Research;
using ShapezShifter.Flow.Toolbar;
using ShapezShifter.Kit;
using ShapezShifter.Textures;
using System;
using ILogger = Core.Logging.ILogger;

namespace SmartCutter
{
    /// <summary>
    /// SmartCutter — a 1×1 Shapez 2 building that masks an incoming shape with a
    /// wire-input shape signal. The wire shape acts as a keep-mask: filled
    /// quadrants are kept on the output, empty quadrants are cut away.
    ///
    /// Registration mirrors FourWaySplitterMod / DiagonalCuttersMod (the
    /// official sample). Phase P01 deviations from the FourWaySplitter chain:
    /// <list type="number">
    ///   <item>Connector data built via Shifter's single-tile builder (we have
    ///     no multi-level outputs, so the hand-crafted approach FourWay needed
    ///     is unnecessary).</item>
    ///   <item>Wire input on a side face (north) using
    ///     <see cref="WireConnectorConfig.CustomInput"/>. Same belt-filter-style
    ///     convention referenced in the project INTENT.</item>
    ///   <item>Prediction is a 1-in-1-out identity stub for P01 (see
    ///     <see cref="Operation1In1OutPredictionFactoryBuilder"/> and the wider
    ///     Shifter-v1.0.0 NRE rationale documented on FourWaySplitter's
    ///     equivalent class).</item>
    ///   <item>Visible mesh deferred — LODEmptyMesh placeholder in every LOD
    ///     slot, same approach FourWaySplitter shipped with. A real mesh is
    ///     polish work for a follow-up phase / story.</item>
    /// </list>
    /// </summary>
    [UsedImplicitly]
    public class SmartCutterMod : IMod
    {
        public SmartCutterMod(ILogger logger)
        {
            // CS0618: BuildingDefinitionId/GroupId ctors are obsolete-flagged
            // for consumers of existing buildings; defining a new one requires
            // the string ctor — same rationale as FourWaySplitter.
#pragma warning disable CS0618
            BuildingDefinitionGroupId groupId = new("SmartCutterGroup");
            BuildingDefinitionId definitionId = new("SmartCutter");
#pragma warning restore CS0618

            string titleId = "building-variant.smart-cutter.title";
            string descriptionId = "building-variant.smart-cutter.description";

            ModFolderLocator modResourcesLocator =
                ModDirectoryLocator.CreateLocator<SmartCutterMod>().SubLocator("Resources");

            string iconPath = modResourcesLocator.SubPath("SmartCutter_Icon.png");

            IBuildingGroupBuilder smartCutterGroup = BuildingGroup.Create(groupId)
                .WithTitle(titleId.T())
                .WithDescription(descriptionId.T())
                .WithIcon(FileTextureLoader.LoadTextureAsSprite(iconPath, out _))
                .AsNonTransportableBuilding()
                .WithPreferredPlacement(DefaultPreferredPlacementMode.LinePerpendicular)
                .WithDefaultStructureOverview();

            // Single-tile connector data: shape-in west, shape-out east, wire-in
            // north. Default shape input/output directions are W/E in Shifter's
            // ShapeConnectorConfig; wire input is placed on the north face via
            // CustomInput so it sits adjacent to the shape lanes without
            // colliding with them.
            IBuildingConnectorData connectorData = BuildingConnectors.SingleTile()
                .AddShapeInput(ShapeConnectorConfig.DefaultInput())
                .AddShapeOutput(ShapeConnectorConfig.DefaultOutput())
                .AddWireInput(WireConnectorConfig.CustomInput(TileDirection.North, BuildingSignalIOType.Wire))
                .Build();

            IBuildingBuilder smartCutterBuilder = Building.Create(definitionId)
                .WithConnectorData(connectorData)
                .DynamicallyRendering<SmartCutterSimulationRenderer, SmartCutterSimulation,
                    ISmartCutterDrawData>(new SmartCutterDrawData())
                .WithStaticDrawData(CreateDrawData())
                .WithoutSound()
                .WithoutSimulationConfiguration()
                .WithEfficiencyData(new BuildingEfficiencyData(2.0f, 1));

            AtomicBuildings.Extend()
                .AllScenarios()
                .WithBuilding(smartCutterBuilder, smartCutterGroup)
                .UnlockedAtMilestone(new ByIndexMilestoneSelector(new Index(0)))
                .WithDefaultPlacement()
                // Toolbar slot: follows the cutter cluster like FourWaySplitter.
                // Resolution is runtime-dependent on the player's game version.
                .InToolbar(ToolbarElementLocator.Root().ChildAt(0).ChildAt(2).ChildAt(^1).InsertAfter())
                .WithSimulation(new SmartCutterFactoryBuilder(), logger)
                .WithAtomicShapeProcessingModules(BuiltinResearchSpeed.CutterSpeed, 2.0f)
                .WithPrediction(new Operation1In1OutPredictionFactoryBuilder(), logger)
                .Build();
        }

        /// <summary>
        /// Empty LOD ladder for the v1 building body — no visible in-world mesh.
        /// Same pattern as FourWaySplitterMod.CreateDrawData. Toolbar icon and
        /// placement connector arrows still render via the connector data + icon.
        /// A real mesh is deferred to follow-up polish work.
        /// </summary>
        private static BuildingDrawData CreateDrawData()
        {
            var empty = new LODEmptyMesh();
            ILODMesh[] lodLadder = { empty, empty, empty };

            return new BuildingDrawData(
                false,                                  // renderVoidBelow
                lodLadder,                              // ILODMesh[] primary LOD ladder
                empty,                                  // shadow pass
                empty,                                  // special pass
                ((IMeshReference)null!),                // close-LOD ref (runtime-nullable)
                empty,                                  // support pass
                Array.Empty<CollisionBox>(),            // no tile-based collision volume
                ((IBuildingCustomDrawData)null!),       // no custom draw payload
                false,                                  // trailing flag
                ((IMeshReference)null!),                // secondary ref (runtime-nullable)
                false                                   // trailing flag
            );
        }

        public void Dispose()
        {
        }
    }
}
