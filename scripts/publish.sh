#!/usr/bin/env bash
set -euo pipefail
repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_dir"
dotnet publish src/Winix.Cli -c Release -r linux-x64 --self-contained true -o artifacts/release
dotnet publish src/Winix.Guest -c Release -r win-x64 --self-contained true -o artifacts/release/guest
cp -r skills artifacts/release/
printf '%s\n' "Published artifacts/release/winix with Windows guest and skill."
