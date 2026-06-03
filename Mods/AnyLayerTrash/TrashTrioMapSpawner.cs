using System.Collections.Generic;
using Game.Core.Coordinates;
using ShapezShifter.Hijack;
using ShapezShifter.Kit;
using ILogger = Core.Logging.ILogger;

namespace AnyLayerTrash
{
    /// <summary>
    /// MAP-MODEL ghost-spawn driver (PLAN-P01-007). Spawns/removes sibling
    /// trashes in the authoritative <c>IMapModel</c> so they render, collide and
    /// save — and the simulator observes them downstream (belts feed every layer).
    ///
    /// <para><b>Why mutation is INLINE in the map events (not deferred to a tick):</b>
    /// the map's <c>OnBuildingAdded</c>/<c>OnBeforeBuildingRemoved</c> fire during
    /// the player's placement/deletion action — a simulation-SAFE point. Doing the
    /// <c>CreateBuilding</c>/<c>DeleteBuilding</c> right there piggybacks on the
    /// same map-edit context the player's own op uses, so teardown finalizes the
    /// same way a native delete does. An earlier revision deferred the work to
    /// <see cref="ITickRewirer.Tick"/>, but Tick runs WHILE the SimulationGraph is
    /// updating — any bunch-edit finalize there throws
    /// "SimulationGraph is currently updating", leaving deletes half-applied (the
    /// trash receiver lingered and kept consuming). Tick is now used ONLY to
    /// acquire/subscribe to the current map.</para>
    ///
    /// <para><b>Coordinates:</b> island-relative <c>Transform_I.Position.z</c> is
    /// the LAYER INDEX (0,1,2). Siblings are built island-relative (same x,y, z =
    /// target layer) and converted with <c>ToGlobal(island)</c> — the island owns
    /// its layer→global-z mapping (a hardcoded ×20 was wrong: "No island found").</para>
    /// </summary>
    internal sealed class TrashTrioMapSpawner : ITickRewirer
    {
        private const int LayerCount = 3; // R3: vanilla cap, layers {0,1,2}.

        private readonly TrashTrioState _state;
        private readonly ILogger _logger;

        private IMapModel? _map;

        // Per tile-column (island + island-relative x,y) → layerIndex → trash there.
        private readonly Dictionary<ColumnKey, Dictionary<int, BuildingId>> _columns = new();
        // Recursion guards: our own create/delete must not re-trigger fill/cascade.
        private readonly HashSet<(ColumnKey col, int layer)> _inFlightCreates = new();
        private readonly HashSet<BuildingId> _inFlightDeletes = new();

        private readonly struct ColumnKey : System.IEquatable<ColumnKey>
        {
            public readonly IslandId Island;
            public readonly int X;
            public readonly int Y;
            public ColumnKey(IslandId island, int x, int y) { Island = island; X = x; Y = y; }
            public bool Equals(ColumnKey o) => Island.Equals(o.Island) && X == o.X && Y == o.Y;
            public override bool Equals(object? o) => o is ColumnKey k && Equals(k);
            public override int GetHashCode() => (Island.GetHashCode() * 397 ^ X) * 397 ^ Y;
        }

        public TrashTrioMapSpawner(TrashTrioState state, ILogger logger)
        {
            _state = state;
            _logger = logger;
        }

        // Tick is ONLY for map acquisition/subscription — never mutates the map
        // (the SimulationGraph is updating here; mutating/finalizing would throw).
        public void Tick(float deltaTime)
        {
            IMapModel? current = TryGetCurrentMap();
            if (ReferenceEquals(current, _map)) return;

            if (_map != null)
            {
                _map.OnBuildingAdded.Unregister(OnBuildingAdded);
                _map.OnBeforeBuildingRemoved.Unregister(OnBuildingRemoved);
                _logger.Info?.Log("[AnyLayerTrash:map] dropped subscription (CurrentMap changed/unloaded).");
            }

            _map = current;
            _columns.Clear();
            _inFlightCreates.Clear();
            _inFlightDeletes.Clear();

            if (_map != null)
            {
                _map.OnBuildingAdded.Register(OnBuildingAdded);
                _map.OnBeforeBuildingRemoved.Register(OnBuildingRemoved);
                _logger.Info?.Log("[AnyLayerTrash:map] (re)subscribed to CurrentMap building add/remove events.");
            }
        }

        private static IMapModel? TryGetCurrentMap()
        {
#pragma warning disable CS0618 // IGameSessionManagers is [Obsolete] — a tick rewirer has no DI seam.
            return GameHelper.Core?.LocalPlayer?.CurrentMap;
#pragma warning restore CS0618
        }

