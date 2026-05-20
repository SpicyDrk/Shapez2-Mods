using System.Collections.Generic;
using Game.Content.Features.Predictions;
using Game.Content.Features.Predictions.Processing;
using Game.Core.Coordinates;
using Game.Core.Rendering;
using Game.Core.Simulation;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Hijack;
using ShapezShifter.Hijack.Predictions;
using ILogger = Core.Logging.ILogger;

namespace SmartCutter
{
    /// <summary>
    /// Shared state across the mirror rewirer trio. Holds the mirror
    /// <see cref="BuildingDefinition"/> once it has been registered. Cleared and
    /// re-populated on each game-data rebuild.
    /// </summary>
    internal sealed class SmartCutterMirrorState
    {
        public BuildingDefinition? Mirror;
        public BuildingDefinitionId? RegisteredInGameBuildingsAtId;

        /// <summary>
        /// Mirror-variant <see cref="BuildingDrawData"/> built up-front in the mod
        /// constructor (where the mesh path is known). When non-null, the lazy
        /// mirror registration attaches this instead of the default's draw data,
        /// so the mirror variant renders its own N↔S-flipped body mesh.
        /// </summary>
        public BuildingDrawData? MirrorDrawData;
    }

    /// <summary>
    /// Helper that lazily registers the mirrored <see cref="BuildingDefinition"/>
    /// into the same <see cref="BuildingDefinitionGroup"/> as the default variant.
    ///
    /// <para>
    /// Originally this was an <see cref="IBuildingsRewirer"/>, but multi-pass
    /// rebuilds reordered the rewirer iteration and our buildings rewirer ended up
    /// running BEFORE the default's <c>BuildingsExtender</c> on later passes
    /// (default's chain self-cycles handles every pass; ours stayed at its initial
    /// handle). The skip resulted in dangling simulation/prediction systems for an
    /// id that wasn't in <see cref="GameBuildings"/>, causing
    /// <c>Could not resolve building 'SmartCutterMirrored'</c> warnings.
    /// </para>
    ///
    /// <para>
    /// The fix: do the registration from the <em>simulation-systems</em> rewirer
    /// instead. By the time that interceptor fires, the buildings interceptor has
    /// completed and the default is guaranteed to be in <c>gameBuildings</c>. We
    /// access <c>gameBuildings</c> via <c>dependencies.Mode.Buildings</c>.
    /// </para>
    /// </summary>
    internal static class SmartCutterMirrorRegistration
    {
        public static BuildingDefinition? EnsureRegistered(
            GameBuildings gameBuildings,
            BuildingDefinitionId defaultId,
            BuildingDefinitionId mirrorId,
            SmartCutterMirrorState state,
            ILogger logger)
        {
            if (gameBuildings.TryGetDefinition(mirrorId, out var existingMirrorRef) && existingMirrorRef is BuildingDefinition existingMirror)
            {
                if (state.Mirror is null || !ReferenceEquals(state.Mirror, existingMirror))
                {
                    state.Mirror = existingMirror;
                    state.RegisteredInGameBuildingsAtId = mirrorId;
                }
                return existingMirror;
            }

            if (!gameBuildings.TryGetDefinition(defaultId, out var defaultDefRef) || defaultDefRef is not BuildingDefinition defaultDef)
            {
                logger.Warning?.Log("[SmartCutter] Mirror registration: default not in gameBuildings yet (unexpected at this stage).");
                return null;
            }

            if (!defaultDef.CustomData.TryGet<BuildingDefinitionGroup>(out var group))
            {
                logger.Warning?.Log("[SmartCutter] Mirror registration: default has no BuildingDefinitionGroup attached.");
                return null;
            }

            IBuildingConnectorData mirroredConnectors = BuildingConnectors.SingleTile()
                .AddShapeInput(ShapeConnectorConfig.DefaultInput())
                .AddShapeOutput(ShapeConnectorConfig.DefaultOutput())
                .AddWireInput(WireConnectorConfig.CustomInput(TileDirection.South, BuildingSignalIOType.Wire))
                .Build();

            BuildingDefinition mirror = new BuildingDefinition(mirrorId, mirroredConnectors);
            mirror.CustomData.Attach(mirroredConnectors);

            foreach (var data in defaultDef.CustomData.All)
            {
                if (data is IBuildingConnectorData) continue;
                if (data is IBuildingMirroringDefinition) continue;
                // Skip the default's BuildingDrawData — we attach a separately-built,
                // N↔S-mirrored version below so the body geometry flips alongside the
                // wire connector. Without this skip both variants would render the
                // same asymmetric body.
                if (state.MirrorDrawData is not null && data is BuildingDrawData) continue;
                mirror.CustomData.Attach(data);
            }

            if (state.MirrorDrawData is not null)
            {
                mirror.CustomData.Attach(state.MirrorDrawData);
            }

            group.AddInternalVariant(mirror);
            defaultDef.CustomData.TryAttach<IBuildingMirroringDefinition>(new SmartCutterMirroring(mirror, isMirrored: false));
            mirror.CustomData.TryAttach<IBuildingMirroringDefinition>(new SmartCutterMirroring(defaultDef, isMirrored: true));

            gameBuildings._DefinitionsById.Add(mirrorId, mirror);

            state.Mirror = mirror;
            state.RegisteredInGameBuildingsAtId = mirrorId;

            logger.Info?.Log($"[SmartCutter] Mirror variant '{mirrorId.Name}' registered into '{group.Id.Id}'. F-key flip wired.");
            return mirror;
        }
    }

