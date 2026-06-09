using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core.Coordinates;
using MonoMod.RuntimeDetour;
using ILogger = Core.Logging.ILogger;

namespace AnyLayerTrash
{
    /// <summary>
    /// Expands the player's trash build action so a trash fills every layer of the
    /// tile (and deleting one removes the whole column), by rewriting
    /// <c>ActionModifyBuildings.Data</c> at commit time via a MonoMod detour on
    /// <c>PlayerAction.TryExecute_INTERNAL</c> — keeping the trio one undoable
    /// transaction. A second detour on <c>ActionModifyBuildings.IsPossible</c>
    /// opens the undo/redo input gate for the force-placed trio.
    /// Design rationale, hook-point choice, and dead-end attempts: see CODE-NOTES.md.
    /// </summary>
    internal sealed class TrashActionInterceptor : IDisposable
    {
        private const int LayerCount = 3; // trio lives on layers {0,1,2}

        private readonly TrashTrioState _state;
        private readonly ILogger _logger;
        private Hook? _hookTryExecute;
        private Hook? _hookIsPossible;

        // MonoMod detours must be static; one back-reference to the live instance.
        private static TrashActionInterceptor? _active;

        public TrashActionInterceptor(TrashTrioState state, ILogger logger)
        {
            _state = state;
            _logger = logger;
        }

        // Trampoline signature for PlayerAction.TryExecute_INTERNAL (self first).
        private delegate bool TryExecuteOrig(
            PlayerAction self, out IPlayerAction reverseAction, IInteractionMode interactionMode, bool skipChecks_INTERNAL);

