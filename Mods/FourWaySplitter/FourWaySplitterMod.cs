using System;
using System.Collections.Generic;
using Core.Localization;
using Game.Core.Coordinates;
using Game.Core.Modding;
using JetBrains.Annotations;
using ShapezShifter.Flow;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Flow.Research;
using ShapezShifter.Flow.Toolbar;
using ShapezShifter.Kit;
using ShapezShifter.Textures;
using ILogger = Core.Logging.ILogger;

namespace FourWaySplitter
{
    /// <summary>
    /// FourWaySplitter — a 1×1 / 2-level Shapez 2 building that splits the
    /// four quadrants of an incoming shape to four cardinal outputs on the
    /// upper platform level. Ships without a research gate, reuses
    /// DiagonalCutter's visual mesh as a v1 placeholder (CONSTRAINTS §5a/§5b —
    /// no custom FBX).
    ///
    /// Registration chain mirrors <c>DiagonalCuttersMod</c> from the official
    /// tobspr samples. Three deliberate deviations from DiagonalCutter:
    /// <list type="number">
    ///   <item>
    ///     Connector data is hand-crafted (not <c>BuildingConnectors.SingleTile()</c>)
    ///     because the fluent builder hardcodes <c>Position_L = TileVector.Zero</c>
    ///     for every connector. Our four outputs sit on level 1
    ///     (<c>TileVector(0, 0, 1)</c>), which requires a direct <see cref="BuildingConnectorData"/>
    ///     construction. See STATE.md P02 API findings (2026-04-22) for the
    ///     upstream source audit that motivated this approach.
    ///   </item>
    ///   <item>
    ///     <see cref="ShapezShifter.Flow.Atomic.IDefinedBuildingExtender"/> exposes
    ///     only three unlock methods — milestone, new side-upgrade, existing
    ///     side-upgrade. There's no "unlocked immediately". We use
    ///     <c>UnlockedAtMilestone</c> with milestone index 0 (the scenario's
    ///     starting research state), which is the closest equivalent to "available
    ///     from game start" and does not introduce a player-visible gate.
    ///     CONSTRAINTS §5b MUST NOT: no research-gate / side-upgrade unlock.
    ///   </item>
    ///   <item>
    ///     <c>WithCustomModules</c> (not <c>WithAtomicShapeProcessingModules</c>)
    ///     because our 4-output simulation bypasses the 1In2Out framework —
    ///     attaching the shape-processing modules would produce incorrect HUD
    ///     stats. <c>WithoutPrediction</c> is the MVP choice — a proper 4-out
    ///     prediction simulation is deferred (see
    ///     <see cref="Operation1In4OutPredictionFactoryBuilder"/>).
    ///   </item>
    /// </list>
    /// </summary>
    [UsedImplicitly]
    public class FourWaySplitterMod : IMod
    {
        public FourWaySplitterMod(ILogger logger)
        {
            // CS0618: the string-ctor is marked obsolete with "Do not hardcode
            // building ids" — but defining a NEW building literally requires
            // that we pick its id. Samples (DiagonalCutter et al.) use the
            // same ctor; the obsolete message is steering for consumers who
            // reference existing buildings, not creators of new ones. Suppress
            // locally so `dotnet build` stays at 0-warnings.
#pragma warning disable CS0618
            BuildingDefinitionGroupId groupId = new("FourWaySplitterGroup");
            BuildingDefinitionId definitionId = new("FourWaySplitter");
#pragma warning restore CS0618

            string titleId = "building-variant.four-way-splitter.title";
            string descriptionId = "building-variant.four-way-splitter.description";

            // Placeholder icon loaded from Resources/ at runtime. CONSTRAINTS §5b
            // permits placeholder PNGs in v1 (no custom FBX, but a simple icon is
            // fine). Mirrors the DiagonalCutter sample's pattern for wiring an
            // external texture into the building group. Using `FileTextureLoader`
            // avoids passing a null Sprite into Shifter's AtomicBuildingExtender —
            // downstream consumers (e.g. ToolbarRewirer.BuildToolbarExtenderFunc)
            // dereference `group.Icon` and NRE on null. See UAT-P02.md.
            ModFolderLocator modResourcesLocator =
                ModDirectoryLocator.CreateLocator<FourWaySplitterMod>().SubLocator("Resources");

            string iconPath = modResourcesLocator.SubPath("FourWaySplitter_Icon.png");

            IBuildingGroupBuilder fourWaySplitterGroup = BuildingGroup.Create(groupId)
                .WithTitle(titleId.T())
                .WithDescription(descriptionId.T())
                .WithIcon(FileTextureLoader.LoadTextureAsSprite(iconPath, out _))
                .AsNonTransportableBuilding()
                .WithPreferredPlacement(DefaultPreferredPlacementMode.LinePerpendicular)
                .WithDefaultStructureOverview();

            IBuildingConnectorData connectorData = BuildFourWayConnectorData();

            // Static draw data: reuse DiagonalCutter's mesh for v1 (CONSTRAINTS
            // §5a PREFER reusing existing game meshes; §5b MUST NOT add custom
            // FBX assets in v1). DiagonalCutter's visible model is good enough
            // as a placeholder — a real sprite / mesh is P03 / a follow-up story.
            IBuildingBuilder fourWaySplitterBuilder = Building.Create(definitionId)
                .WithConnectorData(connectorData)
                .DynamicallyRendering<FourWaySplitterSimulationRenderer, FourWaySplitterSimulation,
                    IFourWaySplitterDrawData>(new FourWaySplitterDrawData())
#pragma warning disable CS0618 // 'Do not hardcode building ids' — intentional for cross-mod mesh reuse (CONSTRAINTS §5a: PREFER reusing existing game meshes). No public non-obsolete alternative exists for referencing another building's static draw data by id.
                .WithCopiedStaticDrawData(new BuildingDefinitionId("DiagonalCutter"))
#pragma warning restore CS0618
                .WithoutSound()
                .WithoutSimulationConfiguration()
                .WithEfficiencyData(new BuildingEfficiencyData(2.0f, 1));

            AtomicBuildings.Extend()
                .AllScenarios()
                .WithBuilding(fourWaySplitterBuilder, fourWaySplitterGroup)
                // Milestone 0 = scenario's starting research state. Closest
                // Shifter equivalent to "always unlocked" — no side-upgrade,
                // no research-gate. See class-level docstring deviation #2.
                .UnlockedAtMilestone(new ByIndexMilestoneSelector(new Index(0)))
                .WithDefaultPlacement()
                // Toolbar placement: same path DiagonalCutter uses — next to
                // the cutter cluster. Resolution is runtime-dependent on the
                // player's game version; verification deferred to `/oes:verify 2`
                // (PLAN-P02-002 R4 stop-rule waived because path resolution is
                // not a compile-time check).
                .InToolbar(ToolbarElementLocator.Root().ChildAt(0).ChildAt(2).ChildAt(^1).InsertAfter())
                .WithSimulation(new FourWaySplitterFactoryBuilder(), logger)
                .WithCustomModules(new FourWaySplitterBuildingModules())
                .WithoutPrediction()
                .Build();
        }

