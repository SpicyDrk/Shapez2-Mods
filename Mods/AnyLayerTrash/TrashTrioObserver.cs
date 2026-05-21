using System.Collections.Generic;
using Core.Events;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using Game.Core.Simulation;
using ILogger = Core.Logging.ILogger;

namespace AnyLayerTrash
{
    /// <summary>
    /// Trio-tracking observer for the ghost-spawn approach (D5 in INTENT,
    /// PLAN-P01-006). Task 2 extends the Task-1 diagnostic scaffold with the
    /// per-(X,Y) trio registry, in-flight spawn/removal guards, and
    /// FRESH / INFLIGHT / SAVE-LOAD classification — still bookkeeping only,
    /// no map mutation. Tasks 3-4 add the spawn + symmetric-removal logic
    /// once the registry is proven correct against real placement and
    /// save-load events.
    ///
    /// <para><b>Coordinate scheme (verified from Task 1 logs):</b> the layer
    /// step in the global tile Z is <see cref="LayerStep"/> = 20. UI layer 1
    /// sits at z=0, UI layer 2 at z=20, UI layer 3 at z=40. The
    /// <c>GlobalTileCoordinate.BuildingLayer()</c> extension returns
    /// <c>z mod 20</c> (the in-layer offset, always 0 for tile-anchored
    /// buildings) — not the UI layer number. We key the trio registry on
    /// (x, y) tile column and a layer ordinal computed by <c>z / 20</c>.</para>
    ///
    /// <para><b>Sim vs prediction:</b> a separate observer instance runs on
    /// each side (constructed by <see cref="TrashTrioRewirer"/>). Only the
    /// sim-side instance owns the authoritative trio registry — Task 1 logs
    /// showed prediction fires noticeably more often (placement preview
    /// re-runs the prediction simulator). Putting mutation on prediction
    /// would mean spawning duplicates on every cursor drag. The prediction
    /// observer is therefore kept for diagnostic logging only (Task 2 logs
    /// classifications on both sides; Tasks 3-4 will gate mutation on
    /// <c>_isAuthoritative</c>).</para>
    /// </summary>
    internal sealed class TrashTrioObserver : IBuildingObserverSimulationSystem
    {
        private const int LayerStep = 20;
        public const int VanillaLayerCount = 3;

        private readonly TrashTrioState _state;
        private readonly ILogger _logger;
        private readonly string _side;
        private readonly bool _isAuthoritative;

        private readonly MultiRegisterEvent<IConnectableSimulation> _onCreated = new();
        private readonly MultiRegisterEvent<IConnectableSimulation> _onBeforeDestroyed = new();

        /// <summary>
        /// Per-(x,y) column of layer-ordinals (0=L1, 1=L2, 2=L3) currently
        /// hosting a vanilla trash. Owned by the authoritative (sim-side)
        /// observer only. The prediction observer doesn't write to this — its
        /// registry would be wiped/rebuilt on every preview tick anyway.
        /// </summary>
        private readonly Dictionary<(int x, int y), HashSet<int>> _knownTrioLayers = new();

        /// <summary>
        /// Anchor GTCs of siblings we are about to add ourselves. The observer
        /// will see its own <c>BuildingWasAdded</c> callback for these and
        /// must NOT recurse into another spawn pass. Cleared when the
        /// callback fires for the matching GTC. Task 3 populates this; Task 2
        /// just declares it so the classification logic is in place.
        /// </summary>
        private readonly HashSet<GlobalTileCoordinate> _inFlightSpawns = new();

        /// <summary>
        /// Anchor GTCs of siblings we are about to remove ourselves. Symmetric
        /// guard to <see cref="_inFlightSpawns"/> for Task 4. Declared now so
        /// the REM classification logic is in place.
        /// </summary>
        private readonly HashSet<GlobalTileCoordinate> _inFlightRemovals = new();

        public TrashTrioObserver(TrashTrioState state, ILogger logger, string side, bool isAuthoritative)
        {
            _state = state;
            _logger = logger;
            _side = side;
            _isAuthoritative = isAuthoritative;
        }

