# Any Layer Trash

Delete shapes on any layer without dragging them down to layer 1 first.

## What it does

Placing a **Trash** building automatically fills **every layer** of that tile with
trash, and deleting any one of them clears the **whole column**. It behaves like a
normal trash everywhere else — you just get all layers in a single click.

- Works with single placement, **area-drag**, **blueprints**, and **platform deletes**.
- It's **one undoable action**: Ctrl+Z / Ctrl+Y affect the whole column at once.
- **Occupied layers are skipped** — if another building already sits on a layer, trash
  is not forced on top of it.
- Only **plain vanilla trash** is placed. Nothing mod-specific is written to your save,
  so you can remove the mod at any time and your trash stays as ordinary trash.

## Requirements

- [Shapez Shifter](https://steamcommunity.com/sharedfiles/filedetails/?id=3542611357)
  (Steam Workshop mod loader) — listed as a dependency in `manifest.json`.

## How it works (for the curious)

The mod doesn't add a new building. It hooks the player's build action
(`PlayerAction.TryExecute_INTERNAL`) and rewrites the action's payload at commit
time so a trash placement/deletion expands to all three layers — keeping it a single
transaction the engine's undo/redo and batch-delete already understand.

## Game version compatibility

Each mod DLL targets one Shapez 2 generation — a build for one will not load on the other.
Because `CODE-NOTES.md` is gitignored, the port record lives here so it survives a fresh clone.

| Game | Mod | Status |
|---|---|---|
| 1.0 | 1.0.1 | Superseded |
| 1.1 (builds 1130–1138) | 1.0.2 / 1.0.3 | **Published on the Workshop** |
| 1.2.0-rc3 | 1.0.4 (unreleased) | Builds clean; **cannot run yet** (see below) |

### Game 1.2 port notes

**ShapezShifter 1.1 does not load on game 1.2 — that block is upstream, not in this mod.**
Game 1.2 changed `GameModeBuildingsFactory.FromMetadata`'s first parameter from
`IBuildingsCatalog` to `IBuildingCatalogPair`; Shifter 1.1 still references the old signature
and throws `MissingMethodException` from `ShapezShifter.Hijack.GameInterceptors..ctor` during
mod loading, before any mod code runs. Every Shifter-based mod is affected. Until a
1.2-compatible Shifter ships, the 1.2 build of this mod cannot be run or verified in-game, and
the Workshop release is deliberately held at 1.0.3 so 1.1 players keep a working mod.

**What the 1.2 port actually required:** one reference. `BlueprintCurrency` moved into a new
`Game.Core.Blueprint.dll` (reached indirectly via `ModifyBuildingsPayload.BlueprintCurrencyModification`).
Nothing else changed.

**Audited, since it could not be run:** the mod's four MonoMod hooks resolve their targets by
name at runtime, so a changed parameter list would compile clean and fail only in-game. All four
were checked against decompiled 1.2 output and are unchanged —
`PlayerAction.TryExecute_INTERNAL`, `ActionModifyBuildings.IsPossible` (still a concrete
`override`, which the name-based lookup depends on), and
`MapPlayingfieldVoidTileTracker.Register/UnregisterBuilding`. There is no other reflection in
the mod, so that bounds the runtime-resolved surface; everything else is compile-checked against
the same DLLs the game loads.

**Known risk:** Shifter's `IBuildingsRewirer.ModifyGameBuildings` contract could change again in
a 1.2 Shifter (it already changed once, in 1.1). The parameter 1.2 actually broke is not part of
that contract, so it probably won't — but re-check it before releasing.

**Release is deliberately held.** The 1.2-targeted build is versioned `1.0.4` and declares Shifter
`"1.2.*"`; the Workshop item stays at `1.0.3` so 1.1 players keep a working mod. The Shifter
dependency range is the graceful-degradation gate — when it doesn't match the installed Shifter,
the game skips the mod as "out of date" instead of crashing. `"1.2.*"` assumes a 1.2-compatible
Shifter versions itself as `1.2.x`; that is unconfirmed until one ships, and it fails safe (the
mod simply won't load if the assumption is wrong). Verify it, and run
[VERIFICATION-1.2.md](VERIFICATION-1.2.md), before publishing.

## Author

Dork
