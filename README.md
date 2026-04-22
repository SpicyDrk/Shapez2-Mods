# Shapez 2 Mod Workspace

A C# / .NET workspace for building multiple [Shapez 2](https://store.steampowered.com/app/2162800/shapez_2/) mods side-by-side under one roof. The root owns the shared MSBuild configuration (target framework, reference paths, output conventions), and each mod lives in its own subfolder under `Mods/`. Adding a new mod is a copy-and-rename; everything else is inherited.

This repo bundles a tiny `HelloWorld` mod that does nothing observable in-game — its only job is to prove the build pipeline works end-to-end. Real mods replace or live alongside it.

---

## Prerequisites

1. **Shapez 2** installed via Steam (version 1.0.0 or later).
2. **ShapezShifter** — the modding framework. Subscribe to it on the [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3542611357) *or* build it from [tobspr-games/shapez2-shifter](https://github.com/tobspr-games/shapez2-shifter). To see pre-public Workshop content, join the [Shapez 2 mod testing Steam group](https://steamcommunity.com/groups/shapez2-mod-testing) first.
3. **.NET SDK 9.0** — pinned for this repo via `global.json` (roll-forward to the latest 9.0 feature band). Install from [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download).
   > *Why pinned?* The .NET 10 SDK has an unresolved VSTest assembly-resolution issue (`Microsoft.TestPlatform.CoreUtilities 15.0.0.0`) that breaks test execution. SDK 9 sidesteps it.
4. **An IDE:** [JetBrains Rider](https://www.jetbrains.com/rider/) is the recommended path (the official [shapez2-mod-samples](https://github.com/tobspr-games/shapez2-mod-samples) ships with Rider project settings). Visual Studio 2022+ works too — the `.sln` opens cleanly.

---

## Environment variables

The build locates the game and Shifter via three environment variables:

| Variable | What it points at | Example |
|---|---|---|
| `SPZ2_PATH` | Shapez 2's `shapez 2_Data/Managed` directory (the game's reference DLLs) | `C:\Program Files (x86)\Steam\steamapps\common\shapez 2\shapez 2_Data\Managed` |
| `SPZ2_PERSISTENT` | Shapez 2's persistent-data path (where built mod DLLs land) | `%APPDATA%\..\LocalLow\tobspr Games\shapez 2` |
| `SPZ2_SHIFTER` | ShapezShifter's directory (containing `ShapezShifter.dll`) | `C:\Program Files (x86)\Steam\steamapps\workshop\content\2162800\3542611357` |

### Easy setup (Windows)

Launch Shapez 2 with the `--set-modding-env-vars` flag (via Steam's "Set Launch Options" or a shortcut) once. The game writes the three variables to your user environment. Restart Rider / Visual Studio afterward so it picks up the new values.

> *Source:* this flag is documented in the official [shapez2-mod-samples README](https://github.com/tobspr-games/shapez2-mod-samples#readme) — *"On Windows, these can be set automatically by the game by running the game with the command line argument `--set-modding-env-vars`."*

### Manual setup

**Windows (PowerShell, persistent):**
```powershell
[Environment]::SetEnvironmentVariable("SPZ2_PATH", "C:\Program Files (x86)\Steam\steamapps\common\shapez 2\shapez 2_Data\Managed", "User")
[Environment]::SetEnvironmentVariable("SPZ2_PERSISTENT", "$env:LOCALAPPDATA\..\LocalLow\tobspr Games\shapez 2", "User")
[Environment]::SetEnvironmentVariable("SPZ2_SHIFTER", "C:\Program Files (x86)\Steam\steamapps\workshop\content\2162800\3542611357", "User")
```

**macOS (`~/.zprofile`):**
```sh
export SPZ2_PATH="$HOME/Library/Application Support/Steam/steamapps/common/shapez 2/shapez 2.app/Contents/Resources/Data/Managed"
export SPZ2_PERSISTENT="$HOME/Library/Application Support/tobspr Games/shapez 2"
export SPZ2_SHIFTER="$HOME/Library/Application Support/Steam/steamapps/workshop/content/2162800/3542611357"
```

> *Mac note:* MonoMod-based patching frameworks (including ShapezShifter) require running Shapez 2 under Rosetta.

**Linux (`~/.bashrc` or `~/.zshrc`):**
```sh
export SPZ2_PATH="$HOME/.steam/steam/steamapps/common/shapez 2/shapez 2_Data/Managed"
export SPZ2_PERSISTENT="$HOME/.config/unity3d/tobspr Games/shapez 2"
export SPZ2_SHIFTER="$HOME/.steam/steam/steamapps/workshop/content/2162800/3542611357"
```

If any variable is missing when you build, the workspace will stop with a clear error (`SPZ2001`, `SPZ2002`, or `SPZ2003`) naming the missing variable — it won't leave you hunting through MSBuild reference-resolution failures.

---

## Build

From the repo root:

```sh
dotnet build Shapez2Mods.sln
```

Or open `Shapez2Mods.sln` in Rider / Visual Studio and hit Build.

Expected output:
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Built mod DLLs land at `$SPZ2_PERSISTENT/mods/<ModName>/<ModName>.dll`, alongside the mod's `manifest.json`. For the bundled `HelloWorld`:

```
$SPZ2_PERSISTENT/mods/HelloWorld/HelloWorld.dll
$SPZ2_PERSISTENT/mods/HelloWorld/manifest.json
```

---

## Run the smoke check

The smoke check validates `Mods/HelloWorld/manifest.json` against the Shapez 2 manifest schema — a cheap early warning if a mod's manifest drifts.

```sh
dotnet run --project Tests/HelloWorld.Tests/
```

Expected output:
```
  ✓ Manifest_Exists
  ✓ Manifest_IsValidJson
  ✓ Manifest_HasRequiredFields
  ✓ Manifest_DeclaresOwnDll
  ✓ Manifest_DeclaresShifterDep

Results: 5 passed, 0 failed (5 total)
```

> *Why a console app instead of xunit?* The .NET 9/10 SDKs' VSTest path fails to resolve `Microsoft.TestPlatform.CoreUtilities 15.0.0.0`, breaking the usual xunit flow. A plain console app with exit codes is simpler, has zero external test-framework dependencies, and gives the same pass/fail signal.

---

## Try the bundled HelloWorld mod in-game

`HelloWorld` is intentionally empty — its only job is to prove that the workspace can produce a Shifter-loadable mod. After building, launch Shapez 2 (with ShapezShifter installed) and you should see "Hello World" appear in the loaded-mod list. The game will behave exactly as vanilla.

If you don't see it in the list:
1. Confirm Shifter is installed (check the Workshop subscription or your build).
2. Confirm the DLL is at `$SPZ2_PERSISTENT/mods/HelloWorld/HelloWorld.dll` — if it's somewhere else, `SPZ2_PERSISTENT` likely points at the wrong directory.
3. Check the game's log output (Shifter logs mod-loading activity).

---

## Add a new mod

Copy the bundled `HelloWorld` as a starting point:

1. **Copy the folder** (and strip any stale build output that tagged along):
   ```sh
   cp -r Mods/HelloWorld Mods/MyMod
   rm -rf Mods/MyMod/bin Mods/MyMod/obj
   ```

2. **Rename the `.csproj`:**
   ```sh
   mv Mods/MyMod/HelloWorld.csproj Mods/MyMod/MyMod.csproj
   ```

3. **Edit `Mods/MyMod/Mod.cs`:** rename the class from `HelloWorldMod` to `MyModMod` and the namespace from `HelloWorld` to `MyMod`.

4. **Edit `Mods/MyMod/manifest.json`:** update `Title`, `Description`, `Author`, and set `Assemblies[0]` to `"MyMod.dll"`. Leave `Dependencies[]` (Shapez Shifter) alone.

5. **Add to the solution:**
   ```sh
   dotnet sln Shapez2Mods.sln add Mods/MyMod/MyMod.csproj
   ```

6. **Build:**
   ```sh
   dotnet build Shapez2Mods.sln
   ```

Your mod's DLL appears at `$SPZ2_PERSISTENT/mods/MyMod/MyMod.dll`. From here, add Shifter API calls to `Mod.cs` to do something interesting — the [official sample mods](https://github.com/tobspr-games/shapez2-mod-samples) (DiagonalCutter, SandboxIslands, BiggerPlatforms) are the best reference for what the Shifter Flow / Atomic / Hijack APIs can do.

---

## Repo layout

```
Shapez2-Mods/
├── Shapez2Mods.sln           ← aggregates every mod + the smoke-check project
├── Directory.Build.props     ← shared MSBuild config (target framework, env-var re-exports)
├── Directory.Build.targets   ← env-var check (SPZ2001/2002/2003 errors); per-mod defaults
├── global.json               ← pins .NET SDK to 9.0 (avoids VSTest issue in .NET 10)
├── Mods/
│   └── HelloWorld/
│       ├── HelloWorld.csproj ← references ShapezShifter + Game.Core.Modding + Core
│       ├── Mod.cs            ← minimal IMod implementation
│       └── manifest.json     ← Shapez 2 mod metadata (Title, Version, Dependencies, …)
├── Tests/
│   └── HelloWorld.Tests/     ← console-app smoke check for manifest.json
├── CLAUDE.md                 ← context for the Claude Code CLI (optional)
├── LICENSE                   ← MIT
└── README.md                 ← you are here
```

---

## Canonical references

Upstream is the authority — consult these before inventing anything.

- **Shapez 2 Modding Documentation (Notion):** https://www.notion.so/tobspr-games/Shapez-2-Modding-Documentation-2543c9e752e080a1a772c6b9ada7e462
- **Official sample mods:** https://github.com/tobspr-games/shapez2-mod-samples
- **ShapezShifter (modding API):** https://github.com/tobspr-games/shapez2-shifter
- **Steam group (Workshop pre-public access):** https://steamcommunity.com/groups/shapez2-mod-testing

---

## License

[MIT](./LICENSE) — same license as the official Shapez 2 samples repo.
