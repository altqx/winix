# HikariSub field notes: what changed in Winix

The notes from the HikariSub Qt rewrite listed where Winix fell short during the evidence runs, the legacy capture,
the audio hot-plug tests and the docking native gate with NVDA. This page maps each item to what Winix now does.
Everything below ships in the CLI, the agent skill and the MCP server alike.

## Priorities

| # | Note | Winix now |
|---|------|-----------|
| 1 | `ui drag` / `ui pointer` through the VM's HID tablet, multi-monitor mapping | `ui pointer --path 'x1,y1;x2,y2' --button left [--hold] [--release] [--steps N] [--delay MS] [--map virtual\|primary\|monitor:N\|WxH+X+Y]`, `ui drag FROM TO`, `ui calibrate --point x,y`. Host-side QMP `input-send-event`; the release runs in a `finally`, so a failed step never leaves a button held. Geometry comes from the guest's monitor list (DPI-aware, cached per VM) and falls back to the libvirt screenshot size when the worker is unreachable. |
| 2 | `ui keys` with the extended flag | Keys go through `SendInput` with scan codes; arrows, Home, End, PgUp, PgDn, Insert, Delete, right Ctrl/Alt, NumLock, Divide, PrintScreen, Win and Apps carry `KEYEVENTF_EXTENDEDKEY`. Sequences and repeats: `--keys 'CTRL+O DOWN*3 ENTER'`. `ui text --text` types literal text (Unicode input). |
| 3 | `run TASK --env K=V --arg V` | Both repeatable, merged over the task's `env`/`environment` and `arguments`, recorded as `parameters` on the job record and exposed to scripts as `$args` and `WINIX_ARGS`. |
| 4 | An explicit elevated path | `elevated: true` on a task, and `winix guest driver add GUEST_INF`. Both go through the provisioned on-demand `WinixElevated` SYSTEM task, which the test user may start but not edit (security descriptor). Only tasks flagged `elevated` and the `driver-add` verb are accepted. |
| 5 | `ui` reaches every top-level and owned window | `ui windows --pid` lists every visible top-level and owned window (hwnd, title, class, owner, foreground). `find`/`click`/`type`/`assert`/`wait` search all of them; `--window TITLE\|INDEX\|hwnd:N` scopes to one. `tree` walks with the control-view walker and stops at other windows of the process, which it lists under `windows`. |
| 6 | `ui launch` with arguments, environment and working directory; a task that leaves a process running | `ui launch --exe X --arg A --env K=V --cwd DIR --no-console --fresh-profile`. `detach: true` on a task skips the kill-on-close job object. |
| 7 | `run --wait` survives a dropped SSH connection | `wait` retries transport loss with backoff for `WINIX_CONTACT_TIMEOUT` seconds (default 600). Giving up is exit code **4** (`contact_lost`) with the resume command; the job keeps running. |
| 8 | VM device options in config; drivers at provisioning | `vm: { audio: none\|off\|spice, displays: N, video: vga\|qxl\|virtio, usb: [usb-audio, VVVV:PPPP] }` in `winix.yaml`. The domain is regenerated from the config whenever the VM starts from shut off. Provisioning installs `qxldod` and `viogpudo` directly from the virtio ISO. |
| 9 | A lease shown in `vm status` | `lease acquire --owner NAME --ttl 2h [--note]`, `lease release [--force]`, `lease status`. `sync`, `run`, mutating `ui` actions, `push`/`pull`, `vm down` and the elevated verbs are refused for other owners; `vm status` and `vm_status` show the lease. `run --queue` waits for a busy guest instead of failing. `--label` names the caller on the job record. |
| 10 | `push FILE` / `pull PATH` outside the synced tree | `push HOST GUEST` and `pull GUEST HOST` for files and directories, absolute or `%TEMP%`-rooted guest paths, SHA-256 verified both ways, read-only attributes cleared on arrival. `job artifacts ID --path GUEST_PATH` fetches one ad-hoc file; `job wait --artifacts DIR` downloads the ZIP. |

## Desktop automation

- `ui focus --pid [--window]` brings a window to the foreground (AttachThreadInput, restore if minimized, the Alt tap when Windows refuses). `ui state` returns the foreground window, the focused element with its ancestor path, and the cursor position.
- The worker is per-monitor-V2 DPI aware; screenshots are physical pixels. `ui screenshot --monitor N` and `ui monitors` (bounds, working area, DPI, scale) are available even when the desktop is locked.
- `ui kill --pid` and `job dump --pid [--full]` (minidump through `MiniDumpWriteDump`; thread stacks through `cdb -pv` when the Windows SDK debuggers are installed). Fetch results with `pull`.
- `ui batch --file steps.json` (or `--args '[...]'`) runs several actions in one round trip with one UI Automation session; `--continue-on-error` keeps going.

