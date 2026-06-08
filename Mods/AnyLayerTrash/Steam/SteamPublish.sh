#!/usr/bin/env bash
#
# Publishes the built AnyLayerTrash mod to the Steam Workshop.
#
# Adapted from the tobspr sample mods (Portals/Steam/SteamPublish.sh). One
# change vs. the sample: the Steam login is read from the STEAM_USERNAME
# environment variable instead of being hard-coded, so no account name is
# committed to the repo. Set it once per shell before publishing, e.g.:
#
#   PowerShell:  $env:STEAM_USERNAME = "your_steam_login"
#
# Usage (preferred — via the csproj target):
#   dotnet msbuild .\AnyLayerTrash.csproj -t:SteamPublish -v:detailed
#
# Arg 1 is the content folder (the built mod dir) — supplied by the target.

set -euo pipefail

# Resolve the content folder (the built mod dir). Use arg 1 if given (e.g. the
# csproj target passes $(OutputPath)); otherwise derive it from SPZ2_PERSISTENT
# + this project's folder name, so no long path has to be typed/pasted.
MOD_NAME=$(basename "$PWD")
SRC_CONTENT="${1:-$SPZ2_PERSISTENT/mods/$MOD_NAME}"

# Validate BEFORE touching Steam: a missing/empty folder is what causes
# steamcmd's "Build for workshop item has no content" failure.
if [ ! -d "$SRC_CONTENT" ] || [ -z "$(ls -A "$SRC_CONTENT" 2>/dev/null)" ]; then
	echo "ERROR: content folder missing or empty:" >&2
	echo "       $SRC_CONTENT" >&2
	echo "       Build the mod first (dotnet build) so it deploys to the mods folder." >&2
	exit 1
fi
echo "Content source: $SRC_CONTENT"

# Normalise to a Windows path via cygpath (handles POSIX or Windows input,
# trailing slash or not — avoids backslash-quoting pitfalls).
CONTENT_PATH=$(cygpath -w "$SRC_CONTENT")

# Steam account to publish under. Fail fast if it isn't set.
STEAM_USER="${STEAM_USERNAME:?Set STEAM_USERNAME to your Steam login before publishing}"

# Current working dir, converted from POSIX (/c/...) to Windows (C:\...) form.
CURRENT_DIR=$(cygpath -w "$PWD")

# Absolute path to the preview image that becomes the Workshop / mod-menu icon.
PREVIEW_IMG=$CURRENT_DIR\\Steam\\preview.png

# Normalise to double-backslash paths the .vdf expects.
CONTENT_PATH="${CONTENT_PATH//\\/\\\\}"
PREVIEW_IMG="${PREVIEW_IMG//\//\\}"
PREVIEW_IMG="${PREVIEW_IMG//\\/\\\\}"

echo "CONTENT_PATH: $CONTENT_PATH"
echo "PREVIEW_IMG:  $PREVIEW_IMG"

export CONTENT_PATH
export PREVIEW_IMG

# Fill the template .vdf with the absolute content + preview paths.
envsubst < Steam\\base.vdf > Steam\\base.tmp.vdf

echo "--- resolved base.tmp.vdf ---"
cat Steam\\base.tmp.vdf
echo "-----------------------------"

TMP_VDF=$CURRENT_DIR\\Steam\\base.tmp.vdf

# Locate steamcmd: explicit $STEAMCMD wins, else PATH, else the standard
# Windows install dir.
STEAMCMD_BIN="${STEAMCMD:-}"
if [ -z "$STEAMCMD_BIN" ]; then
	if command -v steamcmd >/dev/null 2>&1; then
		STEAMCMD_BIN="steamcmd"
	elif [ -f "/c/Program Files (x86)/steamcmd/steamcmd.exe" ]; then
		STEAMCMD_BIN="/c/Program Files (x86)/steamcmd/steamcmd.exe"
	else
		echo "ERROR: steamcmd not found. Add it to PATH or set STEAMCMD=/path/to/steamcmd.exe" >&2
		exit 1
	fi
fi

# Authenticate + upload. steamcmd will prompt for password / Steam Guard
# the first time; after that the session is cached.
"$STEAMCMD_BIN" +login "$STEAM_USER" +workshop_build_item "$TMP_VDF" +quit

# steamcmd writes the assigned id back into the temp vdf. Grab it...
FILE_ID=$(grep '"publishedfileid"' Steam\\base.tmp.vdf | sed 's/.*"publishedfileid"[ \t]*"\([0-9]\+\)".*/\1/')
echo "New published file ID: $FILE_ID"

# ...and persist it into base.vdf so the next publish UPDATES the same item
# instead of creating a duplicate.
sed -i 's/\("publishedfileid"[ \t]*"\)[0-9]\+"/\1'"$FILE_ID"'"/' Steam\\base.vdf

# Clean up.
rm Steam\\base.tmp.vdf