        // ISimulationSystem members — we don't manage any simulations ourselves.
        public IEvent<IConnectableSimulation> OnSimulationCreated => _onCreated;
        public IEvent<IConnectableSimulation> OnBeforeSimulationDestroyed => _onBeforeDestroyed;
        public IEnumerable<IConnectableSimulation> ConnectableSimulations => System.Array.Empty<IConnectableSimulation>();

        public void BuildingWasAdded(in BuildingInstance building, IReadOnlyMapLayout layout)
        {
            if (!IsVanillaTrash(in building)) return;

            GlobalTileCoordinate anchor = GetAnchor(in building);
            int layerOrd = LayerOrdinal(anchor.z);

            // Prediction-side observer just logs and gets out — no registry
            // ownership, no classification (prediction re-fires whole batches
            // on preview ticks and would make classifications meaningless).
            if (!_isAuthoritative)
            {
                _logger.Info?.Log(
                    $"[AnyLayerTrash:trio:{_side}] ADD id={building.Definition.Id.Name} " +
                    $"anchor={anchor} layerOrd={layerOrd}");
                return;
            }

            // Sim-side classification.
            string classification;
            bool shouldSpawnSiblings = false;
            if (_inFlightSpawns.Remove(anchor))
            {
                classification = "INFLIGHT";
                TouchColumn(anchor).Add(layerOrd);
            }
            else
            {
                var occupiedLayers = TouchColumn(anchor);
                if (occupiedLayers.Count == 0)
                {
                    classification = "FRESH";
                    occupiedLayers.Add(layerOrd);
                    shouldSpawnSiblings = true;
                }
                else
                {
                    classification = "SAVE-LOAD";
                    occupiedLayers.Add(layerOrd);
                }
            }

            var column = _knownTrioLayers[(anchor.x, anchor.y)];
            var pending = PendingLayers(column);
            var pendingList = new List<int>();
            foreach (int p in pending) pendingList.Add(p);

            _logger.Info?.Log(
                $"[AnyLayerTrash:trio:{_side}] {classification} id={building.Definition.Id.Name} " +
                $"anchor={anchor} layerOrd={layerOrd} occupied={FormatSet(column)} " +
                $"pending-spawn={FormatList(pendingList)}");

            if (shouldSpawnSiblings && pendingList.Count > 0)
            {
                SpawnSiblings(in building, anchor, pendingList, layout);
            }
        }

