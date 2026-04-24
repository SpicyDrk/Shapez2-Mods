using System;
using System.Collections.Generic;
using Core.Localization;
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
using ILogger = Core.Logging.ILogger;

namespace FourWaySplitter
{
    /// <summary>
    /// FourWaySplitter — a 1×1 / 2-level Shapez 2 building that splits the
    /// four quadrants of an incoming shape to four cardinal outputs on the
    /// upper platform level. Ships without a research gate. The v1 building
    /// has no visible mesh — all LOD slots are <see cref="LODEmptyMesh"/>
    /// placeholders (CONSTRAINTS §5b MUST NOT bundle custom FBX in v1; a real
    /// mesh is ROADMAP R7 / P03 polish). Toolbar icon + connector arrows still
    /// render; only the in-world building body is invisible.
    ///
    /// Registration chain mirrors <c>DiagonalCuttersMod</c> from the official
    /// tobspr samples. Five deliberate deviations from DiagonalCutter:
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
    ///     Originally we called <c>WithCustomModules(...)</c> to avoid HUD
    ///     shape-processing stats driven by the 1In2Out prediction framework
    ///     (which doesn't cover our 4-output case). That produced a
    ///     NullReferenceException during mod-load inside Shifter's
    ///     <c>BuildExtenders</c> — see UAT-P02.md (2026-04-23) for the
    ///     diagnosis. Aligned with
    ///     <c>WithAtomicShapeProcessingModules(CutterSpeed, 2.0f)</c> which
    ///     mirrors DiagonalCutter's chain verbatim; the HUD module will
    ///     display a single-speed stat that reflects our simulation's
    ///     ProcessingDelay reasonably well for v1.
    ///   </item>
    ///   <item>
    ///     Prediction is a deliberately-minimal single-quadrant stub
    ///     (<see cref="Operation1In4OutPredictionFactoryBuilder"/>) rather
    ///     than a correct 4-output prediction. The installed Shifter workshop
    ///     version (v1.0.0) has an unpatched NRE at
    ///     <c>AtomicBuildingExtender.cs:158</c> — <c>LazyPredictionExtender</c>
    ///     is dereferenced without a null-check. Fixed upstream in Shifter
    ///     commit <c>54d5e38</c> (2026-04-12) but not yet shipped to the
    ///     Steam Workshop. <c>WithoutPrediction()</c> leaves the field null
    ///     and triggers that NRE; supplying ANY non-null prediction builder
    ///     sidesteps it. Full 4-output prediction is deferred — the game's
    ///     prediction framework caps at 1In1Out / 1In2Out. See UAT-P02.md
    ///     for the full diagnosis.
    ///   </item>
    ///   <item>
    ///     Static draw data is a self-contained <see cref="LODEmptyMesh"/>
    ///     placeholder in every LOD slot (<see cref="CreateDrawData"/>), not
    ///     a copy of another building's mesh. The original Plan-002 approach
    ///     — <c>WithCopiedStaticDrawData(new BuildingDefinitionId("DiagonalCutter"))</c>
    ///     — threw <c>KeyNotFoundException: "DiagonalCutter"</c> at
    ///     <c>MainMenuOrchestrator</c> ctor → <c>GameBuildings.GetDefinition</c>.
    ///     "DiagonalCutter" is the sample mod's id, not a built-in game
    ///     building. See UAT-P02.md (2026-04-23) for the crash trace; a real
    ///     mesh is deferred to ROADMAP R7 / P03 polish. Placeholder is
    ///     permitted under CONSTRAINTS §5b (MUST NOT bundle custom FBX in v1)
    ///     — <c>LODEmptyMesh</c> is not an FBX and adds no art asset.
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

            // Static draw data: self-contained LODEmptyMesh placeholders in
            // every LOD slot (see CreateDrawData). v1 building has no visible
            // mesh — CONSTRAINTS §5b MUST NOT bundle custom FBX in v1, and the
            // original WithCopiedStaticDrawData("DiagonalCutter") approach
            // crashed at main-menu init with KeyNotFoundException (the sample
            // mod's id isn't a built-in game id). See deviation #5 in the
            // class-level docstring and UAT-P02.md (2026-04-23).
            // TODO(P03): bundle a real mesh — see ROADMAP R7.
            // P03 Task 3 mesh upgrade attempt (PLAN-P03-001). WithCopiedStaticDrawData
            // reuses an existing game building's mesh rather than the invisible
            // LODEmptyMesh placeholder. "FullCutter" is a candidate id found via
            // `strings SPZGameAssembly.dll` per STATE.md parking lot (2026-04-23).
            // CS0618: the string-ctor for BuildingDefinitionId is marked obsolete
            // with guidance to avoid hardcoded ids; for cross-building mesh reuse
            // there's no non-obsolete alternative (same situation as DiagonalCutter's
            // cross-id references). Suppress locally.
            // Stop-rule (per plan): if this id doesn't resolve at mod-load, revert
            // to LODEmptyMesh via CreateDrawData() below (kept as fallback).
#pragma warning disable CS0618
            BuildingDefinitionId fullCutterId = new("FullCutter");
#pragma warning restore CS0618