## Tasks and jobs

- `scriptFile:` points at a PowerShell file in the workspace; `env:` is accepted beside `environment:`.
- `run` prints the job ID first (`{"type":"started"}` in JSON); every JSON line carries a `type` (`started`, `job`, `artifacts`, `sync`, `logs`).
- `job logs --follow` streams until the job ends; `--all` concatenates what is available.
- `job status` adds `createdAtLocal`/`finishedAtLocal` beside the guest's UTC stamps and the VM name.
- Exit codes: 0 success, 1 task failure, 2 usage, 3 infrastructure, **4 contact lost (job still running)**, 130 interrupted.

## Sync and files

- `sync` is incremental: the host remembers the guest's manifest and the size/mtime/hash of every file, so unchanged files are neither rehashed nor sent; the manifest still lists everything and the guest verifies the files it is asked to keep. If the guest drifted (`incremental_stale`), a full sync is sent automatically; `--full` forces one.
- A plain `sync` prints one line (`files, added, changed, removed, unchanged, sent`); `--json` carries the manifest.
- Exclusions: `.git`, `.winix`, `.env`, `*.pfx`, `*.key` by default, plus `bin`/`obj` always, plus everything git ignores. Patterns match any path component. See `exclude:` in `winix.yaml`.

## VM devices and drivers

- `vm device add usb-audio [--id ID] [--props k=v,...]`, `vm device remove ID`, `vm device list`: QMP `device_add`/`device_del`, recorded per VM. `--job ID --marker TEXT` performs the change once the marker appears in the job log; `--delay 5s` adds a pause.
- `vm devices` prints the guest view: monitors with DPI, display adapters and driver versions, audio devices, PnP status.
- `audio: none` gives an ICH9 HDA endpoint backed by QEMU's silent backend; `usb-audio` devices attach to it (`audiodev=audio1`).
- `image build --nvda-url URL --nvda-sha256 HASH` runs the optional NVDA recipe (`provisioning/nvda.ps1`): portable install, silence synthesizer, IO-level log. Winix pins no NVDA hash; supply the one for the release you test with.

## One VM, several agents

Leases (above) plus `run --queue` and `--label`. The MCP server exposes the same through `vm_lease`, `job_start(label, queueSeconds, owner)` and `owner` on every mutating tool. Set `WINIX_OWNER` once per agent instead of passing `--owner` everywhere.

## Bloat and performance

- `scripts/publish.sh` builds the CLI as a ReadyToRun single binary and `./winix` prefers it; `WINIX_BUILD=Debug` keeps the development build.
- One SSH connection per VM is reused (OpenSSH `ControlMaster`, socket under `$XDG_RUNTIME_DIR`); `WINIX_SSH_MUX=0` disables it. `vm down` closes it.
- The Visual Studio developer environment is captured once per VS version and architecture in `C:\Winix\cache` and replayed on later tasks.
- The MCP host exits with its client (stdin EOF, and a parent-process watchdog); `winix doctor` lists orphaned hosts under `mcp-hosts`.
- Image building, media download and provisioning stay in the same binary, but nothing of theirs loads for the daily verbs; the measured cost of a short call is runtime start-up, which ReadyToRun addresses. A separate plugin binary was judged not worth a second install surface.

## The guest image

`provisioning/debloat.ps1` runs during image build with a manifest (`C:\Winix\debloat.json`, copied into the baseline on `seal`): provisioned Store apps and their installed copies (Office hub, Teams, Copilot, Xbox, News/Widgets, Clipchamp, Photos and the rest; Edge, WebView2, Store, Terminal, Notepad and Calculator stay), OneDrive and its default-profile setup hook, optional features and capabilities, background services (telemetry, SysMain, Delivery Optimization, Xbox, push notifications, Edge update), sign-in scheduled tasks, update and consumer-content policies, Defender exclusions for the workspace and vcpkg, and the not-indexed attribute on `C:\Winix`. `provisioning/measure.ps1` records idle CPU, memory, disk, process and service counts before and after (`measure-before.json`, `measure-after.json`, both in the baseline directory and in `toolchain.json`); pass `-ReferenceScript` to time a build as well.