        /// <summary>
        /// Adds vanilla 1×1×1 trash siblings on every <paramref name="missingLayers"/>
        /// ordinal at the same (x,y) as <paramref name="original"/>. Casts the
        /// layout to <see cref="IMapLayout"/>; if the cast fails (e.g.,
        /// save-load's <c>StepByStepRevealingMapLayout</c>, which is
        /// <see cref="IReadOnlyMapLayout"/>-only), skips the spawn pass and
        /// logs the reason.
        ///
        /// <para>Sibling anchor GTCs are inserted into <see cref="_inFlightSpawns"/>
        /// BEFORE <c>AddBuilding</c> so the re-entrant
        /// <see cref="BuildingWasAdded"/> callback classifies them as INFLIGHT
        /// and does not recurse into another spawn pass.</para>
        /// </summary>
        private void SpawnSiblings(
            in BuildingInstance original,
            GlobalTileCoordinate originalAnchor,
            List<int> missingLayers,
            IReadOnlyMapLayout layout)
        {
            if (layout is not IMapLayout mapLayout)
            {
                _logger.Warning?.Log(
                    $"[AnyLayerTrash:trio:sim] cannot spawn siblings at " +
                    $"{originalAnchor} — layout is {layout.GetType().Name}, not IMapLayout. " +
                    $"(Expected during save-load reveal sweep; siblings should already exist in the save.)");
                return;
            }

            GridRotation rot = original.Transform.Rotation;
            IBuildingDefinition def = original.Definition;
            IBuildingConfiguration? config = original.Configuration;

            foreach (int layerOrd in missingLayers)
            {
                short targetZ = (short)(layerOrd * LayerStep);
                var siblingPos = new GlobalTileCoordinate(originalAnchor.x, originalAnchor.y, targetZ);
                var siblingTransform = new GlobalTileTransform(siblingPos, rot);

                // Mark BEFORE AddBuilding so the re-entrant callback sees us.
                _inFlightSpawns.Add(siblingPos);

                try
                {
                    var sibling = new BuildingInstance(def, in siblingTransform, new SimulationStateContainer(), config);
                    mapLayout.AddBuilding(sibling);
                    _logger.Info?.Log(
                        $"[AnyLayerTrash:trio:sim] spawned sibling at {siblingPos} layerOrd={layerOrd}");
                }
                catch (System.Exception ex)
                {
                    // Failed to add (intersection, validation, etc.). Drop the
                    // in-flight marker so future attempts can retry, log the
                    // failure, and continue with the remaining layers — partial
                    // success is better than no spawn at all.
                    _inFlightSpawns.Remove(siblingPos);
                    _logger.Exception?.LogException(ex);
                    _logger.Warning?.Log(
                        $"[AnyLayerTrash:trio:sim] AddBuilding failed for sibling at " +
                        $"{siblingPos} layerOrd={layerOrd} — {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        public void BuildingWillBeRemoved(in BuildingInstance building, IReadOnlyMapLayout layout)
        {
            if (!IsVanillaTrash(in building)) return;

            GlobalTileCoordinate anchor = GetAnchor(in building);
            int layerOrd = LayerOrdinal(anchor.z);

            if (!_isAuthoritative)
            {
                _logger.Info?.Log(
                    $"[AnyLayerTrash:trio:{_side}] REM id={building.Definition.Id.Name} " +
                    $"anchor={anchor} layerOrd={layerOrd}");
                return;
            }

            string classification;
            if (_inFlightRemovals.Remove(anchor))
            {
                classification = "INFLIGHT-REM";
            }
            else
            {
                classification = "TRACK-REM";
            }

            if (_knownTrioLayers.TryGetValue((anchor.x, anchor.y), out var occupiedLayers))
            {
                occupiedLayers.Remove(layerOrd);
                if (occupiedLayers.Count == 0)
                {
                    _knownTrioLayers.Remove((anchor.x, anchor.y));
                }
            }

            int remaining = occupiedLayers?.Count ?? 0;
            string occupiedStr = occupiedLayers == null ? "{}" : FormatSet(occupiedLayers);

            _logger.Info?.Log(
                $"[AnyLayerTrash:trio:{_side}] {classification} id={building.Definition.Id.Name} " +
                $"anchor={anchor} layerOrd={layerOrd} remaining={remaining} occupied={occupiedStr}");
        }

        private HashSet<int> TouchColumn(GlobalTileCoordinate anchor)
        {
            if (!_knownTrioLayers.TryGetValue((anchor.x, anchor.y), out var set))
            {
                set = new HashSet<int>();
                _knownTrioLayers[(anchor.x, anchor.y)] = set;
            }
            return set;
        }

        private static int LayerOrdinal(int z) => z / LayerStep;

        private static IEnumerable<int> PendingLayers(HashSet<int> occupied)
        {
            for (int i = 0; i < VanillaLayerCount; i++)
            {
                if (!occupied.Contains(i)) yield return i;
            }
        }

        private static string FormatSet(IEnumerable<int> set)
        {
            var sorted = new List<int>(set);
            sorted.Sort();
            return "{" + string.Join(",", sorted) + "}";
        }

        private static string FormatList(List<int> list) => "{" + string.Join(",", list) + "}";

        private static GlobalTileCoordinate GetAnchor(in BuildingInstance building)
        {
            // CustomData path mirrors what the rest of the codebase does for
            // post-obsolescence connector-data access (TrashHijackRewirer +
            // TrashSystemAnchorExpander both use this pattern).
            IBuildingConnectorData connectorData = building.Definition.CustomData.Get<IBuildingConnectorData>();
            return connectorData.Tiles[0].ToGlobal(in building.Transform);
        }

        private bool IsVanillaTrash(in BuildingInstance building)
        {
            // Until TrashTrioRewirer.ModifyGameBuildings runs, group id is null —
            // skip silently. After it runs, match by group membership.
            if (!_state.TrashGroupCaptured) return false;
            return _state.VanillaTrashVariantIds.Contains(building.Definition.Id);
        }
    }
}
