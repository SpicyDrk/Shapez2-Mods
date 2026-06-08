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

CONTENT_PATH=$1

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

# Authenticate + upload. steamcmd will prompt for password / Steam Guard
# the first time; after that the session is cached.
steamcmd +login "$STEAM_USER" +workshop_build_item "$TMP_VDF" +quit

# steamcmd writes the assigned id back into the temp vdf. Grab it...
FILE_ID=$(grep '"publishedfileid"' Steam\\base.tmp.vdf | sed 's/.*"publishedfileid"[ \t]*"\([0-9]\+\)".*/\1/')
echo "New published file ID: $FILE_ID"

# ...and persist it into base.vdf so the next publish UPDATES the same item
# instead of creating a duplicate.
sed -i 's/\("publishedfileid"[ \t]*"\)[0-9]\+"/\1'"$FILE_ID"'"/' Steam\\base.vdf

# Clean up.
rm Steam\\base.tmp.vdf
