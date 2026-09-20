using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;

namespace Winix;

public sealed class VmManager(StateStore store, ICommandRunner runner)
{
    public const string Connection = "qemu:///session";
    private Task<string> Virsh(params string[] args) => runner.Checked("virsh", new[] { "-c", Connection }.Concat(args));
    public static string? FindFirmware() => new[] { System.Environment.GetEnvironmentVariable("WINIX_OVMF_CODE"),
        "/usr/share/edk2/x64/OVMF_CODE.secboot.4m.fd", "/usr/share/edk2/x64/OVMF_CODE.4m.fd", "/usr/share/OVMF/OVMF_CODE_4M.fd" }.FirstOrDefault(p => p is not null && File.Exists(p));
    public static string FindVariables(string code) => System.Environment.GetEnvironmentVariable("WINIX_OVMF_VARS") ??
        (File.Exists(Path.Combine(Path.GetDirectoryName(code)!, "OVMF_VARS.4m.fd")) ? Path.Combine(Path.GetDirectoryName(code)!, "OVMF_VARS.4m.fd") : Path.Combine(Path.GetDirectoryName(code)!, "OVMF_VARS_4M.fd"));
    public async Task<string> Status(string name)
    {
        var result = await runner.Run("virsh", ["-c", Connection, "domstate", "winix-" + Paths.Id(name)]);
        return result.ExitCode == 0 ? result.Stdout.Trim() : "undefined";
    }
    public static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
    public async Task EnsureCapacity(string? except = null)
    {
        foreach (var vm in store.List<VmRecord>("vms"))
            if (vm.Name != except && await Status(vm.Name) is not ("shut off" or "undefined"))
                throw new WinixException("vm_busy", $"VM {vm.Name} owns the workstation slot. Shut it down when its work is complete.");
    }
    public static string Domain(VmRecord vm, Resources resources, string firmware, string? iso = null, string? drivers = null, string? seed = null)
    {
        XNamespace qemu = "http://libvirt.org/schemas/domain/qemu/1.0";
        var devices = new XElement("devices",
            new XElement("disk", new XAttribute("type", "file"), new XAttribute("device", "disk"),
                new XElement("driver", new XAttribute("name", "qemu"), new XAttribute("type", "qcow2")),
                new XElement("source", new XAttribute("file", Path.Combine(vm.Directory, "disk.qcow2"))),
                new XElement("target", new XAttribute("dev", "vda"), new XAttribute("bus", "virtio")),
                new XElement("boot", new XAttribute("order", "2"))),
            new XElement("controller", new XAttribute("type", "sata"), new XAttribute("index", "0")),
            new XElement("graphics", new XAttribute("type", "spice"), new XAttribute("autoport", "yes"), new XElement("listen", new XAttribute("type", "address"), new XAttribute("address", "127.0.0.1"))),
            new XElement("video", new XElement("model", new XAttribute("type", "vga"), new XAttribute("vram", "65536"))),
            new XElement("input", new XAttribute("type", "tablet"), new XAttribute("bus", "usb")),
            new XElement("controller", new XAttribute("type", "usb"), new XAttribute("model", "qemu-xhci")),
            new XElement("channel", new XAttribute("type", "unix"), new XElement("target", new XAttribute("type", "virtio"), new XAttribute("name", "org.qemu.guest_agent.0"))),
            new XElement("tpm", new XAttribute("model", "tpm-crb"), new XElement("backend", new XAttribute("type", "emulator"), new XAttribute("version", "2.0"),
                new XElement("source", new XAttribute("type", "dir"), new XAttribute("path", Path.Combine(vm.Directory, "tpm"))))));
        var cds = new[] { iso, drivers, seed };
        for (var i = 0; i < cds.Length; i++)
            if (cds[i] is { } path) devices.Add(new XElement("disk", new XAttribute("type", "file"), new XAttribute("device", "cdrom"),
                new XElement("driver", new XAttribute("name", "qemu"), new XAttribute("type", "raw")),
                new XElement("source", new XAttribute("file", path)), new XElement("target", new XAttribute("dev", "sd" + (char)('a' + i)), new XAttribute("bus", "sata")),
                new XElement("readonly"), i == 0 ? new XElement("boot", new XAttribute("order", "1")) : null));
        return new XDocument(new XElement("domain", new XAttribute("type", "kvm"), new XAttribute(XNamespace.Xmlns + "qemu", qemu),
            new XElement("name", "winix-" + vm.Name), new XElement("uuid", vm.Uuid),
            new XElement("memory", new XAttribute("unit", "GiB"), resources.MemoryGiB), new XElement("vcpu", resources.Cpus),
            new XElement("os", new XElement("type", new XAttribute("arch", "x86_64"), new XAttribute("machine", "q35"), "hvm"),
                new XElement("loader", new XAttribute("readonly", "yes"), new XAttribute("type", "pflash"), firmware),
                new XElement("nvram", Path.Combine(vm.Directory, "nvram.fd"))),
            new XElement("features", new XElement("acpi"), new XElement("apic"), new XElement("smm", new XAttribute("state", "on"))),
            new XElement("cpu", new XAttribute("mode", "host-passthrough")), new XElement("clock", new XAttribute("offset", "utc")),
            new XElement("on_reboot", "restart"), new XElement("on_poweroff", "destroy"), devices,
            new XElement(qemu + "commandline",
                new XElement(qemu + "arg", new XAttribute("value", "-netdev")), new XElement(qemu + "arg", new XAttribute("value", $"user,id=net0,hostfwd=tcp:127.0.0.1:{vm.SshPort}-:22")),
                new XElement(qemu + "arg", new XAttribute("value", "-device")), new XElement(qemu + "arg", new XAttribute("value", "virtio-net-pci,netdev=net0,bus=pcie.0,addr=0x10"))))).ToString();
    }
    public async Task<VmRecord> Up(string name, string image, Resources resources, bool disposable = false)
    {
        Paths.Id(name); Paths.Id(image);
        using var guard = store.Lock();
        await EnsureCapacity(name);
        var directory = store.Dir("vms", name);
        VmRecord vm;
        if (File.Exists(Path.Combine(directory, "record.json"))) vm = store.Read<VmRecord>("vms", name);
        else
        {
            var baseline = store.Read<ImageManifest>("images", image);
            if (await Media.Hash(Path.Combine(baseline.Directory, "disk.qcow2")) != baseline.DiskSha256)
                throw new WinixException("baseline_changed", "The baseline disk checksum changed.");
            Directory.CreateDirectory(directory);
            vm = new(name, image, directory, FreePort(), Guid.NewGuid().ToString(), disposable);
            await runner.Checked("qemu-img", ["create", "-f", "qcow2", "-F", "qcow2", "-b", Path.Combine(baseline.Directory, "disk.qcow2"), Path.Combine(directory, "disk.qcow2")]);
            File.Copy(Path.Combine(baseline.Directory, "nvram.fd"), Path.Combine(directory, "nvram.fd"));
            CopyTree(Path.Combine(baseline.Directory, "tpm"), Path.Combine(directory, "tpm"));
            File.Copy(Path.Combine(baseline.Directory, "id_ed25519"), Path.Combine(directory, "id_ed25519"));
            File.Copy(Path.Combine(baseline.Directory, "hostkey.pub"), Path.Combine(directory, "hostkey.pub"));
            SetPrivateKey(directory);
            var xml = Domain(vm, resources, FindFirmware() ?? throw new WinixException("firmware_missing", "Run winix doctor."));
            File.WriteAllText(Path.Combine(directory, "domain.xml"), xml);
            store.Write("vms", name, vm);
        }
        if (vm.Image != image) throw new WinixException("image_mismatch", $"VM {name} uses {vm.Image}, not {image}.");
        if (await Status(name) == "undefined") await Virsh("define", Path.Combine(directory, "domain.xml"));
        if (await Status(name) == "shut off") await Virsh("start", "winix-" + name);
        return vm;
    }
    public async Task Down(string name, bool force = false)
    {
        using var guard = store.Lock();
        var vm = store.Read<VmRecord>("vms", name);
        var state = await Status(name);
        if (state is "shut off" or "undefined") return;
        if (!force && File.Exists(Path.Combine(vm.Directory, "hostkey.pub")))
        {
            var health = await new GuestConnection(vm, runner).Call("health", new { });
            if (health.TryGetProperty("activeJob", out var active) && active.ValueKind != System.Text.Json.JsonValueKind.Null)
                throw new WinixException("busy", $"Job {active.GetString()} is running. Wait or cancel it before shutdown.");
        }
        await Virsh(force ? "destroy" : "shutdown", "winix-" + Paths.Id(name));
        for (var i = 0; i < 60 && await Status(name) != "shut off"; i++) await Task.Delay(1000);
        if (await Status(name) != "shut off") throw new WinixException("shutdown_timeout", "Windows did not shut down. Use --force explicitly if needed.");
    }
    public async Task Delete(string name)
    {
        using var guard = store.Lock();
        var vm = store.Read<VmRecord>("vms", name);
        if (await Status(name) is not ("shut off" or "undefined")) throw new WinixException("vm_running", "Shut down the VM before deletion.");
        if (await Status(name) != "undefined") await Virsh("undefine", "winix-" + name, "--nvram", "--tpm");
        Directory.Delete(vm.Directory, true);
    }
    public static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), false);
        foreach (var dir in Directory.GetDirectories(from)) CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
    public static void SetPrivateKey(string directory)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(directory, "id_ed25519"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    public async Task PinHostKey(VmRecord vm)
    {
        var key = (await GuestExec(vm.Name, @"C:\Windows\System32\cmd.exe", ["/c", @"type C:\ProgramData\ssh\ssh_host_ed25519_key.pub"])).Trim();
        if (!key.StartsWith("ssh-ed25519 ")) throw new WinixException("hostkey_invalid", "Unexpected SSH host key.");
        File.WriteAllText(Path.Combine(vm.Directory, "hostkey.pub"), key);
    }
    public async Task<System.Text.Json.JsonElement> ProvisionStatus(string vm) => Wire.Decode<System.Text.Json.JsonElement>(
        await GuestExec(vm, @"C:\Windows\System32\cmd.exe", ["/c", @"type C:\Winix\provision.json"]));
    public async Task<string> GuestExec(string vm, string executable, string[] arguments)
    {
        store.Read<VmRecord>("vms", vm);
        var request = Wire.Encode(new { execute = "guest-exec", arguments = new Dictionary<string, object> { ["path"] = executable, ["arg"] = arguments, ["capture-output"] = true } });
        var started = Wire.Decode<System.Text.Json.JsonElement>(await Virsh("qemu-agent-command", "winix-" + vm, request));
        var pid = started.GetProperty("return").GetProperty("pid").GetInt32();
        for (var i = 0; i < 30; i++)
        {
            var result = Wire.Decode<System.Text.Json.JsonElement>(await Virsh("qemu-agent-command", "winix-" + vm, Wire.Encode(new { execute = "guest-exec-status", arguments = new { pid } }))).GetProperty("return");
            if (result.GetProperty("exited").GetBoolean())
            {
                if (result.GetProperty("exitcode").GetInt32() != 0)
                    throw new WinixException("guest_exec_failed", result.TryGetProperty("err-data", out var error) ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(error.GetString()!)) : "Guest command failed.");
                return result.TryGetProperty("out-data", out var output) ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(output.GetString()!)) : "";
            }
            await Task.Delay(500);
        }
        throw new WinixException("guest_exec_timeout", "QEMU guest agent command timed out.");
    }
}
