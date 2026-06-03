using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core.Coordinates;
using MonoMod.RuntimeDetour;
using ILogger = Core.Logging.ILogger;

namespace AnyLayerTrash
{
    /// <summary>
    /// PLAN-P03-001 — routes the ghost-spawn trio through the engine's
    /// <c>Undoable</c> player-action system by expanding
    /// <c>ActionModifyBuildings.Data</c>, so undo/redo and batch/platform deletes
    /// treat the trio as ONE transaction. Replaces the event-driven
    /// <see cref="TrashTrioMapSpawner"/>, which mutated the map outside any action
    /// (invisible to undo; raced the engine's batch-delete loop).
    ///
    /// <para><b>Hook point — <c>PlayerAction.TryExecute_INTERNAL</c>.</b>
    /// The detour validates the player's real building (calls <c>IsPossible</c>
    /// once on the UNEXPANDED payload), then expands <c>Data</c> to include the
    /// siblings, then calls the original with <c>skipChecks_INTERNAL: true</c> so
    /// the engine runs <c>ExecuteInternal</c> on the full trio WITHOUT
    /// re-validating the injected siblings. This seam sits exactly between
    /// validation and execution. Undo/redo and combined/platform deletes also flow
    /// through <c>TryExecute_INTERNAL</c> (<c>CombinedUndoablePlayerAction</c>
    /// calls each inner action's <c>TryExecute_INTERNAL(..., skipChecks: true)</c>),
    /// so one hook covers every commit path.</para>
    ///
    /// <para><b>Why NOT <c>ExecuteInternal</c> (second attempt):</b> detouring
    /// <c>ExecuteInternal</c> makes MonoMod force-JIT-compile it up front, and its
    /// body references <c>IBuildingModelAccessor.TryGetBuilding(BuildingId&amp;,
    /// BuildingModel&amp;)</c> which the forced compile can't resolve →
    /// <c>MissingMethodException</c> at mod load. <c>TryExecute_INTERNAL</c>'s body
    /// is trivial (no such reference), so it compiles cleanly; the real
    /// <c>ExecuteInternal</c> is still reached at runtime via normal lazy JIT,
    /// where that overload resolves fine (the game itself uses it).</para>
    ///
    /// <para><b>Why NOT <c>IsPossible</c> (first attempt):</b> the placement
    /// preview calls <c>IsPossible</c> every frame to validate the cursor ghost.
    /// Mutating <c>Data</c> there corrupted the preview action — the payload
    /// accumulated across frames (observed place 1→3→…→24→72) until overlapping
    /// tiles tripped <c>CheckPlace</c>'s "tile included twice" and <c>IsPossible</c>
    /// returned false, blocking placement entirely. Expanding only on commit
    /// (<c>TryExecute_INTERNAL</c>, never called for preview) avoids that.</para>
    ///
    /// <para>The reverse (undo) action is built inside <c>ExecuteInternal</c> from
    /// the now-expanded <c>Data</c> (CreateReversePlacement over <c>Data.Delete</c>
    /// + the placed-id list), so undo/redo restore or remove all three. Dedup
    /// (tile for places, BuildingId for deletes) makes the reverse/redo actions —
    /// which already carry the full trio — expand to a no-op, and keeps a
    /// platform delete that already lists all three from re-listing an id (which
    /// would make ExecuteInternal throw "Could not find building").</para>
    /// </summary>
    internal sealed class TrashActionInterceptor : IDisposable
    {
        private const int LayerCount = 3; // R3: trio lives on layers {0,1,2}.

        private readonly TrashTrioState _state;
        private readonly ILogger _logger;
        private Hook? _hook;

        // MonoMod detours must be static; a mod instantiates once, so a single
        // static back-reference to the live interceptor is sufficient.
        private static TrashActionInterceptor? _active;

        public TrashActionInterceptor(TrashTrioState state, ILogger logger)
        {
            _state = state;
            _logger = logger;
        }

        // Trampoline signature for PlayerAction.TryExecute_INTERNAL (instance
        // method ⇒ self first; the out param must be declared out here too).
        private delegate bool TryExecuteOrig(
            PlayerAction self, out IPlayerAction reverseAction, IInteractionMode interactionMode, bool skipChecks_INTERNAL);