            IBuildingBuilder fourWaySplitterBuilder = Building.Create(definitionId)
                .WithConnectorData(connectorData)
                .DynamicallyRendering<FourWaySplitterSimulationRenderer, FourWaySplitterSimulation,
                    IFourWaySplitterDrawData>(new FourWaySplitterDrawData())
                .WithCopiedStaticDrawData(fullCutterId)
                // Fallback (commented out, swap with WithCopiedStaticDrawData above if
                // FullCutter doesn't resolve): .WithStaticDrawData(CreateDrawData())
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
                .WithAtomicShapeProcessingModules(BuiltinResearchSpeed.CutterSpeed, 2.0f)
                // Prediction is intentionally a single-quadrant (north-projection)
                // stub — not a correct 4-output prediction. The installed Shifter
                // workshop version (v1.0.0) has an unpatched NRE at
                // AtomicBuildingExtender.cs:158 where LazyPredictionExtender is
                // dereferenced without a null-check (fixed upstream in commit
                // 54d5e38 but not yet shipped to Steam Workshop). Calling
                // WithoutPrediction() leaves the field null and triggers that NRE
                // during mod load. Supplying any non-null IBuildingPredictionFactoryBuilder
                // sidesteps the bug. Full 1In4Out prediction is deferred — see
                // Operation1In4OutPredictionFactoryBuilder.cs and UAT-P02.md.
                .WithPrediction(new Operation1In4OutPredictionFactoryBuilder(), logger)
                .Build();
        }

        /// <summary>
        /// Constructs a fully-empty <see cref="BuildingDrawData"/> whose LOD
        /// slots are all <see cref="LODEmptyMesh"/> instances. v1 building has
        /// no visible in-world mesh — CONSTRAINTS §5b MUST NOT bundle custom
        /// FBX in v1, and the original plan to reuse DiagonalCutter's mesh
        /// via <c>WithCopiedStaticDrawData</c> crashed at main-menu init with
        /// <c>KeyNotFoundException</c> ("DiagonalCutter" is the sample mod's
        /// id, not a built-in). The toolbar icon and placement-time connector
        /// arrows still render; simulation runs normally. See class-level
        /// deviation #5 + UAT-P02.md (2026-04-23).
        /// <para>
        /// Positional args match the <c>BuildingDrawData</c> ctor signature
        /// <c>(bool, ILODMesh[], ILODMesh, ILODMesh, IMeshReference, ILODMesh,
        /// CollisionBox[], IBuildingCustomDrawData, bool, IMeshReference,
        /// bool)</c>. The Shifter/Game.Core.Rendering reference assembly on
        /// disk does not expose ctor parameter names to reflection, so named
        /// arguments beyond <c>renderVoidBelow</c> are not available. Slot
        /// roles inferred from the DiagonalCutter sample pattern: the
        /// <c>ILODMesh[]</c> is the primary LOD ladder, the three solo
        /// <c>ILODMesh</c> slots correspond to shadow / special-render / support
        /// passes, the <c>IMeshReference</c> slots hold close-LOD references
        /// (nullable), the <c>CollisionBox[]</c> is an empty array (no
        /// collision volume needed — placement is tile-based), and the trailing
        /// booleans default to <c>false</c>.
        /// </para>
        /// </summary>
        private static BuildingDrawData CreateDrawData()
        {
            // Shared single instance is fine — LODEmptyMesh has no per-slot
            // mutable state (it's the no-op mesh type the game uses for
            // buildings that render nothing at a given LOD tier).
            var empty = new LODEmptyMesh();
            ILODMesh[] lodLadder = { empty, empty, empty };

            // Null-forgiving (`!`) on the nullable reference-type slots: the
            // reference assembly declares these params as non-nullable but the
            // ctor body accepts null (verified via compile-probe — see class
            // docstring deviation #5). Using `!` keeps warnings at zero without
            // disabling nullable analysis for the whole method.
            return new BuildingDrawData(
                false,                                  // renderVoidBelow
                lodLadder,                              // ILODMesh[] — primary LOD ladder
                empty,                                  // ILODMesh  — shadow pass
                empty,                                  // ILODMesh  — special pass
                ((IMeshReference)null!),                // IMeshReference — close-LOD ref (runtime-nullable)
                empty,                                  // ILODMesh  — support pass
                Array.Empty<CollisionBox>(),            // CollisionBox[] — no tile-based collision volume needed
                ((IBuildingCustomDrawData)null!),       // IBuildingCustomDrawData — no custom draw payload
                false,                                  // bool — trailing flag (default per sample)
                ((IMeshReference)null!),                // IMeshReference — secondary ref (runtime-nullable)
                false                                   // bool — trailing flag (default per sample)
            );
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
