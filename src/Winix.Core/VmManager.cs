using System.Net;
using System.Net.Sockets;
using System.Text.Json;
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
    public static string Domain(VmRecord vm, Resources resources, string firmware, string? iso = null, string? drivers = null, string? seed = null, VmOptions? options = null)
    {
        options ??= new();
        XNamespace qemu = "http://libvirt.org/schemas/domain/qemu/1.0";
        var devices = new XElement("devices",
            new XElement("disk", new XAttribute("type", "file"), new XAttribute("device", "disk"),
                new XElement("driver", new XAttribute("name", "qemu"), new XAttribute("type", "qcow2")),
                new XElement("source", new XAttribute("file", Path.Combine(vm.Directory, "disk.qcow2"))),
                new XElement("target", new XAttribute("dev", "vda"), new XAttribute("bus", "virtio")),
                new XElement("boot", new XAttribute("order", "2"))),
            new XElement("controller", new XAttribute("type", "sata"), new XAttribute("index", "0")),
            new XElement("graphics", new XAttribute("type", "spice"), new XAttribute("autoport", "yes"), new XElement("listen", new XAttribute("type", "address"), new XAttribute("address", "127.0.0.1"))));
        // One video device per monitor. VGA cannot be secondary, so several displays always use QXL.
        var video = options.Displays > 1 && options.Video == "vga" ? "qxl" : options.Video;
        for (var head = 0; head < options.Displays; head++)
        {
            var model = new XElement("model", new XAttribute("type", video), new XAttribute("heads", "1"));
            if (video == "qxl") { model.Add(new XAttribute("ram", "65536"), new XAttribute("vram", "65536"), new XAttribute("vgamem", "16384")); }
            else if (video == "vga") model.Add(new XAttribute("vram", "65536"));
            if (head == 0 && options.Displays > 1) model.Add(new XAttribute("primary", "yes"));
            devices.Add(new XElement("video", model));
        }
        if (options.HasAudioBackend)
        {
            // ICH9 HDA gives Windows a real WASAPI endpoint; the backend decides whether anything is audible.
            devices.Add(new XElement("sound", new XAttribute("model", "ich9"), new XElement("audio", new XAttribute("id", "1"))));
            devices.Add(new XElement("audio", new XAttribute("id", "1"), new XAttribute("type", options.Audio)));
        }
        devices.Add(new XElement("input", new XAttribute("type", "tablet"), new XAttribute("bus", "usb")),
            new XElement("controller", new XAttribute("type", "usb"), new XAttribute("model", "qemu-xhci")),
            new XElement("channel", new XAttribute("type", "unix"), new XElement("target", new XAttribute("type", "virtio"), new XAttribute("name", "org.qemu.guest_agent.0"))),
            new XElement("tpm", new XAttribute("model", "tpm-crb"), new XElement("backend", new XAttribute("type", "emulator"), new XAttribute("version", "2.0"),
                new XElement("source", new XAttribute("type", "dir"), new XAttribute("path", Path.Combine(vm.Directory, "tpm"))))));
        foreach (var usb in options.Usb.Where(VmOptions.IsHostUsb))
            devices.Add(new XElement("hostdev", new XAttribute("mode", "subsystem"), new XAttribute("type", "usb"), new XAttribute("managed", "yes"),
                new XElement("source", new XElement("vendor", new XAttribute("id", "0x" + usb[..4])), new XElement("product", new XAttribute("id", "0x" + usb[5..])))));
        var cds = new[] { iso, drivers, seed };
        for (var i = 0; i < cds.Length; i++)
            if (cds[i] is { } path) devices.Add(new XElement("disk", new XAttribute("type", "file"), new XAttribute("device", "cdrom"),
                new XElement("driver", new XAttribute("name", "qemu"), new XAttribute("type", "raw")),
                new XElement("source", new XAttribute("file", path)), new XElement("target", new XAttribute("dev", "sd" + (char)('a' + i)), new XAttribute("bus", "sata")),
                new XElement("readonly"), i == 0 ? new XElement("boot", new XAttribute("order", "1")) : null));
        var commandline = new XElement(qemu + "commandline",
            new XElement(qemu + "arg", new XAttribute("value", "-netdev")), new XElement(qemu + "arg", new XAttribute("value", $"user,id=net0,hostfwd=tcp:127.0.0.1:{vm.SshPort}-:22")),
            new XElement(qemu + "arg", new XAttribute("value", "-device")), new XElement(qemu + "arg", new XAttribute("value", "virtio-net-pci,netdev=net0,bus=pcie.0,addr=0x10")));
        var index = 0;
        foreach (var usb in options.Usb.Where(u => !VmOptions.IsHostUsb(u)))
        {
            var spec = $"{usb},id=winix-boot-usb{index++}" + (usb == "usb-audio" && options.HasAudioBackend ? ",audiodev=audio1" : "");
            commandline.Add(new XElement(qemu + "arg", new XAttribute("value", "-device")), new XElement(qemu + "arg", new XAttribute("value", spec)));
        }
        return new XDocument(new XElement("domain", new XAttribute("type", "kvm"), new XAttribute(XNamespace.Xmlns + "qemu", qemu),
            new XElement("name", "winix-" + vm.Name), new XElement("uuid", vm.Uuid),
            new XElement("memory", new XAttribute("unit", "GiB"), resources.MemoryGiB), new XElement("vcpu", resources.Cpus),
            new XElement("os", new XElement("type", new XAttribute("arch", "x86_64"), new XAttribute("machine", "q35"), "hvm"),
                new XElement("loader", new XAttribute("readonly", "yes"), new XAttribute("type", "pflash"), firmware),
                new XElement("nvram", Path.Combine(vm.Directory, "nvram.fd"))),
            new XElement("features", new XElement("acpi"), new XElement("apic"), new XElement("smm", new XAttribute("state", "on"))),
            new XElement("cpu", new XAttribute("mode", "host-passthrough")), new XElement("clock", new XAttribute("offset", "utc")),
            new XElement("on_reboot", "restart"), new XElement("on_poweroff", "destroy"), devices, commandline)).ToString();
    }
    public async Task<VmRecord> Up(string name, string image, Resources resources, bool disposable = false, VmOptions? options = null)
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
            store.Write("vms", name, vm);
        }
        if (vm.Image != image) throw new WinixException("image_mismatch", $"VM {name} uses {vm.Image}, not {image}.");
        var xml = Domain(vm, resources, FindFirmware() ?? throw new WinixException("firmware_missing", "Run winix doctor."), options: options);
        var definition = Path.Combine(directory, "domain.xml");
        var state = await Status(name);
        if (!File.Exists(definition) || File.ReadAllText(definition) != xml)
        {
            // Resource and device options live in the project config; the domain follows them whenever the VM is off.
            if (state is not ("shut off" or "undefined"))
                throw new WinixException("vm_definition_changed", $"VM {name} is running with different resources or devices than the config now requests. Run vm down, then vm up.");
            File.WriteAllText(definition, xml);
            if (state == "shut off") await Virsh("define", definition);
        }
        if (await Status(name) == "undefined") await Virsh("define", definition);
        if (await Status(name) == "shut off") await Virsh("start", "winix-" + name);
        return vm;
    }
    public async Task Down(string name, bool force = false)
    {
        using var guard = store.Lock();
        var vm = store.Read<VmRecord>("vms", name);
        var state = await Status(name);
        if (state is "shut off" or "undefined") return;
        var guest = new GuestConnection(vm, runner);
        if (!force && File.Exists(Path.Combine(vm.Directory, "hostkey.pub")))
        {
            var health = await guest.Call("health", new { });
            if (health.TryGetProperty("activeJob", out var active) && active.ValueKind != JsonValueKind.Null)
                throw new WinixException("busy", $"Job {active.GetString()} is running. Wait or cancel it before shutdown.");
        }
        await guest.Disconnect();
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
    public async Task<JsonElement> ProvisionStatus(string vm) => Wire.Decode<JsonElement>(
        await GuestExec(vm, @"C:\Windows\System32\cmd.exe", ["/c", @"type C:\Winix\provision.json"]));
    public async Task<string> GuestExec(string vm, string executable, string[] arguments, TimeSpan? timeout = null)
    {
        store.Read<VmRecord>("vms", vm);
        var request = Wire.Encode(new { execute = "guest-exec", arguments = new Dictionary<string, object> { ["path"] = executable, ["arg"] = arguments, ["capture-output"] = true } });
        var started = Wire.Decode<JsonElement>(await Virsh("qemu-agent-command", "winix-" + vm, request));
        var pid = started.GetProperty("return").GetProperty("pid").GetInt32();
        var polls = (int)((timeout ?? TimeSpan.FromSeconds(15)).TotalMilliseconds / 500);
        for (var i = 0; i < polls; i++)
        {
            var result = Wire.Decode<JsonElement>(await Virsh("qemu-agent-command", "winix-" + vm, Wire.Encode(new { execute = "guest-exec-status", arguments = new { pid } }))).GetProperty("return");
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

    // ---- Hot-plug (QMP device_add/device_del). Winix records what it added in the VM directory. ----
    private HostInput Qmp => new(store, runner, name => new GuestConnection(store.Read<VmRecord>("vms", name), runner));
    private string DevicesFile(string vm) => Path.Combine(store.Dir("vms", vm), "devices.json");
    private List<HotplugDevice> Hotplugged(string vm) => File.Exists(DevicesFile(vm)) ? Wire.Decode<List<HotplugDevice>>(File.ReadAllText(DevicesFile(vm))) : [];
    public async Task<HotplugDevice> DeviceAdd(string vm, string driver, string? id, Dictionary<string, string> properties, CancellationToken ct = default)
    {
        store.Read<VmRecord>("vms", vm);
        if (!VmOptions.EmulatedUsb.Contains(driver) && driver != "usb-host") throw new WinixException("invalid_device", $"Hot-pluggable drivers: {string.Join(", ", VmOptions.EmulatedUsb)}, usb-host (with vendorid/productid).");
        if (driver == "usb-host" && !(properties.ContainsKey("vendorid") && properties.ContainsKey("productid"))) throw new WinixException("invalid_device", "usb-host needs vendorid and productid properties.");
        id = Paths.Id(id ?? $"winix-{driver}-{Guid.NewGuid().ToString("N")[..6]}");
        var arguments = new Dictionary<string, object> { ["driver"] = driver, ["id"] = id };
        foreach (var (key, value) in properties) arguments[Paths.Id(key)] = value;
        if (driver == "usb-audio" && !properties.ContainsKey("audiodev")) arguments["audiodev"] = "audio1";
        await Qmp.Qmp(vm, new { execute = "device_add", arguments }, ct);
        var record = new HotplugDevice(id, driver, properties, DateTimeOffset.UtcNow);
        var list = Hotplugged(vm); list.RemoveAll(d => d.Id == id); list.Add(record);
        StateStore.Atomic(DevicesFile(vm), Wire.Encode(list));
        return record;
    }
    public async Task<object> DeviceRemove(string vm, string id, CancellationToken ct = default)
    {
        store.Read<VmRecord>("vms", vm); Paths.Id(id);
        await Qmp.Qmp(vm, new { execute = "device_del", arguments = new { id } }, ct);
        var list = Hotplugged(vm); var removed = list.RemoveAll(d => d.Id == id) > 0;
        StateStore.Atomic(DevicesFile(vm), Wire.Encode(list));
        return new { vm, id, removed = true, tracked = removed };
    }
    public async Task<object> DeviceList(string vm, CancellationToken ct = default)
    {
        store.Read<VmRecord>("vms", vm);
        var peripherals = await Qmp.Qmp(vm, new { execute = "qom-list", arguments = new { path = "/machine/peripheral" } }, ct);
        var live = peripherals.ValueKind == JsonValueKind.Array
            ? peripherals.EnumerateArray().Select(p => new { name = p.GetProperty("name").GetString(), type = p.GetProperty("type").GetString() }).Where(p => !p.type!.StartsWith("link<")).ToList() : [];
        return new { vm, hotplugged = Hotplugged(vm), peripherals = live };
    }
}
