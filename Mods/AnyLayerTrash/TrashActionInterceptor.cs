using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core.Coordinates;
using MonoMod.RuntimeDetour;
using ILogger = Core.Logging.ILogger;

namespace AnyLayerTrash
{
    /// <summary>
    /// Coexist redesign (2026-06-06). Routes the column-stamp through the engine's
    /// <c>Undoable</c> player-action system by expanding
    /// <c>ActionModifyBuildings.Data</c>, so undo/redo and batch/platform deletes
    /// treat the stamped column as ONE transaction.
    ///
    /// <para><b>What changed from the hijack.</b> Previously this expanded EVERY
    /// vanilla trash placement into a 3-layer column (vanilla trash was no longer
    /// vanilla). Now it triggers ONLY when the player places the modded
    /// "Any Layer Trash" variant (<see cref="TrashTrioState.ModdedTrashVariantId"/>),
    /// and it SWAPS that modded placement for plain <b>vanilla</b> trash on every
    /// layer of the tile. The modded variant therefore never lands on the map — so
    /// it needs no simulation — and the original vanilla Trash building is left
    /// 100% vanilla (a normal trash placement flows straight through untouched).
    /// Deletion is plain vanilla per-building (the old column-delete expansion is
    /// gone): once stamped, the column is just ordinary vanilla trash.</para>
    ///
    /// <para><b>Hook point — <c>PlayerAction.TryExecute_INTERNAL</c>.</b> The detour
    /// validates the player's modded placement (<c>IsPossible</c> once on the
    /// UNEXPANDED payload), swaps it to the vanilla column, then calls the original
    /// with <c>skipChecks_INTERNAL: true</c> so the force-placed upper layers aren't
    /// re-validated. Undo/redo and combined/platform deletes also flow through
    /// <c>TryExecute_INTERNAL</c>, so one hook covers every commit path. (Same
    /// reasons as before for choosing this seam over <c>ExecuteInternal</c>, which
    /// MonoMod force-compiles into a <c>MissingMethodException</c>, and over
    /// <c>IsPossible</c>, whose per-frame preview calls would accumulate the payload.)</para>
    ///
    /// <para><b>Two branches.</b> (1) An action that <i>places the modded variant</i>
    /// is a player-initiated placement: validate, swap to the vanilla column, replay
    /// with checks skipped. (2) An <i>all-forced action involving vanilla trash</i>
    /// is the engine replaying our own reverse/redo column (or any forced trash
    /// step): replay verbatim with checks skipped so the force-placed upper layers
    /// don't trip <c>CheckPlace</c>. Everything else — including a normal,
    /// non-forced vanilla trash placement — passes through to the engine's normal
    /// validation, so vanilla trash keeps its vanilla checks.</para>
    /// </summary>
    internal sealed class TrashActionInterceptor : IDisposable
    {
        private const int LayerCount = 3; // stamp the column on layers {0,1,2}.

        private readonly TrashTrioState _state;
        private readonly ILogger _logger;
        private Hook? _hookTryExecute;
        private Hook? _hookIsPossible;

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

            // CanUndo/CanRedo (PlayerActionManager.cs:151/177) call IsPossible
            // DIRECTLY on the stack-top action to gate undo/redo input — outside
            // TryExecute_INTERNAL. Our reverse/redo column is force-placed on the
            // upper layers, which IsPossible→CheckPlace rejects (tile-validity/notch
            // are NOT bypassed by forceAllowPlace) → CanRedo false → the input is
            // silently dropped. This postfix forces IsPossible TRUE for an
            // all-forced vanilla-trash action so the gate passes. Return-value ONLY —
            // it never mutates Data, so the placement preview (a non-forced cursor
            // action) is untouched.
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
                _logger.Warning?.Log("[AnyLayerTrash:action] ActionModifyBuildings.IsPossible not found — undo/redo gate NOT patched.");
            }