        public void Install()
        {
            _active = this;

            MethodInfo? tryExecute = typeof(PlayerAction).GetMethod(
                nameof(PlayerAction.TryExecute_INTERNAL), BindingFlags.Instance | BindingFlags.Public);
            if (tryExecute == null)
            {
                _logger.Warning?.Log("[AnyLayerTrash:action] PlayerAction.TryExecute_INTERNAL not found — interceptor NOT installed.");
                return;
            }
            MethodInfo tryExecuteDetour = typeof(TrashActionInterceptor).GetMethod(
                nameof(TryExecuteDetour), BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("TryExecuteDetour not found.");
            _hookTryExecute = new Hook(tryExecute, tryExecuteDetour);

            // Patch IsPossible to open the undo/redo input gate for the force-placed
            // trio (CanUndo/CanRedo call it directly, outside TryExecute_INTERNAL).
            // Return-value only; never mutates Data. See CODE-NOTES.md.
            MethodInfo? isPossible = typeof(ActionModifyBuildings).GetMethod(
                nameof(ActionModifyBuildings.IsPossible), BindingFlags.Instance | BindingFlags.Public);
            if (isPossible != null)
            {
                MethodInfo isPossibleDetour = typeof(TrashActionInterceptor).GetMethod(
                    nameof(IsPossibleDetour), BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("IsPossibleDetour not found.");
                _hookIsPossible = new Hook(isPossible, isPossibleDetour);
            }
            else
            {
                _logger.Warning?.Log("[AnyLayerTrash:action] ActionModifyBuildings.IsPossible not found — redo gate NOT patched.");
            }

            _logger.Info?.Log("[AnyLayerTrash:action] interceptor installed — TryExecute_INTERNAL + IsPossible (undo/redo gate).");
        }

        // Trash action: validate the player's real building, expand Data to the
        // full trio, then run orig with checks skipped so injected siblings aren't
        // re-validated. Non-trash actions pass straight through. See CODE-NOTES.md.
        private static bool TryExecuteDetour(
            TryExecuteOrig orig,
            PlayerAction self,
            out IPlayerAction reverseAction,
            IInteractionMode interactionMode,
            bool skipChecks_INTERNAL)
        {
            if (self is ActionModifyBuildings action && _active != null && _active.InvolvesTrash(action))
            {
                // Validate ONLY a player-initiated action. Skip when already-checked
                // (skipChecks_INTERNAL) or all-forced (a reverse/redo trio the engine
                // replays — re-validating its force-placed siblings would fail).
                // See CODE-NOTES.md. (null! silences CS8625 on the false path.)
                if (!skipChecks_INTERNAL && !IsAllForced(action.Data) && !action.IsPossible(interactionMode))
                {
                    reverseAction = null!;
                    return false;
                }

                _active.Expand(action);
                return orig(self, out reverseAction, interactionMode, skipChecks_INTERNAL: true);
            }

            return orig(self, out reverseAction, interactionMode, skipChecks_INTERNAL);
        }

        // Force TRUE only for an all-forced trash action (a reverse/redo trio) so
        // the CanUndo/CanRedo gate accepts it. Cheap checks first. See CODE-NOTES.md.
        private static bool IsPossibleDetour(
            Func<ActionModifyBuildings, IInteractionMode, bool> orig,
            ActionModifyBuildings self,
            IInteractionMode interactionMode)
        {
            bool result = orig(self, interactionMode);
            if (!result && _active != null && IsAllForced(self.Data) && _active.InvolvesTrash(self))
            {
                return true;
            }
            return result;
        }

        private void Expand(ActionModifyBuildings action)
        {
            ModifyBuildingsPayload data = action.Data;
            IReadOnlyList<PlaceBuildingPayload> places = data.Place;
            IReadOnlyList<DeleteBuildingPayload> deletes = data.Delete;

            // Place expansion: add siblings on the other layers; dedup by tile.
            List<PlaceBuildingPayload>? expandedPlace = null;
            var placeTiles = new HashSet<(IslandId, int, int, int)>();
            foreach (PlaceBuildingPayload p in places)
            {
                IslandTileCoordinate pos = p.Transform_I.Position;
                placeTiles.Add((p.IslandId, pos.x, pos.y, pos.z));
            }
            IMapModel? placeMap = action.Map;
            foreach (PlaceBuildingPayload p in places)
            {
                if (!IsTrashDef(p.Definition)) continue;
                IslandTileCoordinate pos = p.Transform_I.Position;
                for (int layer = 0; layer < LayerCount; layer++)
                {
                    if (layer == pos.z) continue;
                    if (!placeTiles.Add((p.IslandId, pos.x, pos.y, layer))) continue; // tile already in the action
                    var coordI = new IslandTileCoordinate(pos.x, pos.y, (short)layer);
                    // Skip layers already occupied (force-placing trash on top throws
                    // MapCannotCreateBuildingException). See CODE-NOTES.md.
                    if (placeMap != null
                        && placeMap.TryGetIsland(p.IslandId, out var occIsland)
                        && occIsland.TryGetBuilding(in coordI, out _))
                    {
                        continue;
                    }
                    var transformI = new IslandTileTransform(coordI, p.Transform_I.Rotation);
                    expandedPlace ??= new List<PlaceBuildingPayload>(places);
                    expandedPlace.Add(new PlaceBuildingPayload(
                        p.IslandId, p.Definition, p.Configuration, in transformI, forceAllowPlace: true));
                }
            }

            // Delete expansion: pull in the other layers' members (looked up live
            // via the island); dedup by BuildingId (never re-list an id).
            List<DeleteBuildingPayload>? expandedDelete = null;
            IMapModel? map = action.Map;
            if (map != null)
            {
                var deleteIds = new HashSet<BuildingId>();
                foreach (DeleteBuildingPayload d in deletes) deleteIds.Add(d.BuildingId);
                foreach (DeleteBuildingPayload d in deletes)
                {
                    if (!map.TryGetBuilding(in d.BuildingId, out BuildingModel b) || !IsTrashDef(b.Definition)) continue;
                    IslandTileCoordinate pos = b.Transform_I.Position;
                    for (int layer = 0; layer < LayerCount; layer++)
                    {
                        if (layer == pos.z) continue;
                        var coordI = new IslandTileCoordinate(pos.x, pos.y, (short)layer);
                        if (!b.Island.TryGetBuilding(in coordI, out BuildingModel sib) || !IsTrashDef(sib.Definition)) continue;
                        if (!deleteIds.Add(sib.Id)) continue; // already slated for deletion
                        expandedDelete ??= new List<DeleteBuildingPayload>(deletes);
                        expandedDelete.Add(new DeleteBuildingPayload(sib.Id, forceAllowDelete: true));
                    }
                }
            }

            if (expandedPlace == null && expandedDelete == null) return; // not a trash action

            IReadOnlyList<PlaceBuildingPayload> newPlace = expandedPlace ?? places;
            IReadOnlyList<DeleteBuildingPayload> newDelete = expandedDelete ?? deletes;
            action.Data = new ModifyBuildingsPayload(newPlace, newDelete, data.BlueprintCurrencyModification);
            _logger.Info?.Log(
                $"[AnyLayerTrash:action] expanded trio at execute: place {places.Count}->{newPlace.Count}, " +
                $"delete {deletes.Count}->{newDelete.Count}.");
        }

        // Cheap pre-scan: does this action place or delete any vanilla trash?
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

        // All entries force-flagged ⇒ engine-replayed (reverse/redo); a
        // player-initiated action carries ≥1 non-forced entry. See CODE-NOTES.md.
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
            _hookIsPossible?.Dispose();
            _hookIsPossible = null;
            _hookTryExecute?.Dispose();
            _hookTryExecute = null;
            if (ReferenceEquals(_active, this)) _active = null;
        }
    }
}
