#!/usr/bin/env bash
# Publishes the daily CLI as a ReadyToRun single binary (short calls skip JIT and debug overhead) and the
# Windows guest the same way. The repository launcher ./winix picks artifacts/release/winix up automatically.
set -euo pipefail
repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_dir"
dotnet publish src/Winix.Cli -c Release -r linux-x64 --self-contained true -p:PublishReadyToRun=true -p:PublishSingleFile=true -p:InvariantGlobalization=true -o artifacts/release
dotnet publish src/Winix.Guest -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o artifacts/release/guest
cp -r skills artifacts/release/
printf '%s\n' "Published artifacts/release/winix (ReadyToRun) with Windows guest and skill."
