# Winix

Build, test and drive real Windows desktop apps from Linux, without dual-booting.

Winix runs Windows 11 with Visual Studio in a local KVM virtual machine and gives you, and your coding agent, one command line for it: sync your working tree in, run build and test tasks, read the logs, collect artifacts, and click through the app's UI with UI Automation and a hardware-level mouse and keyboard. The CLI, an agent skill and an MCP server all drive the same backend.

```sh
winix vm up dev --wait
winix sync .
winix run build --wait
winix run test --wait --artifacts out/
winix ui launch --exe 'C:\Winix\work\app\build\Debug\App.exe'
winix ui click --pid 4312 --automation-id SaveButton
winix ui screenshot --output app.png
```

## Install with your coding agent

Paste this into Claude Code, Codex or any coding agent that can run shell commands:

```text
Install Winix on this machine by following https://github.com/altqx/winix/blob/main/docs/install.md.
Ask me before running anything with sudo and before building the Windows image.
```

The agent checks the host and installs the packages you approve. It then installs Winix, wires up the skill and the MCP server, and builds the Windows image when you say so.

## Install it yourself

You need Linux on x86_64 with KVM, about 80 GiB of free disk for the Windows image, and the packages listed in [docs/install.md](docs/install.md#2-install-system-packages).

```sh
# Prebuilt (no .NET SDK needed)
gh release download --repo altqx/winix --pattern 'winix-*-linux-x64.tar.gz'
tar xzf winix-*-linux-x64.tar.gz && ./winix-*-linux-x64/install.sh

# Or from source (needs the .NET 10 SDK)
git clone https://github.com/altqx/winix.git && winix/scripts/install.sh
```

`install.sh` puts `winix` in `~/.local/bin`, links the agent skill into `~/.claude/skills` and `~/.agents/skills`, and runs `winix doctor`. It never uses sudo. Then build the Windows image once. This takes hours: it downloads Windows from Microsoft and installs Visual Studio.

```sh
winix image build
winix image status --vm build-windows-dev      # until the toolchain is ready
winix vm trust build-windows-dev && winix image seal --vm build-windows-dev --name windows-dev
```

[docs/setup.md](docs/setup.md) covers each provisioning stage, editions, your own ISO and recovery.

## What you get

- **Builds in a real Windows.** Visual Studio 2026 (Community by default) with the C++ and .NET desktop workloads, MFC/ATL, C++/CLI, WinUI, UWP, CMake, MSBuild and MSIX packaging. The developer environment is set up for every task.
- **Incremental sync.** `sync` copies your working tree (uncommitted changes included) into the guest. Each run sends only changed files and leaves unchanged timestamps alone, so builds stay incremental. It skips `.git`, `bin`, `obj`, everything git ignores and common secret files. Each sync records the commit, branch and dirty state.
- **Durable jobs.** `run` returns a job ID at once. Jobs survive dropped connections and Ctrl-C, have timeouts the host enforces, and can run a `teardown:` script after a cancel. `job cancel` escalates until the job is gone.
- **Desktop automation.** Every action lives under `ui`:
  - finding windows (including owned and floating ones);
  - walking the UI Automation tree;
  - click, type, keys (`'CTRL+O DOWN*3 ENTER'`, sent as scan codes so screen readers see them) and text;
  - focus, assertions, waits and screenshots per monitor;
  - drags and pointer paths through the hypervisor's USB tablet, which also work on the lock screen.

  The worker is per-monitor DPI aware.
- **Virtual hardware.** Several monitors, a silent audio endpoint for WASAPI tests, USB devices at boot, and USB hot-plug timed to a marker in a job's log.
- **Shared VMs.** Leases with an idle timeout and a first-come-first-served queue, a separate work tree per branch with shared compiler caches, and snapshots to revert to a known baseline before each evidence run.
- **Clean CI runs.** `winix ci` boots a throwaway overlay, runs the pipeline, saves each task's artifacts and deletes the VM.

## Configure a project

`winix init` writes a `winix.yaml`:

```yaml
version: 1
image: windows-dev
workspace: app
resources: { cpus: 6, memoryGiB: 12, diskGiB: 180 }
pipeline: [build, test]
tasks:
  build:
    script: |
      cmake -S . -B build -A x64
      if ($LASTEXITCODE) { exit $LASTEXITCODE }
      cmake --build build --config Debug
    timeoutSeconds: 1800
    artifacts: [build]
  test:
    executable: ctest
    arguments: [--test-dir, build, -C, Debug, --output-on-failure]
    timeoutSeconds: 600
```

Tasks are an `executable` with `arguments`, an inline PowerShell `script`, or a `scriptFile` in your repository. They run in `C:\Winix\work\<workspace>`. Other task keys:

- `env:` sets environment variables.
- `detach: true` lets an app the task starts keep running.
- `elevated: true` runs the task as SYSTEM through a provisioned task.
- `teardown:` cleans up after a cancel or timeout.

Per run, `--env K=V`, `--arg V` and `--timeout 20m` override them. A `vm:` block sets audio, monitors, video and USB devices. [examples/](examples/README.md) has projects for Win32, MFC, C++/CLI, WPF, WinForms, WinUI 3 and UWP.

## Use it from an agent

The [agent skill](skills/winix/SKILL.md) teaches the edit, sync, build, test and inspect loop, and how to tell test failures from infrastructure failures. Every command takes `--json` and prints one object per line.

The MCP server (`winix mcp`, protocol revision `2026-07-28`) exposes the same operations as tools: jobs, sync, push and pull, desktop actions with image screenshots, leases, snapshots and devices. Register it with:

```sh
claude mcp add --scope user winix -- ~/.local/bin/winix mcp
```

Several agents can share one VM:

```sh
winix lease acquire --owner agent-a --wait        # queues until the VM is free
winix sync . --workspace feature-x                # a work tree per branch
winix run test --workspace feature-x --label agent-a --wait
winix lease release --owner agent-a
```

## Security model

The guest is a disposable development environment for code you trust. It is not a sandbox for hostile code or a runner for public pull requests.

- Winix uses your user's `qemu:///session` libvirt and never runs as root.
- SSH to the guest binds only to `127.0.0.1`, and host keys are pinned through the QEMU guest agent.
- No host folders are mounted; sources are copied in.
- Tasks run as a standard desktop user. Admin work goes only through the explicit `elevated: true` path and `winix guest driver add`.
- Windows media comes from Microsoft's CDN and is checked against a pinned SHA-256.
- You supply the Windows license. Visual Studio's own license terms apply.

## Documentation

- [docs/install.md](docs/install.md): installation, written for coding agents
- [docs/setup.md](docs/setup.md): host packages, image provisioning, security notes
- [docs/field-notes.md](docs/field-notes.md): behaviours added from real use, and why
- [skills/winix/SKILL.md](skills/winix/SKILL.md): the agent workflow
- `winix --help`: every command and option

## Development

```sh
dotnet build && dotnet test
python3 tests/mcp_smoke.py                 # the MCP server end to end
python3 tests/live_jobs.py --vm dev        # against a running VM
scripts/publish.sh                         # ReadyToRun binary that ./winix prefers
winix guest update                         # put the matching worker on a running VM
```
