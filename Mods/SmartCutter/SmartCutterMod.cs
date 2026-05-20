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
using ShapezShifter.Hijack;
using ShapezShifter.Kit;
using ShapezShifter.Textures;
using System;
using System.IO;
using UnityEngine;
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
        private readonly ILogger _logger;

        public SmartCutterMod(ILogger logger)
        {
            _logger = logger;

            // CS0618: BuildingDefinitionId/GroupId ctors are obsolete-flagged
            // for consumers of existing buildings; defining a new one requires
            // the string ctor — same rationale as FourWaySplitter.
#pragma warning disable CS0618
            BuildingDefinitionGroupId groupId = new("SmartCutterGroup");
            BuildingDefinitionId definitionId = new("SmartCutter");
            BuildingDefinitionId mirroredDefinitionId = new("SmartCutterMirrored");
#pragma warning restore CS0618

            string titleId = "building-variant.smart-cutter.title";
            string descriptionId = "building-variant.smart-cutter.description";

            ModFolderLocator modResourcesLocator =
                ModDirectoryLocator.CreateLocator<SmartCutterMod>().SubLocator("Resources");

            string iconPath = modResourcesLocator.SubPath("SmartCutter_Icon.png");
            string meshPath = ResolveBodyMeshPath(modResourcesLocator);

            IBuildingGroupBuilder smartCutterGroup = BuildingGroup.Create(groupId)
                .WithTitle(titleId.T())
                .WithDescription(descriptionId.T())
                .WithIcon(FileTextureLoader.LoadTextureAsSprite(iconPath, out _))
                .AsNonTransportableBuilding()
                .WithPreferredPlacement(DefaultPreferredPlacementMode.LinePerpendicular)
                .WithDefaultStructureOverview();

            // Default variant: shape-in west, shape-out east, wire-in NORTH.
            IBuildingConnectorData connectorData = BuildingConnectors.SingleTile()
                .AddShapeInput(ShapeConnectorConfig.DefaultInput())
                .AddShapeOutput(ShapeConnectorConfig.DefaultOutput())
                .AddWireInput(WireConnectorConfig.CustomInput(TileDirection.North, BuildingSignalIOType.Wire))
                .Build();

            IBuildingBuilder smartCutterBuilder = Building.Create(definitionId)
                .WithConnectorData(connectorData)
                .DynamicallyRendering<SmartCutterSimulationRenderer, SmartCutterSimulation,
                    ISmartCutterDrawData>(new SmartCutterDrawData())
                .WithStaticDrawData(CreateDrawData(meshPath, mirror: false))
                .WithoutSound()
                .WithoutSimulationConfiguration()
                .WithEfficiencyData(new BuildingEfficiencyData(2.0f, 1));

            AtomicBuildings.Extend()
                .AllScenarios()
                .WithBuilding(smartCutterBuilder, smartCutterGroup)
                .UnlockedAtMilestone(new ByIndexMilestoneSelector(new Index(0)))
                .WithDefaultPlacement()
                .InToolbar(ToolbarElementLocator.Root().ChildAt(0).ChildAt(2).ChildAt(^1).InsertAfter())
                .WithSimulation(new SmartCutterFactoryBuilder(), logger)
                .WithAtomicShapeProcessingModules(BuiltinResearchSpeed.CutterSpeed, 2.0f)
                .WithPrediction(new Operation1In1OutPredictionFactoryBuilder(), logger)
                .Build();

            // Mirrored variant: register a SECOND BuildingDefinition into the same
            // group via a rewirer trio. Matches the vanilla flow where
            // BuildingDefinitionFactory.CreateDefinitions yields BOTH the default
            // and mirrored definitions into one group with IBuildingMirroringDefinition
            // cross-links — which is what makes the F-key cycle between them during
            // placement. Going through the Shifter atomic chain a second time would
            // produce a duplicate toolbar entry (and a duplicate group registration),
            // so the mirror is wired by hand.
            //
            // The mirror BuildingDefinition itself is registered lazily by the
            // simulation rewirer (see SmartCutterMirrorSimulationRewirer) rather
            // than from an IBuildingsRewirer — Shifter's default-chain rewirers
            // self-cycle handles every pass and would otherwise end up running
            // AFTER our static-handle rewirer on subsequent passes, causing the
            // mirror BuildingDefinition to be missing when downstream sim/pred/
            // modules systems tried to resolve it.
            var mirrorState = new SmartCutterMirrorState
            {
                MirrorDrawData = CreateDrawData(meshPath, mirror: true),
            };
            GameRewirers.AddRewirer(new SmartCutterMirrorSimulationRewirer(definitionId, mirroredDefinitionId, mirrorState, new SmartCutterFactoryBuilder(), logger));
            GameRewirers.AddRewirer(new SmartCutterMirrorPredictionRewirer(definitionId, mirroredDefinitionId, mirrorState, new Operation1In1OutPredictionFactoryBuilder(), logger));
            GameRewirers.AddRewirer(new SmartCutterMirrorModulesRewirer(mirrorState, BuiltinResearchSpeed.CutterSpeed, 2.0f, logger));
        }

        /// <summary>
        /// Build the BuildingDrawData. If a body mesh exists at <paramref name="meshPath"/>,
        /// load it via the multi-mesh-aware loader and use it across all three LOD slots.
        /// Otherwise fall back to LODEmptyMesh × 3 and log a warning so the mod still
        /// loads cleanly while art is being prepared.
        ///
        /// <para>
        /// <paramref name="mirror"/> selects the N↔S-flipped orientation used by the
        /// mirror variant. We load the FBX twice (once per variant) so the mirror gets
        /// its own Unity <c>Mesh</c> with reversed winding + recalculated normals —
        /// scaling the transform alone at render time would back-face-cull the body.
        /// </para>
        /// </summary>
        private BuildingDrawData CreateDrawData(string meshPath, bool mirror)
        {
            var empty = new LODEmptyMesh();
            ILODMesh[] lodLadder;

            if (!string.IsNullOrEmpty(meshPath) && File.Exists(meshPath))
            {
                try
                {
                    // Use our multi-mesh-aware loader instead of
                    // FileMeshLoader.LoadSingleMeshFromFile — Shifter's helper
                    // only handles single-mesh FBXs (it calls Meshes.Single()),
                    // which fails on most DCC-exported FBXs.
                    Mesh bodyMesh = MultiMeshLoader.LoadCombinedMeshFromFile(meshPath, mirror);
                    var bodyRef = new TemporaryMeshReference(bodyMesh);
                    var bodyLod = new RuntimeLODMesh(new IMeshReference[] { bodyRef, bodyRef, bodyRef });
                    lodLadder = new ILODMesh[] { bodyLod, bodyLod, bodyLod };
                    _logger.Info?.Log($"[SmartCutter] Loaded {(mirror ? "mirrored " : "")}body mesh from {meshPath}");
                }
                catch (Exception ex)
                {
                    _logger.Warning?.Log($"[SmartCutter] Failed to load {(mirror ? "mirrored " : "")}body mesh from {meshPath}: {ex.Message}. Falling back to empty mesh.");
                    lodLadder = new ILODMesh[] { empty, empty, empty };
                }
            }
            else
            {
                _logger.Warning?.Log($"[SmartCutter] No body mesh found at {meshPath}. Drop a .fbx or .obj at that path; falling back to empty mesh for now.");
                lodLadder = new ILODMesh[] { empty, empty, empty };
            }

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

        /// <summary>
        /// Resolve the body mesh path. Prefers .fbx, falls back to .obj. Returns
        /// the .fbx candidate path even when the file doesn't exist so the
        /// warning log points the user at the expected location.
        /// </summary>
        private static string ResolveBodyMeshPath(ModFolderLocator modResourcesLocator)
        {
            string fbxPath = modResourcesLocator.SubPath("SmartCutter_Body.fbx");
            if (File.Exists(fbxPath)) return fbxPath;

            string objPath = modResourcesLocator.SubPath("SmartCutter_Body.obj");
            if (File.Exists(objPath)) return objPath;

            return fbxPath; // canonical expected path for the warning log
        }

        public void Dispose()
        {
        }
    }
}
