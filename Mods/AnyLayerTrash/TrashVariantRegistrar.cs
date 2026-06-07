using System.Collections.Generic;
using System.Linq;
using ShapezShifter.Hijack;
using ShapezShifter.Hijack.Predictions;
using ILogger = Core.Logging.ILogger;

namespace AnyLayerTrash
{
    /// <summary>
    /// Cross-link that makes the placement "flip" key cycle between the vanilla
    /// trash variant and our modded "Any Layer Trash" variant — exactly the
    /// mechanism vanilla mirror-pairs use (<c>BuildingDefinitionFactory</c> attaches
    /// a <see cref="IBuildingMirroringDefinition"/> to each side of a mirror pair,
    /// and the placement system flips between <c>MirroredDefinition</c>s). We reuse
    /// it not for geometry mirroring but as the only in-engine way to make a second
    /// same-group variant selectable during placement. Mirrors
    /// <c>SmartCutter.SmartCutterMirroring</c>.
    /// </summary>
    internal sealed class TrashVariantMirroring : IBuildingMirroringDefinition
    {
        public IBuildingDefinition MirroredDefinition { get; }
        public bool IsMirrored { get; }

        public TrashVariantMirroring(IBuildingDefinition mirroredDefinition, bool isMirrored)
        {
            MirroredDefinition = mirroredDefinition;
            IsMirrored = isMirrored;
        }
    }

    /// <summary>
    /// Registers the modded "Any Layer Trash" variant into the EXISTING vanilla
    /// trash group so the two coexist (the original trash is untouched; the modded
    /// one is reached via the placement flip key, like the cutter/stacker variants).
    ///
    /// <para><b>Coexist design (2026-06-06)</b> — supersedes the old hijack, where
    /// <c>TrashActionInterceptor</c> expanded EVERY vanilla trash placement into a
    /// 3-layer column. Now the column-stamp fires ONLY for this modded variant; the
    /// modded variant never lands on the map (the interceptor swaps it to vanilla
    /// trash on every layer at commit), so it needs no simulation / prediction /
    /// module systems — it exists purely as a placeable, flip-selectable clone.</para>
    ///
    /// <para><b>Why a rewirer at simulation-systems time</b> (not <c>IBuildingsRewirer</c>):
    /// the same ordering lesson SmartCutter hit — Shifter's default-chain rewirers
    /// self-cycle their handles every game-data rebuild pass, but a mod's own
    /// static-handle buildings-rewirer can end up running BEFORE the vanilla trash
    /// group exists on later passes, leaving the clone dangling. By the time the
    /// simulation (and prediction) interceptors fire, the buildings interceptor has
    /// completed and the vanilla trash group is guaranteed present in
    /// <c>dependencies.Mode.Buildings</c>. We re-ensure idempotently in both.</para>
    /// </summary>
    internal sealed class TrashVariantRegistrar : ISimulationSystemsRewirer, IPredictionSystemsRewirer
    {
        // Unique id for the modded variant. Any unique string works; the engine
        // never persists it (placements are swapped to vanilla trash at commit).
#pragma warning disable CS0618 // string ctor is steered against for CONSUMERS of
                               // existing buildings; defining a new variant requires it.
        private static readonly BuildingDefinitionId ModdedId = new("AnyLayerTrashVariant");
#pragma warning restore CS0618

        private readonly TrashTrioState _state;
        private readonly ILogger _logger;

        public TrashVariantRegistrar(TrashTrioState state, ILogger logger)
        {
            _state = state;
            _logger = logger;
        }

        public void ModifySimulationSystems(
            ICollection<ISimulationSystem> simulationSystems,
            SimulationSystemsDependencies dependencies)
        {
            EnsureRegistered(dependencies.Mode.Buildings);
            // Intentionally NO simulation system for the modded id: it never lands
            // (swapped to vanilla trash at commit), so nothing of it is ever simulated.
        }

        public void ModifyPredictionSystems(
            ICollection<ISimulationSystem> simulationSystems,
            PredictionSystemsDependencies dependencies)
        {
            // Defensive re-ensure in case the prediction interceptor runs before the
            // simulation one in some pass (SmartCutter does the same).
            EnsureRegistered(dependencies.Mode.Buildings);
        }

