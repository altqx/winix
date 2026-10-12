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

- `scripts/publish.sh` builds the CLI as a ReadyToRun single binary and `./winix` prefers it; `WINIX_BUILD=Debug` keeps the development build. `winix guest update` ships the published guest to a running VM (SSH upload, swap as SYSTEM through the QEMU guest agent, data directories and the `WinixElevated` task created on the way), so an existing image gains new worker features without a rebuild.
- One SSH connection per VM is reused (OpenSSH `ControlMaster`, socket under `$XDG_RUNTIME_DIR`); `WINIX_SSH_MUX=0` disables it. `vm down` closes it.
- The Visual Studio developer environment is captured once per VS version and architecture in `C:\Winix\cache` and replayed on later tasks.
- The MCP host exits with its client (stdin EOF, and a parent-process watchdog); `winix doctor` lists orphaned hosts under `mcp-hosts`.
- Image building, media download and provisioning stay in the same binary, but nothing of theirs loads for the daily verbs; the measured cost of a short call is runtime start-up, which ReadyToRun addresses. A separate plugin binary was judged not worth a second install surface.

## The guest image

`provisioning/debloat.ps1` runs during image build with a manifest (`C:\Winix\debloat.json`, copied into the baseline on `seal`): provisioned Store apps and their installed copies (Office hub, Teams, Copilot, Xbox, News/Widgets, Clipchamp, Photos and the rest; Edge, WebView2, Store, Terminal, Notepad and Calculator stay), OneDrive and its default-profile setup hook, optional features and capabilities, background services (telemetry, SysMain, Delivery Optimization, Xbox, push notifications, Edge update), sign-in scheduled tasks, update and consumer-content policies, Defender exclusions for the workspace and vcpkg, and the not-indexed attribute on `C:\Winix`. `provisioning/measure.ps1` records idle CPU, memory, disk, process and service counts before and after (`measure-before.json`, `measure-after.json`, both in the baseline directory and in `toolchain.json`); pass `-ReferenceScript` to time a build as well.

## After 0.2.0 (shipped in 0.2.1)

A day of seven agents sharing one VM found jobs and leases that outlived their owners. Each note maps to what 0.2.1 does. The guest-side items need the 0.2.1 worker (`scripts/publish.sh`, then `winix guest update`).

| Note | Winix now |
|------|-----------|
| A job cancel could not stop | Root cause: after the wrapper exited (or was killed), the worker waited without limit for its output pipes to close, and a child that inherited them (a detached app) never closed them, so the record stayed `running` and the guest stayed busy. Output is now drained for at most 5 s, then capture stops with a note in the log; a process that ignores termination is waited for at most 10 s. `job cancel` escalates by itself: cooperative cancel (tree terminated, teardown run), then `force` (kill everything the task started, detached or not), then a worker restart as SYSTEM through the QEMU guest agent. `--force` skips the first step; the result lists the `escalation` steps taken. The host enforces the timeout too: `job wait` and a `run --queue` waiting behind the job stop a job running past its timeout plus teardown plus 60 s (`WINIX_TIMEOUT_GRACE`) as `timedOut`. `job status` shows `elapsedSeconds`, `timeoutSeconds`, `remainingSeconds` and `overdue`. `guest restart` restarts a stuck worker by hand. |
| Leases outlive their owner | A lease ends after 30 minutes with no job of its holder running and no Winix call from them (`lease acquire --idle 30m`; `0` disables). A running job keeps it alive until the job's timeout. Renewing is not activity. No lease lasts past `WINIX_LEASE_MAX_TTL` (8 h) from its first acquisition, renewals included. `lease status` lists the queue. |
| Waiting for a lease is a poll loop | `lease acquire --wait [--timeout 4h]` (MCP: `vm_lease` with `wait`, `waitTimeout`) joins a first-come-first-served queue. A free VM goes to the head of the queue; a caller that did not queue cannot jump ahead. A waiter that exits drops out within 30 s. |
| Which commit is on the guest? | `sync` records the source commit, branch and a dirty flag (working tree differs from HEAD). They appear in the sync summary and JSON, travel to the guest with the manifest, and are stamped on every job record as `source`. |
| One work tree for every branch | `sync --workspace NAME` and `run --workspace NAME` (or `WINIX_WORKSPACE`) give each branch its own `C:\Winix\work\NAME`. Jobs get shared cache locations by default: `CCACHE_DIR`, `SCCACHE_DIR`, `VCPKG_DEFAULT_BINARY_CACHE` and `WINIX_CACHE` under `C:\Winix\cache`, and `CCACHE_BASEDIR=C:\Winix\work` so paths hash alike across trees. Values set by the task or the machine win. A sync into a tree that last held another branch prints a note. |
| Interrupting the client killed the guest side | Child commands (ssh, scp, virsh, qemu-img, git) run in their own session, so a SIGINT sent to the caller's process group no longer reaches them. A guest call already in flight runs to completion, and the first Ctrl-C only stops the client's waiting. A second Ctrl-C exits at once. A task's `teardown:` script (with `teardownTimeoutSeconds`, default 120) runs after a cancel, a timeout, or a worker restart that interrupted the job. It runs as the desktop user with the task's environment and `WINIX_TEARDOWN_REASON`, its output is tagged `[teardown:…]` in the job log, and its result is the record's `teardown`. A forced stop does not cut a teardown short; only its own timeout does. |
| Fixed task timeouts | `run --timeout 20m` (MCP: `job_start` `timeoutSeconds`) overrides the task's timeout for one run; the job record carries `timeoutSeconds`. The queue window moved to `run --queue --queue-timeout 1h`. |
| A clean baseline between evidence runs | `vm snapshot create NAME [--note]`, `vm snapshot list`, `vm snapshot delete NAME`, `vm revert NAME [--restart]` (MCP: `vm_snapshot`). A snapshot is an internal qcow2 snapshot of the VM's overlay plus copies of its UEFI variables and TPM state, taken and restored with the VM shut off. `--restart` shuts a running VM down, reverts, starts it and waits for the desktop. A revert also rolls back the worker; a `guest update` made after the snapshot has to be repeated. |