            _logger.Info?.Log("[AnyLayerTrash:action] interceptor installed — modded-trash placement stamps a vanilla column; vanilla trash untouched.");
        }

        private static bool TryExecuteDetour(
            TryExecuteOrig orig,
            PlayerAction self,
            out IPlayerAction reverseAction,
            IInteractionMode interactionMode,
            bool skipChecks_INTERNAL)
        {
            if (self is ActionModifyBuildings action && _active != null)
            {
                // (1) Player placed the modded variant: validate the real (unexpanded)
                // modded placement, then swap it to a vanilla column and replay with
                // checks skipped so the force-placed upper layers aren't re-validated.
                if (_active.InvolvesModdedTrash(action))
                {
                    if (!skipChecks_INTERNAL && !action.IsPossible(interactionMode))
                    {
                        reverseAction = null!;
                        return false;
                    }
                    _active.StampColumn(action);
                    return orig(self, out reverseAction, interactionMode, skipChecks_INTERNAL: true);
                }

                // (2) Engine replaying our own reverse/redo column (or any forced trash
                // step): every entry is force-flagged. Replay verbatim with checks
                // skipped so the upper-layer placements don't trip CheckPlace. A
                // NORMAL (non-forced) vanilla trash placement is NOT all-forced, so it
                // falls through to the engine's normal validation below.
                if (IsAllForced(action.Data) && _active.InvolvesVanillaTrash(action))
                {
                    return orig(self, out reverseAction, interactionMode, skipChecks_INTERNAL: true);
                }
            }

            return orig(self, out reverseAction, interactionMode, skipChecks_INTERNAL);
        }

        // Postfix on ActionModifyBuildings.IsPossible — see the Install note. Force
        // TRUE only for an all-forced trash action (our reverse/redo column) so the
        // CanUndo/CanRedo gate accepts it. Return value only; never mutates Data.
        private static bool IsPossibleDetour(
            Func<ActionModifyBuildings, IInteractionMode, bool> orig,
            ActionModifyBuildings self,
            IInteractionMode interactionMode)
        {
            bool result = orig(self, interactionMode);
            if (!result && _active != null && IsAllForced(self.Data) && _active.InvolvesVanillaTrash(self))
            {
                return true;
            }
            return result;
        }

        /// <summary>
        /// Swap each modded-variant placement for plain vanilla trash on every layer
        /// of that tile. Dedup by tile so a placement that already covers a layer
        /// (or a non-modded entry at that tile) is not duplicated.
        /// </summary>
        private void StampColumn(ActionModifyBuildings action)
        {
            if (_state.VanillaTrashDefault is not { } vanillaDef) return;

            ModifyBuildingsPayload data = action.Data;
            IReadOnlyList<PlaceBuildingPayload> places = data.Place;

            var occupied = new HashSet<(IslandId, int, int, int)>();
            bool anyModded = false;
            foreach (PlaceBuildingPayload p in places)
            {
                if (IsModdedTrash(p.Definition)) { anyModded = true; continue; }
                IslandTileCoordinate pos = p.Transform_I.Position;
                occupied.Add((p.IslandId, pos.x, pos.y, pos.z));
            }
            if (!anyModded) return;

            var rebuilt = new List<PlaceBuildingPayload>(places.Count + LayerCount);
            foreach (PlaceBuildingPayload p in places)
            {
                if (!IsModdedTrash(p.Definition)) { rebuilt.Add(p); continue; }

                IslandTileCoordinate pos = p.Transform_I.Position;
                for (int layer = 0; layer < LayerCount; layer++)
                {
                    if (!occupied.Add((p.IslandId, pos.x, pos.y, layer))) continue;
                    var coordI = new IslandTileCoordinate(pos.x, pos.y, (short)layer);
                    var transformI = new IslandTileTransform(coordI, p.Transform_I.Rotation);
                    rebuilt.Add(new PlaceBuildingPayload(
                        p.IslandId, vanillaDef, p.Configuration, in transformI, forceAllowPlace: true));
                }
            }

            action.Data = new ModifyBuildingsPayload(rebuilt, data.Delete, data.BlueprintCurrencyModification);
            _logger.Info?.Log(
                $"[AnyLayerTrash:action] stamped vanilla trash column: place {places.Count}->{rebuilt.Count}.");
        }

        // Does this action place the modded variant? Cheap pre-scan (places only —
        // the modded variant is the placement trigger; deletes are plain vanilla).
        private bool InvolvesModdedTrash(ActionModifyBuildings action)
        {
            foreach (PlaceBuildingPayload p in action.Data.Place)
            {
                if (IsModdedTrash(p.Definition)) return true;
            }
            return false;
        }

        // Does this action place or delete any VANILLA trash? Used by the redo/undo
        // gate (which fires for the force-placed column the engine replays).
        private bool InvolvesVanillaTrash(ActionModifyBuildings action)
        {
            ModifyBuildingsPayload data = action.Data;
            foreach (PlaceBuildingPayload p in data.Place)
            {
                if (IsVanillaTrashDef(p.Definition)) return true;
            }
            IMapModel? map = action.Map;
            if (map != null)
            {
                foreach (DeleteBuildingPayload d in data.Delete)
                {
                    if (map.TryGetBuilding(in d.BuildingId, out BuildingModel b) && IsVanillaTrashDef(b.Definition)) return true;
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

        private bool IsModdedTrash(IBuildingDefinition? def)
        {
            return def != null
                && _state.ModdedTrashVariantId is { } moddedId
                && def.Id.Equals(moddedId);
        }

        private bool IsVanillaTrashDef(IBuildingDefinition? def)
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