        /// <summary>
        /// Constructs the 5-connector <see cref="BuildingConnectorData"/> by
        /// hand (bypassing <see cref="BuildingConnectors.SingleTile"/> because
        /// that builder hardcodes <c>Position_L = TileVector.Zero</c> on every
        /// connector — we need the 4 outputs at <c>TileVector(0, 0, 1)</c> so
        /// they appear on the upper platform level).
        /// <list type="bullet">
        ///   <item>1 input: level 0 south (<c>TileVector(0, 0, 0)</c>, <see cref="TileDirection.South"/>).</item>
        ///   <item>4 outputs: level 1 (<c>TileVector(0, 0, 1)</c>) — North, East, South, West.</item>
        /// </list>
        /// Each connector is a plain object initializer on the publicized
        /// <see cref="BuildingItemInput"/> / <see cref="BuildingItemOutput"/>
        /// records — exactly the shape <c>SingleTileBuildingConnectorDataBuilder</c>
        /// produces, just with explicit non-zero <c>Position_L</c> on the outputs.
        /// </summary>
        private static IBuildingConnectorData BuildFourWayConnectorData()
        {
            // Level-0 (lower platform) input on the south face. Shape items
            // enter here from an upstream south-facing belt.
            var inputSouthLevel0 = new BuildingItemInput
            {
                Position_L = new TileVector(0, 0, 0),
                Direction_L = TileDirection.South.Value,
                StandType = BuildingBeltStandType.Normal,
                IOType = BuildingItemIOType.ElevatedBorder,
                Seperators = false
            };

            // Level-1 (upper platform) outputs. Four cardinals per CONSTRAINTS
            // §5b clockwise-spatial mapping (TR→N / BR→E / BL→S / TL→W).
            var outputNorthLevel1 = new BuildingItemOutput
            {
                Position_L = new TileVector(0, 0, 1),
                Direction_L = TileDirection.North.Value,
                StandType = BuildingBeltStandType.Normal,
                IOType = BuildingItemIOType.ElevatedBorder,
                Seperators = false
            };

            var outputEastLevel1 = new BuildingItemOutput
            {
                Position_L = new TileVector(0, 0, 1),
                Direction_L = TileDirection.East.Value,
                StandType = BuildingBeltStandType.Normal,
                IOType = BuildingItemIOType.ElevatedBorder,
                Seperators = false
            };

            var outputSouthLevel1 = new BuildingItemOutput
            {
                Position_L = new TileVector(0, 0, 1),
                Direction_L = TileDirection.South.Value,
                StandType = BuildingBeltStandType.Normal,
                IOType = BuildingItemIOType.ElevatedBorder,
                Seperators = false
            };

            var outputWestLevel1 = new BuildingItemOutput
            {
                Position_L = new TileVector(0, 0, 1),
                Direction_L = TileDirection.West.Value,
                StandType = BuildingBeltStandType.Normal,
                IOType = BuildingItemIOType.ElevatedBorder,
                Seperators = false
            };

            // All connectors in a single list. BuildingConnectorData takes
            // BuildingBaseIO which is the common base of input + output records.
            var allConnectors = new List<BuildingBaseIO>
            {
                inputSouthLevel0,
                outputNorthLevel1,
                outputEastLevel1,
                outputSouthLevel1,
                outputWestLevel1
            };

            // Tile / bounds metadata: single-tile 1×1×1 footprint (same bounds
            // as DiagonalCutter). Level extent is implicit — the Z=1 outputs
            // sit on the upper platform via the connector Position_L, not via
            // the tile bounds themselves. If the game's placement engine rejects
            // connectors outside tileBounds at runtime, we'll discover it during
            // `/oes:verify 2` and expand bounds to cover Z=0..1.
            TileVector[] tiles = { TileVector.Zero };
            LocalTileBounds tileBounds = new(min: TileVector.Zero, max: TileVector.Zero);
            TileDimensions tileDimensions = tileBounds.Dimensions;
            LocalVector tileBoundsCenter = LocalVector.Lerp(
                a: (LocalVector)tileBounds.Min,
                b: (LocalVector)tileBounds.Max,
                t: 0.5f);

            return new BuildingConnectorData(
                allInputs: allConnectors,
                tiles: tiles,
                tileBounds: tileBounds,
                tileBoundsCenter: tileBoundsCenter,
                tileDimensions: tileDimensions);
        }

        public void Dispose()
        {
        }
    }
}