    /// <summary>
    /// Adds the mirror simulation system to the runtime list. Also serves as the
    /// hook point for lazy mirror <see cref="BuildingDefinition"/> registration,
    /// because by the time this interceptor fires we have a guaranteed reference
    /// to the fully-populated <see cref="GameBuildings"/> via
    /// <c>dependencies.Mode.Buildings</c>.
    /// </summary>
    internal sealed class SmartCutterMirrorSimulationRewirer : ISimulationSystemsRewirer
    {
        private readonly BuildingDefinitionId _defaultId;
        private readonly BuildingDefinitionId _mirrorId;
        private readonly SmartCutterMirrorState _state;
        private readonly SmartCutterFactoryBuilder _factoryBuilder;
        private readonly ILogger _logger;

        public SmartCutterMirrorSimulationRewirer(BuildingDefinitionId defaultId, BuildingDefinitionId mirrorId, SmartCutterMirrorState state, SmartCutterFactoryBuilder factoryBuilder, ILogger logger)
        {
            _defaultId = defaultId;
            _mirrorId = mirrorId;
            _state = state;
            _factoryBuilder = factoryBuilder;
            _logger = logger;
        }

        public void ModifySimulationSystems(ICollection<ISimulationSystem> simulationSystems, SimulationSystemsDependencies dependencies)
        {
            // Lazily register the mirror BuildingDefinition into the default's
            // group + GameBuildings.DefinitionsById. By this interceptor, the
            // BuildingsInterceptor has completed; default is guaranteed present.
            var gameBuildings = dependencies.Mode.Buildings;
            SmartCutterMirrorRegistration.EnsureRegistered(gameBuildings, _defaultId, _mirrorId, _state, _logger);

            var factory = _factoryBuilder.BuildFactory(dependencies, out _);
            var system = new AtomicStatefulBuildingSimulationSystem<SmartCutterSimulation, SmartCutterSimulationState>(factory, _mirrorId, _logger);
            simulationSystems.Add(system);
            _logger.Info?.Log($"[SmartCutter] Mirror simulation system registered for '{_mirrorId.Name}'.");
        }

        public bool Equals(IRewirer other) => ReferenceEquals(this, other);
    }

    /// <summary>
    /// Adds the prediction system for the mirror id. Defensively re-attempts
    /// mirror BuildingDefinition registration in case the prediction interceptor
    /// runs before the simulation interceptor in some pass.
    /// </summary>
    internal sealed class SmartCutterMirrorPredictionRewirer : IPredictionSystemsRewirer
    {
        private readonly BuildingDefinitionId _defaultId;
        private readonly BuildingDefinitionId _mirrorId;
        private readonly SmartCutterMirrorState _state;
        private readonly Operation1In1OutPredictionFactoryBuilder _predictionBuilder;
        private readonly ILogger _logger;

        public SmartCutterMirrorPredictionRewirer(BuildingDefinitionId defaultId, BuildingDefinitionId mirrorId, SmartCutterMirrorState state, Operation1In1OutPredictionFactoryBuilder predictionBuilder, ILogger logger)
        {
            _defaultId = defaultId;
            _mirrorId = mirrorId;
            _state = state;
            _predictionBuilder = predictionBuilder;
            _logger = logger;
        }

        public void ModifyPredictionSystems(ICollection<ISimulationSystem> simulationSystems, PredictionSystemsDependencies dependencies)
        {
            SmartCutterMirrorRegistration.EnsureRegistered(dependencies.Mode.Buildings, _defaultId, _mirrorId, _state, _logger);

            var factory = _predictionBuilder.BuildFactory(dependencies);
            var system = new AtomicBuildingPredictionSimulationSystem<Processing1In1OutPredictionSimulation>(factory, _mirrorId, _logger);
            simulationSystems.Add(system);
            _logger.Info?.Log($"[SmartCutter] Mirror prediction system registered for '{_mirrorId.Name}'.");
        }

        public bool Equals(IRewirer other) => ReferenceEquals(this, other);
    }

    /// <summary>
    /// Adds a module registration for the mirror id, lazily resolving the mirror
    /// <see cref="BuildingDefinition"/> from <see cref="SmartCutterMirrorState"/>
    /// populated earlier by the simulation rewirer in the same load cycle.
    /// </summary>
    internal sealed class SmartCutterMirrorModulesRewirer : IBuildingModulesRewirer
    {
        private readonly SmartCutterMirrorState _state;
        private readonly ResearchSpeedId _speedId;
        private readonly float _processingDuration;
        private readonly ILogger _logger;

        public SmartCutterMirrorModulesRewirer(SmartCutterMirrorState state, ResearchSpeedId speedId, float processingDuration, ILogger logger)
        {
            _state = state;
            _speedId = speedId;
            _processingDuration = processingDuration;
            _logger = logger;
        }

        public void AddModules(BuildingsModulesLookup modulesLookup)
        {
            if (_state.Mirror is not { } mirror)
            {
                _logger.Warning?.Log("[SmartCutter] MirrorModulesRewirer: no mirror definition available, skipping module registration this pass.");
                return;
            }

            var modules = new ItemSimulationBuildingModuleDataProvider(
                new ResearchSpeedId("BeltSpeed"),
                _speedId,
                _processingDuration,
                1,
                ItemSimulationEfficiencyMeasurementMode.ByOutput);

            modulesLookup.AddModule(mirror.Id, mirror, modules);
            _logger.Info?.Log($"[SmartCutter] Mirror module registered for '{mirror.Id.Name}'.");
        }

        public bool Equals(IRewirer other) => ReferenceEquals(this, other);
    }
}
