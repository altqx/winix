#!/usr/bin/env bash
# Installs Winix for the current user, from a checkout (publishes the CLI and the Windows guest first; needs the .NET 10 SDK)
# or from an unpacked release archive (prebuilt; no SDK). Puts `winix` on PATH and links the agent skill into the coding
# agents found on this machine. Safe to run again after `git pull` or over a newer release.
# It never uses sudo: missing system packages are reported, not installed. See docs/install.md.
#
#   scripts/install.sh [--bin-dir DIR] [--skills-dir DIR]... [--no-skill] [--no-build]
set -euo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
if [ -f "$script_dir/winix" ] && [ -d "$script_dir/guest" ]; then
  release_dir="$script_dir"     # an unpacked release: winix, guest/, provisioning/, skills/
else
  release_dir=""
  repo_dir="$(cd -- "$script_dir/.." && pwd)"
fi
bin_dir="$HOME/.local/bin"
skills_dirs=()
link_skill=1
build=1
while [ $# -gt 0 ]; do
  case "$1" in
    --bin-dir) bin_dir="$2"; shift 2 ;;
    --skills-dir) skills_dirs+=("$2"); shift 2 ;;
    --no-skill) link_skill=0; shift ;;
    --no-build) build=0; shift ;;
    -h|--help) sed -n '2,7p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "install.sh: unknown option $1" >&2; exit 2 ;;
  esac
done

say() { printf '%s\n' "$*"; }
fail() { printf 'install.sh: %s\n' "$*" >&2; exit 1; }

[ -n "$release_dir" ] && build=0
[ "$(uname -s)" = Linux ] && [ "$(uname -m)" = x86_64 ] || fail "Winix runs on Linux x86_64 hosts only."
command -v git >/dev/null || fail "git is required (sync reads the working tree through git)."
if [ "$build" = 1 ]; then
  command -v dotnet >/dev/null || fail "The .NET 10 SDK is required to build Winix: https://dotnet.microsoft.com/download/dotnet/10.0"
  dotnet --list-sdks | grep -q '^10\.' || fail "The .NET 10 SDK is required (found: $(dotnet --list-sdks | cut -d' ' -f1 | tr '\n' ' '))."
  say "==> Publishing the CLI and the Windows guest (a few minutes the first time)"
  "$repo_dir/scripts/publish.sh"
fi
if [ -n "$release_dir" ]; then
  target="$release_dir/winix"; skill_source="$release_dir/skills/winix"; origin="the release at $release_dir"
else
  [ -x "$repo_dir/artifacts/release/winix" ] || fail "No published binary at artifacts/release/winix; run without --no-build."
  target="$repo_dir/winix"; skill_source="$repo_dir/skills/winix"; origin="the checkout at $repo_dir"
fi

say "==> Linking $bin_dir/winix"
mkdir -p "$bin_dir"
launcher="$bin_dir/winix"
if [ -e "$launcher" ] && ! grep -q "winix/winix\|Winix launcher" "$launcher" 2>/dev/null; then
  fail "$launcher exists and is not a Winix launcher; move it away or pass --bin-dir."
fi
cat > "$launcher" <<EOF
#!/usr/bin/env bash
# Winix launcher (scripts/install.sh): runs $origin
exec "$target" "\$@"
EOF
chmod +x "$launcher"
case ":$PATH:" in *":$bin_dir:"*) ;; *) say "    $bin_dir is not on PATH; add it to your shell profile." ;; esac

if [ "$link_skill" = 1 ]; then
  if [ ${#skills_dirs[@]} -eq 0 ]; then
    [ -d "$HOME/.claude" ] && skills_dirs+=("$HOME/.claude/skills")
    [ -d "$HOME/.agents" ] && skills_dirs+=("$HOME/.agents/skills")
  fi
  for dir in "${skills_dirs[@]}"; do
    mkdir -p "$dir"
    target="$dir/winix"
    if [ -e "$target" ] && [ ! -L "$target" ]; then say "    $target exists and is not a link; left alone."; continue; fi
    ln -sfn "$skill_source" "$target"
    say "==> Linked the agent skill into $target"
  done
  [ ${#skills_dirs[@]} -eq 0 ] && say "    No agent skill directory found; pass --skills-dir DIR to link skills/winix."
fi

say "==> winix $("$launcher" version --json | sed 's/.*"version":"\([^"]*\)".*/\1/')"
say "==> Host check (winix doctor)"
if "$launcher" doctor; then doctor_ok=1; else doctor_ok=0; fi

cat <<EOF

MCP server (protocol 2026-07-28): stdio command "$launcher" with argument "mcp".
  Claude Code:  claude mcp add --scope user winix -- "$launcher" mcp
  Other clients: {"mcpServers": {"winix": {"command": "$launcher", "args": ["mcp"]}}}
EOF
if [ "$doctor_ok" = 0 ]; then
  say ""
  say "Some host checks failed. Install the missing packages listed in docs/setup.md (needs sudo), then run: winix doctor"
fi
say "Next: build the Windows image once (docs/setup.md), then 'winix init' in your project."