        private void OnBuildingAdded(BuildingModel building)
        {
            if (_map == null || !IsVanillaTrash(building)) return;

            IslandTileCoordinate posI = building.Transform_I.Position;
            int layer = posI.z;
            var col = new ColumnKey(building.Island.Id, posI.x, posI.y);

            Dictionary<int, BuildingId> layers = Column(col);
            layers[layer] = building.Id;

            if (_inFlightCreates.Remove((col, layer)))
            {
                _logger.Info?.Log($"[AnyLayerTrash:map] INFLIGHT-ADD layer={layer} islandRel=({posI.x},{posI.y},{posI.z})");
                return; // our own spawn — do not fill again
            }

            _logger.Info?.Log($"[AnyLayerTrash:map] TRACK-ADD layer={layer} islandRel=({posI.x},{posI.y},{posI.z})");
            if (layers.Count >= LayerCount) return; // full trio already (e.g. save-load of a complete column)

            // Fill missing layers inline. CreateBuilding re-enters OnBuildingAdded
            // for each sibling; the in-flight guard classifies those as INFLIGHT
            // and updates the column, so by the time we reach the next layer the
            // freshly-added one is already present.
            for (int targetLayer = 0; targetLayer < LayerCount; targetLayer++)
            {
                if (layers.ContainsKey(targetLayer)) continue;

                var coordI = new IslandTileCoordinate(posI.x, posI.y, (short)targetLayer);
                var transformI = new IslandTileTransform(coordI, building.Transform_I.Rotation);
                GlobalTileTransform transform = transformI.ToGlobal(building.Island);

                _inFlightCreates.Add((col, targetLayer));
                try
                {
                    BuildingModel sib = _map.CreateBuilding(building.Definition, in transform, building.Configuration);
                    _logger.Info?.Log(
                        $"[AnyLayerTrash:map] spawned sibling layer={targetLayer} islandRel=({coordI.x},{coordI.y},{coordI.z}) " +
                        $"global=({transform.Position.x},{transform.Position.y},{transform.Position.z}) id={sib.Id}");
                }
                catch (System.Exception ex)
                {
                    _inFlightCreates.Remove((col, targetLayer));
                    _logger.Exception?.LogException(ex);
                    _logger.Warning?.Log(
                        $"[AnyLayerTrash:map] CreateBuilding failed at islandRel=({coordI.x},{coordI.y},{coordI.z}) " +
                        $"layer={targetLayer} — {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private void OnBuildingRemoved(BuildingModel building)
        {
            if (_map == null || !IsVanillaTrash(building)) return;

            IslandTileCoordinate posI = building.Transform_I.Position;
            int layer = posI.z;
            var col = new ColumnKey(building.Island.Id, posI.x, posI.y);

            _columns.TryGetValue(col, out var layers);
            layers?.Remove(layer);

            if (_inFlightDeletes.Remove(building.Id))
            {
                _logger.Info?.Log($"[AnyLayerTrash:map] INFLIGHT-REM layer={layer}");
                if (layers != null && layers.Count == 0) _columns.Remove(col);
                return; // our own cascade — do not cascade again
            }

            _logger.Info?.Log($"[AnyLayerTrash:map] TRACK-REM layer={layer}");

            // Cascade-delete the remaining trio members inline. Snapshot ids first:
            // each DeleteBuilding re-enters OnBuildingRemoved (classified INFLIGHT)
            // and mutates the column.
            if (layers != null && layers.Count > 0)
            {
                var siblingIds = new List<BuildingId>(layers.Values);
                layers.Clear();
                foreach (BuildingId id in siblingIds)
                {
                    _inFlightDeletes.Add(id);
                    try
                    {
                        _map.DeleteBuilding(in id);
                        _logger.Info?.Log($"[AnyLayerTrash:map] cascade-deleted sibling id={id}");
                    }
                    catch (System.Exception ex)
                    {
                        _inFlightDeletes.Remove(id);
                        _logger.Exception?.LogException(ex);
                        _logger.Warning?.Log(
                            $"[AnyLayerTrash:map] DeleteBuilding failed id={id} — {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            if (layers != null && layers.Count == 0) _columns.Remove(col);
        }

        private Dictionary<int, BuildingId> Column(ColumnKey col)
        {
            if (!_columns.TryGetValue(col, out var layers))
            {
                layers = new Dictionary<int, BuildingId>();
                _columns[col] = layers;
            }
            return layers;
        }

        private bool IsVanillaTrash(BuildingModel building)
        {
            if (!_state.TrashGroupCaptured) return false;
            return _state.VanillaTrashVariantIds.Contains(building.Definition.Id);
        }

        public bool Equals(IRewirer other) => ReferenceEquals(this, other);
    }
}
