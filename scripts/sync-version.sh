#!/usr/bin/env bash
# Single source of truth for the plugin version: Jellyfin.Plugin.ScheduleEnforcer.csproj's
# <Version> element. This script propagates it into meta.json and manifest.json's single
# version entry, so those two files never drift from the csproj by hand-editing.
#
# Everything else that references the version (deploy commands, the Enable API call) reads
# it back out of meta.json at execution time via jq rather than hardcoding a literal string --
# see README.md's "Build, test, deploy" and "Cutting a release" sections.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

CSPROJ="Jellyfin.Plugin.ScheduleEnforcer/Jellyfin.Plugin.ScheduleEnforcer.csproj"
VERSION=$(grep -oP '(?<=<Version>)[^<]+' "$CSPROJ")

if [ -z "$VERSION" ]; then
  echo "Could not read <Version> from $CSPROJ" >&2
  exit 1
fi

jq --arg v "$VERSION" '.version = $v' meta.json > meta.json.tmp && mv meta.json.tmp meta.json
jq --arg v "$VERSION" '.[0].versions[0].version = $v' manifest.json > manifest.json.tmp && mv manifest.json.tmp manifest.json

echo "Synced version $VERSION into meta.json and manifest.json."
