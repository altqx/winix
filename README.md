# Winix

Build and test real Windows desktop applications from Linux, without rebooting the host. A CLI, agent skill and strict MCP **2026-07-28** server share the same QEMU/KVM + libvirt backend.

## Development

```sh
dotnet build
dotnet test
./winix doctor
```

Follow [setup](docs/setup.md) to download Windows directly from Microsoft and provision a full Visual Studio Community VM. Host defaults: 6 vCPUs, 12 GiB RAM, 180 GiB sparse disk. Windows guest packages can be cross-published from Linux.

## Agent workflow

```sh
./winix vm up dev --wait
./winix lease acquire --owner gate --ttl 2h      # optional: several agents, one VM
./winix sync .                                   # incremental; one-line summary
./winix run build --wait --env CONFIG=Release --label gate
./winix run test --json --arg --filter=Dock
./winix job logs <job-id> --follow
./winix job wait <job-id> --artifacts out/evidence
./winix ui launch --exe 'C:\Winix\work\app\build\Debug\App.exe' --arg --demo --env QT_SCALE_FACTOR=1.5 --fresh-profile
./winix ui windows --pid 1234                    # every top-level and owned window
./winix ui focus --pid 1234 --window "Placement"
./winix ui keys --pid 1234 --keys 'CTRL+O DOWN*3 ENTER'
./winix ui drag 400,300 900,300                  # hardware pointer through the hypervisor
./winix ui screenshot --monitor 1 --output right.png
./winix pull '%TEMP%\app.log' artifacts/app.log
```

Configure executable/argument, inline `script` or `scriptFile` tasks in `winix.yaml`. Task working directories and artifact paths are relative to `C:\Winix\work\<workspace>`. Artifact entries are exact files/directories, not globs. `detach: true` lets an app a task starts outlive it; `elevated: true` runs it through the provisioned SYSTEM task. `vm:` declares audio, monitors and USB devices; `vm device add usb-audio --job <id> --marker READY` hot-plugs on a log marker. Builds use NTFS and preserve unchanged source mtimes for incremental compilation.

Sync never copies `.git`, `.winix`, `bin`, `obj`, anything git ignores, or the default secret patterns `.env`, `*.pfx`, `*.key` (extend with `exclude:`). Files outside the tree move with `push`/`pull`, hash-verified.

All operations accept `--json`; multi-step commands emit one object per line with a `type`. Jobs survive caller disconnection: `run --wait` retries a lost connection for ten minutes and then exits 4 with the job still running. Query the recorded job ID instead of submitting twice. `ci` uses a fresh overlay and retrieves a ZIP per task before destroying the instance. Infrastructure failures retain the VM for recovery. See [docs/field-notes.md](docs/field-notes.md) for the full list of behaviours added for the HikariSub gate and why.

The [agent skill](skills/winix/SKILL.md) describes the operational workflow. For a compatible MCP client, configure a stdio command pointing to the published binary with `args: ["mcp"]` (or the absolute repository launcher). Winix explicitly rejects old initialization handshakes. Desktop screenshots return MCP image content. Long tasks use explicit job IDs and polling; `job_wait` blocks with the same reconnect logic as the CLI. The host exits with its client; `doctor` reports orphaned hosts. Run `scripts/publish.sh` for the ReadyToRun binary that `./winix` prefers.

## Supported tooling

The image recipe includes Visual Studio C++ and .NET desktop workloads, MFC/ATL, C++/CLI, .NET Framework development tools, WinUI, UWP, CMake, MSBuild and MSIX packaging. Desktop interaction uses FlaUI/UI Automation (UIA2 selectable), keyboard input through `SendInput` scan codes with the extended flag for navigation keys, and pointer input through the hypervisor's USB tablet. The worker is per-monitor DPI aware. WinUI/UWP deploy through `Add-AppxPackage` and activate by application identity.

See [architecture and limitations](docs/architecture.md), [examples](examples/README.md), and the self-hosted CI workflow. The [acceptance record](docs/acceptance.md) documents 37 host tests, live Windows builds and UI tests, durable jobs, and two clean CI runs.
