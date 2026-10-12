#!/usr/bin/env bash
# Publishes the daily CLI as a ReadyToRun single binary (short calls skip JIT and debug overhead) and the
# Windows guest the same way. The repository launcher ./winix picks artifacts/release/winix up automatically.
# The new release is built beside the old one and swapped in by rename, so running agents and MCP hosts keep
# their binary while new calls get the new one.
set -euo pipefail
repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_dir"
staging="artifacts/release.staging"
rm -rf "$staging" artifacts/release.old
dotnet publish src/Winix.Cli -c Release -r linux-x64 --self-contained true -p:PublishReadyToRun=true -p:PublishSingleFile=true -p:InvariantGlobalization=true -o "$staging"
dotnet publish src/Winix.Guest -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o "$staging/guest"
cp -r skills "$staging/"
if [ -d artifacts/release ]; then mv artifacts/release artifacts/release.old; fi
mv "$staging" artifacts/release
rm -rf artifacts/release.old
printf '%s\n' "Published artifacts/release/winix ($(artifacts/release/winix version --json | tr -d '\n ')) with Windows guest and skill."
