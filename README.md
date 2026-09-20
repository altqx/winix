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
./winix sync .
./winix run build --json
./winix job status <job-id>
./winix job logs <job-id>
./winix job artifacts <job-id>
./winix ui launch --exe 'C:\Winix\work\app\build\Debug\App.exe'
./winix ui tree --pid 1234
./winix ui screenshot --output desktop.png
```

Configure executable/argument or PowerShell tasks in `winix.yaml`. Task working directories and artifact paths are relative to `C:\Winix\work\<workspace>`. Artifact entries are exact files/directories, not globs. Exclusions support path prefixes and component globs. Builds use NTFS and preserve unchanged source mtimes for incremental compilation.

All operations accept `--json`. Jobs survive caller disconnection; query the recorded job ID instead of submitting twice. `ci` uses a fresh overlay and retrieves a ZIP per task before destroying the instance. Infrastructure failures retain the VM for recovery.

The [agent skill](skills/winix/SKILL.md) describes the operational workflow. For a compatible MCP client, configure a stdio command pointing to the published binary with `args: ["mcp"]` (or the absolute repository launcher). Winix explicitly rejects old initialization handshakes. Desktop screenshots return MCP image content. Long tasks use explicit job IDs and polling.

## Supported tooling

The image recipe includes Visual Studio C++ and .NET desktop workloads, MFC/ATL, C++/CLI, .NET Framework development tools, WinUI, UWP, CMake, MSBuild and MSIX packaging. Desktop interaction uses FlaUI/UI Automation, with UIA2 selectable when needed. WinUI/UWP deploy through `Add-AppxPackage` and activate by application identity.

See [architecture and limitations](docs/architecture.md), [examples](examples/README.md), and the self-hosted CI workflow. The [acceptance record](docs/acceptance.md) documents 37 host tests, live Windows builds and UI tests, durable jobs, and two clean CI runs.
