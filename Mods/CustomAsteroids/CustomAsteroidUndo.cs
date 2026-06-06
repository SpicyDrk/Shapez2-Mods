using System;
using System.Collections.Generic;
using Game.Core.Coordinates;
using ShapezShifter.Hijack;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace CustomAsteroids
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
    /// chosen design). <see cref="CustomAsteroidUndoController"/> only invokes us when the relevant
    /// stack is non-empty, so an empty stack never intercepts the vanilla keypress.</para>
    /// </summary>
    internal sealed class CustomAsteroidUndo
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
        private readonly CustomAsteroidUiState _ui;
        private readonly ILogger _logger;

        public CustomAsteroidUndo(CustomAsteroidUiState ui, ILogger logger)
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
                    $"[CustomAsteroids:undo] undid {op.Kind} of '{op.Record.Code}' at " +
                    $"({op.Record.X},{op.Record.Y},{op.Record.Z}). undo={_undo.Count} redo={_redo.Count}.");
            }
            else
            {
                _undo.AddLast(op); // restore on failure so the stack stays consistent
                _logger.Warning?.Log($"[CustomAsteroids:undo] could not undo {op.Kind} of '{op.Record.Code}'.");
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
                    $"[CustomAsteroids:undo] redid {op.Kind} of '{op.Record.Code}' at " +
                    $"({op.Record.X},{op.Record.Y},{op.Record.Z}). undo={_undo.Count} redo={_redo.Count}.");
            }
            else
            {
                _redo.Push(op);
                _logger.Warning?.Log($"[CustomAsteroids:undo] could not redo {op.Kind} of '{op.Record.Code}'.");
            }
            return ok;
        }

        // Add the asteroid back to the live map + registry (reverse of a delete / redo of a place).
        private bool ApplyAdd(GameResourcesMap grm, PlacedAsteroidRecord rec)
        {
            var origin = new GlobalChunkCoordinate(rec.X, rec.Y, (short)rec.Z);

            if (!CanonicalShapeResolver.TryResolve(rec.Code, out ShapeDefinition shape, out string diag))
            {
                _logger.Warning?.Log($"[CustomAsteroids:undo] cannot re-add — '{rec.Code}' no longer resolves ({diag}).");
                return false;
            }

            var offsets = new List<ChunkVector>(rec.Tiles.Count);
            foreach (TileOffset t in rec.Tiles) offsets.Add(new ChunkVector(t.X, t.Y, 0));
            if (offsets.Count == 0) offsets.Add(new ChunkVector(0, 0, 0));

            if (!CustomAsteroidPlacer.TryAddSource(grm, shape, origin, offsets, _logger, out string addDiag, out _))
            {
                _logger.Warning?.Log($"[CustomAsteroids:undo] re-add at {origin} failed ({addDiag}).");
                return false;
            }

            _ui.Persistence?.ReAddRecord(rec);
            return true;
        }

        // Remove the asteroid from the live map + registry (reverse of a place / redo of a delete).
        private bool ApplyRemove(GameResourcesMap grm, PlacedAsteroidRecord rec)
        {
            var origin = new GlobalChunkCoordinate(rec.X, rec.Y, (short)rec.Z);
            CustomAsteroidPlacer.TryRemoveAt(grm, origin, _logger, out string diag);
            _ui.Persistence?.RemoveRecordExact(rec);
            _logger.Info?.Log($"[CustomAsteroids:undo] removed source at {origin} ({diag}).");
            return true; // registry is now consistent regardless of whether a live source existed
        }
    }

    /// <summary>
    /// PLAN-P03-001 Task 3 — drives <see cref="CustomAsteroidUndo"/> from the keyboard.
    /// Ctrl+Z undoes, Ctrl+Y (or Ctrl+Shift+Z) redoes — but ONLY when our corresponding stack is
    /// non-empty. When the stack is empty we don't consume the keypress at all, so vanilla undo
    /// behaves exactly as it does without the mod (the user's chosen design).
    /// </summary>
    internal sealed class CustomAsteroidUndoController : ITickRewirer
    {
        private readonly CustomAsteroidUndo _undo;
        private readonly ILogger _logger;
        private int _lastFrame = -1;

        public CustomAsteroidUndoController(CustomAsteroidUndo undo, ILogger logger)
        {
            _undo = undo;
            _logger = logger;
        }

        public void Tick(float deltaTime)
        {
            // Tick can fire more than once per frame; act once per frame.
            int frame = Time.frameCount;
            if (frame == _lastFrame) return;
            _lastFrame = frame;

            try
            {
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                if (!ctrl) return;

                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

                // Redo: Ctrl+Y or Ctrl+Shift+Z. Undo: Ctrl+Z (no shift).
                if (Input.GetKeyDown(KeyCode.Y) || (shift && Input.GetKeyDown(KeyCode.Z)))
                {
                    if (_undo.CanRedo) _undo.Redo();
                }
                else if (!shift && Input.GetKeyDown(KeyCode.Z))
                {
                    if (_undo.CanUndo) _undo.Undo();
                }
            }
            catch (Exception ex)
            {
                _logger.Error?.Log($"[CustomAsteroids:undo] tick threw (non-fatal): {ex}");
            }
        }

        public bool Equals(IRewirer other) => ReferenceEquals(this, other);
    }
}
