# Installing Winix (for coding agents)

You are installing Winix for the user on their Linux machine. Winix runs Windows 11 in a local KVM virtual machine and lets you build, test and drive Windows desktop apps from Linux through a CLI, an agent skill and an MCP server.

Work through the steps in order. Report what you did at the end. Stop and ask the user where a step says **ask**.

## 1. Check the host

Winix needs Linux on x86_64 with hardware virtualization. Run:

```sh
uname -sm                      # expect: Linux x86_64
ls -l /dev/kvm                 # must exist; the user must be able to read and write it
df -h ~                        # at least 80 GiB free for one image; 150 GiB or more is comfortable
nproc; free -g                 # the default VM takes 6 vCPUs and 12 GiB RAM
```

If `/dev/kvm` is missing, virtualization is off in the firmware or the machine is itself a VM without nested virtualization. Tell the user and stop; Winix cannot run there. If it exists but is not writable, the user usually needs to join the `kvm` group (**ask** before running `sudo usermod -aG kvm "$USER"`; it takes effect after logging in again).

## 2. Install system packages

**Ask** before running anything with `sudo`. Install what is missing:

| Distribution | Command |
|---|---|
| Arch, Omarchy, Manjaro | `sudo pacman -S --needed qemu-desktop libvirt edk2-ovmf swtpm virt-viewer xorriso openssh curl git` |
| Ubuntu, Debian | `sudo apt install qemu-system-x86 qemu-utils libvirt-daemon-system libvirt-clients ovmf swtpm swtpm-tools virt-viewer xorriso openssh-client curl git` |

On other distributions install the equivalents: QEMU with KVM, libvirt, OVMF (UEFI firmware), swtpm, virt-viewer, xorriso, OpenSSH client, curl and git. If `winix doctor` cannot find the firmware, set `WINIX_OVMF_CODE` and `WINIX_OVMF_VARS` to the distribution's 4M OVMF code and variables files.

Winix uses the user's own `qemu:///session` libvirt connection. Never run Winix itself as root.

## 3. Get Winix

Choose one. A prebuilt release needs no .NET SDK; a checkout is easier to update and to change.

**Prebuilt release** (recommended for using Winix):

```sh
mkdir -p ~/.local/opt && cd ~/.local/opt
gh release download --repo altqx/winix --pattern 'winix-*-linux-x64.tar.gz'   # or download it from the Releases page
tar xzf winix-*-linux-x64.tar.gz
./winix-*-linux-x64/install.sh
```

**From source** (needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)):

```sh
git clone https://github.com/altqx/winix.git ~/.local/src/winix
~/.local/src/winix/scripts/install.sh
```

`install.sh` never uses sudo. It does four things:

1. Creates `~/.local/bin/winix`.
2. Links the agent skill into `~/.claude/skills` and `~/.agents/skills` when those directories exist. Use `--skills-dir DIR` for other agents.
3. Runs `winix doctor`.
4. Prints the MCP settings.

Re-run it after `git pull` or over a newer release. If `~/.local/bin` is not on `PATH`, add it to the user's shell profile.

`winix doctor` must pass every check before going on. Each failing check names what is missing.

## 4. Register the MCP server (optional)

Winix's MCP server speaks protocol revision `2026-07-28` only; clients on older revisions should use the CLI and the skill instead.

```sh
claude mcp add --scope user winix -- ~/.local/bin/winix mcp       # Claude Code
codex mcp add winix -- ~/.local/bin/winix mcp                     # Codex CLI
```

For other clients, add a stdio server with command `~/.local/bin/winix` (as an absolute path) and the argument `mcp`.

## 5. Build the Windows image

This step is long and heavy. It downloads the Windows 11 ISO from Microsoft (about 7 GiB), installs Windows unattended, then downloads and installs Visual Studio with the C++ and .NET desktop workloads. Expect it to take hours and to use about 60 GiB per image. **Ask** the user before starting it. Tell them about licensing: Windows needs their own license to activate, and Visual Studio Community has its own eligibility terms. They may prefer `--edition Professional` or `Enterprise`, or their own ISO via `--iso PATH`.

```sh
winix image build                    # returns once the VM boots; installation continues inside it
winix image status --vm build-windows-dev   # poll; stages are reported in order
```

When `image status` shows the toolchain is ready and Windows has rebooted into the `winix` account:

```sh
winix vm trust build-windows-dev
winix vm ready build-windows-dev
winix image seal --vm build-windows-dev --name windows-dev
```

`winix console --vm build-windows-dev` opens the VM's display if the user wants to watch. [setup.md](setup.md) explains each stage and how to recover from a failed one.

## 6. Set up a project

In the user's project (a git repository):

```sh
winix init                 # writes winix.yaml; edit its build and test tasks
winix vm up dev --wait
winix sync .
winix run build --wait
```

From here on, follow the Winix skill (`skills/winix/SKILL.md`) for the edit, build and test loop.

## Report

Tell the user:

- which install path you used and the `winix version`;
- the result of `winix doctor`;
- whether the MCP server was registered, and in which client;
- whether the image was built, and if not, the command that builds it.
