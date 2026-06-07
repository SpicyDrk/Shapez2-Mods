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

## Author

Dork