        /// <summary>
        /// Idempotently clone the vanilla trash default into the modded variant and
        /// register it into the CURRENT <paramref name="gameBuildings"/> trash group.
        /// Re-runs cleanly each rebuild (each rebuild = fresh GameBuildings/group, so
        /// the modded id is absent again and we re-clone from the fresh default).
        /// </summary>
        private void EnsureRegistered(GameBuildings gameBuildings)
        {
            BuildingDefinitionGroupId trashGroupId = gameBuildings.TrashBuildingId;
            if (!gameBuildings._VariantsById.TryGetValue(trashGroupId, out IBuildingDefinitionGroup? groupRef)
                || groupRef is not BuildingDefinitionGroup group)
            {
                _state.TrashGroupCaptured = false;
                _logger.Warning?.Log(
                    $"[AnyLayerTrash:variant] trash group '{trashGroupId.Id}' not in gameBuildings; " +
                    "modded variant not registered this pass.");
                return;
            }

            // Always (re)capture the vanilla side for this gameBuildings: the group
            // id, every vanilla variant id (the interceptor's redo-gate filter), and
            // the default definition we stamp on every layer.
            _state.TrashGroupId = trashGroupId;
            _state.ModdedTrashVariantId = ModdedId;
            _state.VanillaTrashVariantIds.Clear();
            foreach (IBuildingDefinition d in group.Definitions)
            {
                if (!d.Id.Equals(ModdedId)) _state.VanillaTrashVariantIds.Add(d.Id);
            }

            if (group.Definitions.FirstOrDefault(d => !d.Id.Equals(ModdedId)) is not BuildingDefinition defaultDef)
            {
                _state.TrashGroupCaptured = false;
                _logger.Warning?.Log("[AnyLayerTrash:variant] vanilla trash default definition not found in group.");
                return;
            }
            _state.VanillaTrashDefault = defaultDef;
            _state.TrashGroupCaptured = true;

            // Already present in THIS gameBuildings? (same pass re-entry / pred after sim.)
            if (gameBuildings.TryGetDefinition(ModdedId, out IBuildingDefinition? existing) && existing is BuildingDefinition)
            {
                _state.ModdedVariantRegistered = true;
                return;
            }

            // Clone the vanilla trash default → modded variant. Reuse vanilla
            // connectors + copy all other CustomData (draw data, group back-ref,
            // placement prefs, sim config, modules) so the modded variant PREVIEWS
            // and renders exactly like trash. Skip the connector data (passed to the
            // ctor + attached below) and any mirroring (we attach our own pair).
            var modded = new BuildingDefinition(ModdedId, defaultDef.ConnectorData);
            modded.CustomData.Attach(defaultDef.ConnectorData);
            foreach (object data in defaultDef.CustomData.All)
            {
                if (data is IBuildingConnectorData) continue;
                if (data is IBuildingMirroringDefinition) continue;
                modded.CustomData.Attach(data);
            }

            group.AddInternalVariant(modded);
            gameBuildings._DefinitionsById.Add(ModdedId, modded);

            // Flip-pair cross-link: vanilla <-> modded, so the placement flip key
            // cycles between them. TryAttach (no-op if already present) keeps a
            // re-ensure from double-attaching on the same defs.
            defaultDef.CustomData.TryAttach<IBuildingMirroringDefinition>(new TrashVariantMirroring(modded, isMirrored: false));
            modded.CustomData.TryAttach<IBuildingMirroringDefinition>(new TrashVariantMirroring(defaultDef, isMirrored: true));

            _state.ModdedVariantRegistered = true;
            _logger.Info?.Log(
                $"[AnyLayerTrash:variant] registered modded variant '{ModdedId.Name}' into trash group " +
                $"'{trashGroupId.Id}' (flip-selectable; placement stamps a vanilla trash column on every layer).");
        }

        public bool Equals(IRewirer other) => ReferenceEquals(this, other);
    }
}
