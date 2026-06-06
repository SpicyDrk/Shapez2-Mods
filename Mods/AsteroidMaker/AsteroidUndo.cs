using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core.Coordinates;
using MonoMod.RuntimeDetour;
using ShapezShifter.Hijack;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace AsteroidMaker
{
    /// <summary>
    /// PLAN-P03-001 Task 3 (SC-09) — a session-only undo/redo stack for custom-asteroid
    /// place/delete operations.
    ///
    /// <para><b>Why mod-managed, not engine undo.</b> The engine's undo system
    /// (<c>PlayerActionManager</c> / <c>ActionModifyBuildings</c>, see
    /// <c>.oes/LESSONS-shapez2-engine-hooking.md</c>) operates on buildings/islands; there is NO
    /// player-action for resource-map sources, and raw map mutation outside an action is invisible
    /// to it. So we keep our own stack and reverse ops with the same in-place add/remove +
    /// registry sync the place/delete paths use.</para>
    ///
    /// <para>An op is a <see cref="PlacedAsteroidRecord"/> tagged Place or Delete. Undo reverses it
    /// (Place → remove from map + registry; Delete → re-add); redo re-applies. A new place/delete
    /// clears the redo stack (standard semantics). The stack is NOT persisted — after a reload it's
    /// empty, so Ctrl+Z does nothing of ours and vanilla undo behaves normally (per the user's
    /// chosen design). <see cref="AsteroidUndoController"/> only invokes us when the relevant
    /// stack is non-empty, so an empty stack never intercepts the vanilla keypress.</para>
    /// </summary>
    internal sealed class AsteroidUndo
    {
        private const int MaxDepth = 64; // bounded — guards against unbounded session growth

        private enum OpKind { Place, Delete }

        private sealed class Op
        {
            public OpKind Kind;
            public PlacedAsteroidRecord Record = null!;
        }

        private readonly LinkedList<Op> _undo = new LinkedList<Op>();
        private readonly Stack<Op> _redo = new Stack<Op>();
        private readonly AsteroidUiState _ui;
        private readonly ILogger _logger;

        public AsteroidUndo(AsteroidUiState ui, ILogger logger)
        {
            _ui = ui;
            _logger = logger;
        }

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        /// <summary>Record a just-completed placement (clears the redo stack).</summary>
        public void RecordPlace(PlacedAsteroidRecord rec) => Push(OpKind.Place, rec);

        /// <summary>Record a just-completed delete (clears the redo stack).</summary>
        public void RecordDelete(PlacedAsteroidRecord rec) => Push(OpKind.Delete, rec);

        private void Push(OpKind kind, PlacedAsteroidRecord rec)
        {
            if (rec == null) return;
            _undo.AddLast(new Op { Kind = kind, Record = rec });
            while (_undo.Count > MaxDepth) _undo.RemoveFirst(); // drop oldest
            _redo.Clear();
        }

        /// <summary>Reverse the most recent op. No-op (returns false) when the undo stack is empty.</summary>
        public bool Undo()
        {
            if (_undo.Count == 0) return false;
            if (_ui.ResourcesMap is not GameResourcesMap grm) return false;

            Op op = _undo.Last!.Value;
            _undo.RemoveLast();

            // Undo of a Place removes the asteroid; undo of a Delete restores it.
            bool ok = op.Kind == OpKind.Place ? ApplyRemove(grm, op.Record) : ApplyAdd(grm, op.Record);
            if (ok)
            {
                _redo.Push(op);
                _logger.Info?.Log(
                    $"[AsteroidMaker:undo] undid {op.Kind} of '{op.Record.Code}' at " +
                    $"({op.Record.X},{op.Record.Y},{op.Record.Z}). undo={_undo.Count} redo={_redo.Count}.");
            }
            else
            {
                _undo.AddLast(op); // restore on failure so the stack stays consistent
                _logger.Warning?.Log($"[AsteroidMaker:undo] could not undo {op.Kind} of '{op.Record.Code}'.");
            }
            return ok;
        }

        /// <summary>Re-apply the most recently undone op. No-op (returns false) when the redo stack is empty.</summary>
        public bool Redo()
        {
            if (_redo.Count == 0) return false;
            if (_ui.ResourcesMap is not GameResourcesMap grm) return false;

            Op op = _redo.Pop();

            // Redo of a Place re-adds the asteroid; redo of a Delete removes it again.
            bool ok = op.Kind == OpKind.Place ? ApplyAdd(grm, op.Record) : ApplyRemove(grm, op.Record);
            if (ok)
            {
                _undo.AddLast(op);
                _logger.Info?.Log(
                    $"[AsteroidMaker:undo] redid {op.Kind} of '{op.Record.Code}' at " +
                    $"({op.Record.X},{op.Record.Y},{op.Record.Z}). undo={_undo.Count} redo={_redo.Count}.");
            }
            else
            {
                _redo.Push(op);
                _logger.Warning?.Log($"[AsteroidMaker:undo] could not redo {op.Kind} of '{op.Record.Code}'.");
            }
            return ok;
        }

        // Add the asteroid back to the live map + registry (reverse of a delete / redo of a place).
        private bool ApplyAdd(GameResourcesMap grm, PlacedAsteroidRecord rec)
        {
            var origin = new GlobalChunkCoordinate(rec.X, rec.Y, (short)rec.Z);

            if (!CanonicalShapeResolver.TryResolve(rec.Code, out ShapeDefinition shape, out string diag))
            {
                _logger.Warning?.Log($"[AsteroidMaker:undo] cannot re-add — '{rec.Code}' no longer resolves ({diag}).");
                return false;
            }

            var offsets = new List<ChunkVector>(rec.Tiles.Count);
            foreach (TileOffset t in rec.Tiles) offsets.Add(new ChunkVector(t.X, t.Y, 0));
            if (offsets.Count == 0) offsets.Add(new ChunkVector(0, 0, 0));

            if (!AsteroidPlacer.TryAddSource(grm, shape, origin, offsets, _logger, out string addDiag, out _))
            {
                _logger.Warning?.Log($"[AsteroidMaker:undo] re-add at {origin} failed ({addDiag}).");
                return false;
            }

            _ui.Persistence?.ReAddRecord(rec);
            return true;
        }

        // Remove the asteroid from the live map + registry (reverse of a place / redo of a delete).
        private bool ApplyRemove(GameResourcesMap grm, PlacedAsteroidRecord rec)
        {
            var origin = new GlobalChunkCoordinate(rec.X, rec.Y, (short)rec.Z);
            AsteroidPlacer.TryRemoveAt(grm, origin, _logger, out string diag);
            _ui.Persistence?.RemoveRecordExact(rec);
            _logger.Info?.Log($"[AsteroidMaker:undo] removed source at {origin} ({diag}).");
            return true; // registry is now consistent regardless of whether a live source existed
        }
    }

    /// <summary>
    /// PLAN-P03-001 Task 3 (revised) — routes the engine's undo/redo INPUT to
    /// <see cref="AsteroidUndo"/> without double-firing.
    ///
    /// <para><b>Why a hook, not input polling.</b> The first version polled <c>Input.GetKeyDown(Z)</c> in a
    /// tick and ran our undo — but it could NOT stop the engine from also handling the same Ctrl+Z, so one
    /// press undid our asteroid AND a vanilla action (e.g. a miner) at once. The undo/redo input has a single
    /// funnel: <c>SystemButtonsModel.TryUndo/TryRedo</c> → <c>PlayerActionManager.CanUndo()/CanRedo()</c>
    /// gate → <c>ScheduleUndo()/ScheduleRedo()</c>. We hook those four methods so a single press performs
    /// exactly ONE undo/redo.</para>
    ///
    /// <para><b>Ordering: vanilla first, ours as the fallback.</b> While the engine has anything on its own
    /// undo stack, Ctrl+Z reverses that (recently-built platforms / miners). Only once the engine's stack is
    /// empty does Ctrl+Z fall through to our custom-asteroid stack. This matches the usual flow — asteroids
    /// placed first, structures built over them after — so you undo the newer structures before the older
    /// asteroids. The Can-gates are widened to <c>vanilla || ours</c> so the input is still accepted when
    /// only our stack has something to reverse (otherwise <c>SystemButtonsModel</c> would reject the press).</para>
    /// </summary>
    internal sealed class AsteroidUndoHook : IDisposable
    {
        private readonly AsteroidUndo _undo;
        private readonly ILogger _logger;
        private readonly Hook _canUndoHook;
        private readonly Hook _canRedoHook;
        private readonly Hook _scheduleUndoHook;
        private readonly Hook _scheduleRedoHook;

        public AsteroidUndoHook(AsteroidUndo undo, ILogger logger)
        {
            _undo = undo;
            _logger = logger;

            _canUndoHook = HookBool("CanUndo", CanUndoDetour);
            _canRedoHook = HookBool("CanRedo", CanRedoDetour);
            _scheduleUndoHook = HookVoid("ScheduleUndo", ScheduleUndoDetour);
            _scheduleRedoHook = HookVoid("ScheduleRedo", ScheduleRedoDetour);

            logger.Info?.Log(
                "[AsteroidMaker:undo] undo/redo router installed (single Ctrl+Z = one reversal; vanilla " +
                "first, custom asteroids as the fallback once the engine stack is empty).");
        }

        public void Dispose()
        {
            _canUndoHook.Dispose();
            _canRedoHook.Dispose();
            _scheduleUndoHook.Dispose();
            _scheduleRedoHook.Dispose();
        }

        private static Hook HookBool(string name, CanDelegate detour) => new Hook(Resolve(name), detour);
        private static Hook HookVoid(string name, SchedDelegate detour) => new Hook(Resolve(name), detour);

        private static MethodInfo Resolve(string name) =>
            typeof(PlayerActionManager).GetMethod(
                name, BindingFlags.Instance | BindingFlags.Public, binder: null, Type.EmptyTypes, modifiers: null)
            ?? throw new InvalidOperationException($"AsteroidMaker: PlayerActionManager.{name}() not found.");

        private delegate bool CanDelegate(Func<PlayerActionManager, bool> orig, PlayerActionManager self);
        private delegate void SchedDelegate(Action<PlayerActionManager> orig, PlayerActionManager self);

        // Accept the undo/redo input when EITHER the engine or our stack has something to reverse.
        private bool CanUndoDetour(Func<PlayerActionManager, bool> orig, PlayerActionManager self)
            => orig(self) || _undo.CanUndo;

        private bool CanRedoDetour(Func<PlayerActionManager, bool> orig, PlayerActionManager self)
            => orig(self) || _undo.CanRedo;

        // Vanilla first: if the engine has an action to reverse, let it; otherwise reverse one of ours.
        private void ScheduleUndoDetour(Action<PlayerActionManager> orig, PlayerActionManager self)
        {
            if (self.HasActionsOnUndoStack) { orig(self); return; }
            if (TryOurs(_undo.Undo, "undo")) return;
            orig(self);
        }

        private void ScheduleRedoDetour(Action<PlayerActionManager> orig, PlayerActionManager self)
        {
            if (self.HasActionsOnRedoStack) { orig(self); return; }
            if (TryOurs(_undo.Redo, "redo")) return;
            orig(self);
        }

        private bool TryOurs(Func<bool> op, string tag)
        {
            try
            {
                return op();
            }
            catch (Exception ex)
            {
                _logger.Error?.Log($"[AsteroidMaker:undo] custom {tag} threw (non-fatal): {ex}");
                return true; // treat as handled — don't fall through to a vanilla action
            }
        }
    }
}
