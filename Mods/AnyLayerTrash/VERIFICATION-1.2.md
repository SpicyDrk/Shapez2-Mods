# AnyLayerTrash — deferred in-game verification (game 1.2)

**Why this file exists.** The 1.2 port was completed without ever running the mod. ShapezShifter
1.1 throws `MissingMethodException` from its own constructor on game 1.2, before any mod code
loads, so no in-game check was possible. The port is verified *structurally* (clean build, all
four MonoMod hook targets confirmed against decompiled 1.2 signatures) but **not behaviourally**.

**Run this list the day a Shifter build loads on 1.2 — before republishing to the Workshop.**

## Preconditions

- [ ] A ShapezShifter build that loads on game 1.2 is installed (item `3542611357`).
- [ ] Game launches to the main menu with mods enabled and **no** mod-loading exception.
- [ ] `AnyLayerTrash` appears in the loaded-mod list.
- [ ] Build is current: `dotnet build Shapez2Mods-AnyLayerTrash.slnf -t:Rebuild` → `0 Error(s)`,
      deploying to `<SPZ2_PERSISTENT>/mods/AnyLayerTrash/`.

## Core behaviour

| # | Scenario | Expected result |
|---|---|---|
| 1 | Place trash on a platform tile | Trash appears on **all three layers** at that tile, as one action |
| 2 | Place trash where another building already occupies a layer | That layer is **skipped**; occupied layers are never overwritten |
| 3 | Delete one trash of a stacked trio | The **whole trio** is removed in a single action — no orphan on any layer |
| 4 | Ctrl+Z after a trio place | The entire trio is undone as one step, not layer-by-layer |
| 5 | Ctrl+Y (redo) after that undo | The trio comes back intact |

Items 3–5 are the highest-risk checks: they exercise `PlayerAction.TryExecute_INTERNAL` and the
reverse-action path, which is where the payload rewriting happens.

## Blueprint / void-tile regression (the 1.1-era bug class)

| # | Scenario | Expected result |
|---|---|---|
| 6 | Copy a platform containing stacked trash to a blueprint, then paste it | Paste **succeeds**; the platform is created with its stacked trash |
| 7 | Delete a platform containing stacked trash | Deletes cleanly — **no** `AggregateException`, action does not abort |

These two are why `VoidTileTrackerGuard` and the `IsPossible` hook exist at all
(`MapPlayingfieldVoidTileTracker` is z==0-only per island; stacking `RenderVoidBelow` buildings
broke both paste and delete). If 1.2 changed void-tile bookkeeping, these fail first.

## Vanilla non-regression

| # | Scenario | Expected result |
|---|---|---|
| 8 | Place and delete ordinary (non-trash) buildings | Completely unchanged behaviour |
| 9 | Save → exit → reload a world containing stacked trash | Trash persists; only vanilla trash is in the save (mod writes nothing custom) |
| 10 | Disable the mod and load that save | Save still loads; stacked trash remains as ordinary trash |

## Log gate

- [ ] `%USERPROFILE%\AppData\LocalLow\tobspr Games\shapez 2\Player.log` contains **no** exception
      or stack trace after running items 1–10. Scan the log directly — do not rely on the absence
      of a visible in-game error.
- [ ] The mod's own `[AnyLayerTrash:*]` log lines show the interceptor and void-tile guard
      installed, with no "not found — NOT installed" warnings (those indicate a hook target the
      1.2 audit expected to be present but wasn't).

## Before republishing

- [ ] Re-check Shifter's `IBuildingsRewirer.ModifyGameBuildings` signature against the shipping
      1.2 Shifter. It changed once already (1.1: `MetaGameModeBuildings` → `AuthoringBuildings`).
      The audit predicts it survives 1.2 unchanged — confirm rather than assume.
- [ ] Confirm the manifest's Shifter dependency range matches the shipping Shifter's actual
      version, so 1.1 players get a clean "out of date" skip rather than a crash.
- [ ] Only then publish:
      1. Build first — `SteamPublish` fails on an empty content folder
         (`dotnet build Shapez2Mods-AnyLayerTrash.slnf`, which deploys to
         `<SPZ2_PERSISTENT>/mods/AnyLayerTrash/`).
      2. `steamcmd` on PATH and `STEAM_USERNAME` set in the shell.
      3. `dotnet msbuild .\AnyLayerTrash.csproj -t:SteamPublish -v:detailed`, **or** run
         `Steam/SteamPublish.sh` directly from Git Bash (the MSBuild target shells out via
         cmd.exe, so `sh` must be on the *system* PATH for it to work).

> Publish preconditions are inlined above on purpose: the fuller rationale lives in
> `CODE-NOTES.md`, which is **gitignored** and therefore absent from a fresh clone.

---

## Run log — 2026-08-19 (Shifter 1.2.0)

ShapezShifter **1.2.0** shipped and the mod loads and runs on game 1.2.0-rc3. The manifest's
`"1.2.*"` dependency matched (Shifter versions itself `1.2.0`), and Shifter's
`IBuildingsRewirer.ModifyGameBuildings` signature is **unchanged** from 1.1 — the open risk
carried out of the Phase 2 audit is closed.

**Confirmed from `Player.log` (0 exception-class lines in the whole file):**

| Item | Status | Evidence |
|---|---|---|
| Preconditions | ✓ | `Loading …\mods\AnyLayerTrash\AnyLayerTrash.dll`; both hooks report *installed*, no "not found — NOT installed" warnings |
| 1 — place fills all layers | ✓ | `expanded trio at execute: place 2->6`, `4->12`, `10->30` — exactly 3× each time |
| 3 — delete removes whole trio | ✓ | `expanded trio at execute: delete 1->3` (5 occurrences) |
| Log gate | ✓ | 0 matches for Exception / StackTrace / NullReference / MissingMethod / MissingField / TypeLoad |
| Rewirer ran | ✓ | `captured trash group 'TrashDefaultVariant' (1 variant(s))` |

**Still unrun — these need deliberate play, and the log cannot stand in for them:**

- 2 — occupied layers skipped (the observed runs were all clean 3× expansions, so nothing was
  skipped; the skip path is untested)
- 4 / 5 — undo and redo of a trio
- 6 / 7 — blueprint paste over stacked trash, and platform delete with stacked trash
  (**highest residual risk** — this is the bug class `VoidTileTrackerGuard` exists for)
- 8 — vanilla non-regression
- 9 / 10 — save → reload, and load with the mod disabled
