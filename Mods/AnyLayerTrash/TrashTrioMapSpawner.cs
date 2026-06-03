using System.Collections.Generic;
using Game.Core.Coordinates;
using ShapezShifter.Hijack;
using ShapezShifter.Kit;
using ILogger = Core.Logging.ILogger;

namespace AnyLayerTrash
{
    /// <summary>
    /// SUPERSEDED 2026-06-03 by <see cref="TrashActionInterceptor"/> (PLAN-P03-001)
    /// and NO LONGER REGISTERED. It mutated the map outside any <c>PlayerAction</c>,
    /// so trio create/delete were invisible to undo/redo and raced the engine's
    /// batch-delete loop. Kept as a reference catalogue (map-event subscription +
    /// island-relative spawn/cascade patterns). Do not re-register alongside the
    /// action interceptor — that would double the trio.
    ///
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

                // Occupancy guard (save/reload robustness). A sibling may already
                // exist on this layer — most importantly one deserialized from a
                // save when a trio is reloaded, but also a manual trash. Never
                // CreateBuilding over an occupied tile: register the existing
                // trash and skip. The save loader itself does the same
                // (IslandLayoutSerializer skips tiles where TryGetBuilding hits),
                // so whichever trio member deserializes first triggers our fill,
                // and the rest are detected here / skipped by the loader — the
                // column converges to exactly three trashes regardless of load
                // order, with no duplicate stack and no "already another building".
                if (building.Island.TryGetBuilding(in coordI, out BuildingModel existing))
                {
                    if (IsVanillaTrash(existing))
                    {
                        layers[targetLayer] = existing.Id;
                        _logger.Info?.Log(
                            $"[AnyLayerTrash:map] layer={targetLayer} already occupied by trash id={existing.Id} — registered, skipped spawn");
                    }
                    continue;
                }

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

            // Cascade-delete the remaining trio members inline. Discover siblings
            // by querying the ISLAND at the other layer indices rather than
            // trusting the in-memory registry: a trio LOADED FROM A SAVE has no
            // registry entry (it's cleared on every CurrentMap change), so a
            // registry-only cascade would orphan the siblings after a reload.
            // The island is the authoritative source. Each DeleteBuilding
            // re-enters OnBuildingRemoved (classified INFLIGHT via the guard) and
            // clears its own column entry, so we don't double-delete or recurse.
            // Legacy single trashes (pre-mod saves) have no siblings — the
            // lookups simply miss and nothing cascades, which is correct.
            layers?.Clear();
            for (int otherLayer = 0; otherLayer < LayerCount; otherLayer++)
            {
                if (otherLayer == layer) continue;

                var coordI = new IslandTileCoordinate(posI.x, posI.y, (short)otherLayer);
                if (!building.Island.TryGetBuilding(in coordI, out BuildingModel sib)) continue;
                if (!IsVanillaTrash(sib) || _inFlightDeletes.Contains(sib.Id)) continue;

                _inFlightDeletes.Add(sib.Id);
                try
                {
                    _map.DeleteBuilding(in sib.Id);
                    _logger.Info?.Log($"[AnyLayerTrash:map] cascade-deleted sibling layer={otherLayer} id={sib.Id}");
                }
                catch (System.Exception ex)
                {
                    _inFlightDeletes.Remove(sib.Id);
                    _logger.Exception?.LogException(ex);
                    _logger.Warning?.Log(
                        $"[AnyLayerTrash:map] DeleteBuilding failed layer={otherLayer} — {ex.GetType().Name}: {ex.Message}");
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
