# Host and image setup

Winix uses the current user's `qemu:///session` connection. VM disks, private keys, logs and baselines live in `$WINIX_HOME` or `~/.local/share/winix`. Do not run ordinary Winix operations as root. Only package installation needs elevation.

On Omarchy/Arch:

```sh
omarchy pkg add qemu-desktop libvirt edk2-ovmf swtpm virt-viewer xorriso
```

On Ubuntu:

```sh
sudo apt install qemu-system-x86 qemu-utils libvirt-daemon-system libvirt-clients ovmf swtpm swtpm-tools virt-viewer xorriso openssh-client curl git
```

The user needs read/write access to `/dev/kvm`. Install .NET 10 SDK to develop Winix. Published self-contained binaries do not need a system .NET runtime. `WINIX_OVMF_CODE` and `WINIX_OVMF_VARS` override the firmware paths when distro defaults differ.

```sh
dotnet restore
dotnet build
dotnet test
dotnet publish src/Winix.Guest -c Release -r win-x64 --self-contained true -o artifacts/guest
./winix doctor
./winix image build --guest artifacts/guest
```

`image build` downloads the pinned English x64 Windows 11 25H2 consumer ISO directly from Microsoft's CDN and verifies SHA-256. The source/hash provenance is documented in `Media.cs`; media is never fetched from third-party mirrors. VirtIO drivers come from the Fedora project's stable distribution. Their actual downloaded hash is recorded. Use `--iso /path/to/windows.iso` to supply equivalent Windows 11 Pro media yourself. Downloading media does not activate Windows; supply your own license through normal Windows activation.

The image installs **Visual Studio Community 2026** by default. `--edition Professional` and `--edition Enterprise` select those installers. Installer license eligibility remains the user's responsibility. The public Windows Pro installation key only selects the edition; it does not provide a license.

Image creation returns a VM named `build-windows-dev` after boot. Windows installation and tool downloads continue inside the VM. Open its display:

```sh
./winix console --vm build-windows-dev
```

The seed installs VirtIO/QEMU guest agent, OpenSSH, the full Visual Studio workloads, and the Winix desktop worker. Provisioning progress and errors are in `C:\Winix\provision.json` and `C:\Winix\provision.log`. The SYSTEM scheduled task `WinixProvision` resumes completed stages after reboot. On installation failure, inspect the logs and rerun the scheduled task; do not mark the image complete manually.

After the QEMU guest agent is installed, `./winix image status --vm build-windows-dev` reports the current stage without needing SSH or a desktop connection.

OpenSSH uses Microsoft's signed `10.0.0.0p2-Preview` MSI from the PowerShell/Win32-OpenSSH repository, with a pinned SHA-256. This avoids Windows Update's Features on Demand download during image creation. The upstream package is labeled Preview; Winix uses it in its disposable development guest. An existing `sshd` service is retained. See [Microsoft's MSI installation instructions](https://github.com/PowerShell/Win32-OpenSSH/wiki/Install-Win32-OpenSSH-Using-MSI).

Once provisioning has rebooted into the `winix` account:

```sh
./winix vm trust build-windows-dev
./winix vm ready build-windows-dev
./winix image seal --vm build-windows-dev --name windows-dev
./winix init
./winix vm up dev --wait
```

The optional live transport/job checks run with `python3 tests/live_jobs.py --vm dev`. They use a separate `live-jobs` workspace and verify detached jobs, failure exit codes, log paging, artifact export, cancellation and timeout. Run the application fixtures with `./winix ci --config examples/acceptance.yaml` after releasing the development VM.

SSH host keys are read through the local QEMU guest-agent channel and pinned. Winix does not use `StrictHostKeyChecking=no` or accept network-discovered host keys. `seal` checks toolchain readiness, shuts Windows down, and captures a flattened disk together with firmware and TPM state. It refuses to overwrite a baseline. The building VM remains available for investigation or explicit deletion.

Allow disk space for both the builder and the captured baseline, plus media and development overlays. The verified full-toolchain image occupied about 57 GiB per standalone disk. Standalone qcow2 disks use GNU coreutils `cp` with copy-on-write when supported, falling back to a sparse copy; overlays are flattened with `qemu-img`. A full conversion took about ten minutes on the validation host. If a normal shutdown times out while Windows is applying updates, inspect the console and let it finish before sealing. CI exports completed job artifacts before force-stopping and discarding its own overlay.

VM SSH forwarding binds only to `127.0.0.1`; no host source folders are mounted. Source archives are copied into guest-owned directories. One VM owns the workstation slot at a time: shut down `dev` before running CI. Contention returns `vm_busy` rather than interrupting work. UI commands and source synchronization are refused while a build/test job owns the desktop.

The guest is a disposable, trusted-code development environment. Autologon necessarily stores its generated local test password in Windows. Its development signing key is local to the image and must never sign a production release. Don't inject personal accounts or host secrets into a baseline. Winix isn't a hostile-code sandbox or a public pull-request runner.