        public void Install()
        {
            MethodInfo? target = typeof(PlayerAction).GetMethod(
                nameof(PlayerAction.TryExecute_INTERNAL), BindingFlags.Instance | BindingFlags.Public);
            if (target == null)
            {
                _logger.Warning?.Log("[AnyLayerTrash:action] PlayerAction.TryExecute_INTERNAL not found — interceptor NOT installed.");
                return;
            }

            MethodInfo detour = typeof(TrashActionInterceptor).GetMethod(
                nameof(TryExecuteDetour), BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("TryExecuteDetour not found.");

            _active = this;
            _hook = new Hook(target, detour);
            _logger.Info?.Log("[AnyLayerTrash:action] interceptor installed — PlayerAction.TryExecute_INTERNAL.");
        }

        // Detour-with-orig: first param is the trampoline to the original method.
        // For a trash action: validate the player's real building (player actions
        // only), expand Data to the full trio, then run the original with checks
        // skipped so the injected siblings aren't re-validated. Non-trash actions
        // pass straight through to the engine's normal validation+execution.
        private static bool TryExecuteDetour(
            TryExecuteOrig orig,
            PlayerAction self,
            out IPlayerAction reverseAction,
            IInteractionMode interactionMode,
            bool skipChecks_INTERNAL)
        {
            if (self is ActionModifyBuildings action && _active != null && _active.InvolvesTrash(action))
            {
                // Validate the player's real (unexpanded) building — but ONLY for a
                // player-INITIATED action. A reverse/redo action (place→undo→redo,
                // delete→undo) already carries the full trio, all entries
                // force-flagged. Running IsPossible on those re-validates the
                // upper-layer placements through CheckPlace, whose tile-validity
                // (line 47), occupancy (62) and notch (66) gates are NOT bypassed by
                // forceAllowPlace → IsPossible returns false → redo silently does
                // nothing (and the player's building never validates against a
                // force-placed sibling). Reverse actions are entirely force-flagged
                // and were built from a previously-valid state, so we skip the gate
                // and let the engine replay them verbatim. skipChecks_INTERNAL
                // (combined/platform inner steps) likewise means "already checked".
                if (!skipChecks_INTERNAL && !IsAllForced(action.Data) && !action.IsPossible(interactionMode))
                {
                    reverseAction = null;
                    return false;
                }

                _active.Expand(action);
                return orig(self, out reverseAction, interactionMode, skipChecks_INTERNAL: true);
            }

            return orig(self, out reverseAction, interactionMode, skipChecks_INTERNAL);
        }

        private void Expand(ActionModifyBuildings action)
        {
            ModifyBuildingsPayload data = action.Data;
            IReadOnlyList<PlaceBuildingPayload> places = data.Place;
            IReadOnlyList<DeleteBuildingPayload> deletes = data.Delete;

            // --- Place expansion: a trash placement gains siblings on the other
            //     layers. Dedup by tile so blueprint/reverse actions that already
            //     carry the full column add nothing. ---
            List<PlaceBuildingPayload>? expandedPlace = null;
            var placeTiles = new HashSet<(IslandId, int, int, int)>();
            foreach (PlaceBuildingPayload p in places)
            {
                IslandTileCoordinate pos = p.Transform_I.Position;
                placeTiles.Add((p.IslandId, pos.x, pos.y, pos.z));
            }
            foreach (PlaceBuildingPayload p in places)
            {
                if (!IsTrashDef(p.Definition)) continue;
                IslandTileCoordinate pos = p.Transform_I.Position;
                for (int layer = 0; layer < LayerCount; layer++)
                {
                    if (layer == pos.z) continue;
                    if (!placeTiles.Add((p.IslandId, pos.x, pos.y, layer))) continue; // tile already in the action
                    var coordI = new IslandTileCoordinate(pos.x, pos.y, (short)layer);
                    var transformI = new IslandTileTransform(coordI, p.Transform_I.Rotation);
                    expandedPlace ??= new List<PlaceBuildingPayload>(places);
                    expandedPlace.Add(new PlaceBuildingPayload(
                        p.IslandId, p.Definition, p.Configuration, in transformI, forceAllowPlace: true));
                }
            }

            // --- Delete expansion: deleting a trash member pulls in the other
            //     layers' members (looked up live via the island). Dedup by
            //     BuildingId so a platform/area delete that already lists all
            //     three adds nothing (and never re-lists an id). The result is
            //     then reordered so each column's layer-0 trash deletes LAST. ---
            List<DeleteBuildingPayload>? newDeleteList = null;
            IMapModel? map = action.Map;
            if (map != null && deletes.Count > 0)
            {
                var deleteIds = new HashSet<BuildingId>();
                foreach (DeleteBuildingPayload d in deletes) deleteIds.Add(d.BuildingId);

                var working = new List<DeleteBuildingPayload>(deletes);
                bool hasTrash = false;
                foreach (DeleteBuildingPayload d in deletes)
                {
                    if (!map.TryGetBuilding(in d.BuildingId, out BuildingModel b) || !IsTrashDef(b.Definition)) continue;
                    hasTrash = true;
                    IslandTileCoordinate pos = b.Transform_I.Position;
                    for (int layer = 0; layer < LayerCount; layer++)
                    {
                        if (layer == pos.z) continue;
                        var coordI = new IslandTileCoordinate(pos.x, pos.y, (short)layer);
                        if (!b.Island.TryGetBuilding(in coordI, out BuildingModel sib) || !IsTrashDef(sib.Definition)) continue;
                        if (!deleteIds.Add(sib.Id)) continue; // already slated for deletion
                        working.Add(new DeleteBuildingPayload(sib.Id, forceAllowDelete: true));
                    }
                }

                if (hasTrash)
                {
                    // Render void-tile fix: delete each column's layer-0 (z==0)
                    // trash LAST. MapPlayingfieldVoidTileTracker.UnregisterBuilding
                    // (line 57/73) removes the island's ChunkCache entry the moment
                    // its z==0 void-tile set empties; only the z==0 member of a
                    // stacked trio contributes a tile, so deleting it first removes
                    // the entry while z>0 trashes (also RenderVoidBelow) remain — and
                    // their UnregisterBuilding then throws "Entry not found in cache".
                    // That throw aborts ExecuteInternal before it builds the reverse
                    // action (line 193) → undo "works" visually but redo is dead.
                    // Deleting z>0 first keeps the entry alive until the final z==0
                    // unregister, after which no RenderVoidBelow trash remains. Even
                    // when the action already lists the full trio (platform delete,
                    // undo-of-place reverse), we reorder so the loop never throws.
                    newDeleteList = OrderTrashLayerZeroLast(map, working);
                }
            }

            if (expandedPlace == null && newDeleteList == null) return; // not a trash action

            IReadOnlyList<PlaceBuildingPayload> newPlace = expandedPlace ?? places;
            IReadOnlyList<DeleteBuildingPayload> newDelete = newDeleteList ?? deletes;
            action.Data = new ModifyBuildingsPayload(newPlace, newDelete, data.BlueprintCurrencyModification);
            _logger.Info?.Log(
                $"[AnyLayerTrash:action] expanded trio at execute: place {places.Count}->{newPlace.Count}, " +
                $"delete {deletes.Count}->{newDelete.Count}.");
        }

        // Stable-partition the delete list so layer-0 (z==0) trash members are
        // deleted after everything else — see the render void-tile note in Expand.
        private List<DeleteBuildingPayload> OrderTrashLayerZeroLast(IMapModel map, List<DeleteBuildingPayload> items)
        {
            var head = new List<DeleteBuildingPayload>(items.Count);
            List<DeleteBuildingPayload>? tail = null;
            foreach (DeleteBuildingPayload d in items)
            {
                if (map.TryGetBuilding(in d.BuildingId, out BuildingModel b)
                    && IsTrashDef(b.Definition) && b.Transform_I.Position.z == 0)
                {
                    (tail ??= new List<DeleteBuildingPayload>()).Add(d);
                }
                else
                {
                    head.Add(d);
                }
            }
            if (tail != null) head.AddRange(tail);
            return head;
        }

        // Does this action place or delete any vanilla trash? Cheap pre-scan that
        // decides whether we touch the action at all (non-trash → vanilla
        // passthrough) and whether the IsPossible gate is worth running.
        private bool InvolvesTrash(ActionModifyBuildings action)
        {
            ModifyBuildingsPayload data = action.Data;
            foreach (PlaceBuildingPayload p in data.Place)
            {
                if (IsTrashDef(p.Definition)) return true;
            }
            IMapModel? map = action.Map;
            if (map != null)
            {
                foreach (DeleteBuildingPayload d in data.Delete)
                {
                    if (map.TryGetBuilding(in d.BuildingId, out BuildingModel b) && IsTrashDef(b.Definition)) return true;
                }
            }
            return false;
        }

        // A reverse/redo action (or any engine-replayed action) has every entry
        // force-flagged. Player-initiated actions carry at least one non-forced
        // entry (the building the player actually placed/deleted).
        private static bool IsAllForced(ModifyBuildingsPayload data)
        {
            foreach (PlaceBuildingPayload p in data.Place)
            {
                if (!p.ForceAllowPlace) return false;
            }
            foreach (DeleteBuildingPayload d in data.Delete)
            {
                if (!d.ForceAllowDelete) return false;
            }
            return true;
        }

        private bool IsTrashDef(IBuildingDefinition? def)
        {
            return _state.TrashGroupCaptured && def != null && _state.VanillaTrashVariantIds.Contains(def.Id);
        }

        public void Dispose()
        {
            _hook?.Dispose();
            _hook = null;
            if (ReferenceEquals(_active, this)) _active = null;
        }
    }
}
