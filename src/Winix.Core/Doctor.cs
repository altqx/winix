namespace Winix;

public static class Doctor
{
    public static async Task<List<Check>> Run(ICommandRunner runner, StateStore state, CancellationToken ct = default)
    {
        List<Check> checks = [new("linux-x64", OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64, "Linux x86_64 is the supported host.")];
        try { using var kvm = File.Open("/dev/kvm", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); checks.Add(new("kvm", true, "KVM accessible.")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { checks.Add(new("kvm", false, e.Message)); }
        foreach (var tool in new[] { "virsh", "qemu-system-x86_64", "qemu-img", "swtpm", "swtpm_setup", "xorriso", "ssh", "scp", "ssh-keygen", "curl", "git", "cp" })
        {
            var found = (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(p => File.Exists(Path.Combine(p, tool)));
            checks.Add(new(tool, found, found ? "Installed." : $"Install {tool}; see docs/setup.md."));
        }
        try { checks.Add(new("libvirt", true, await runner.Checked("virsh", ["-c", VmManager.Connection, "uri"], ct: ct))); }
        catch (WinixException e) { checks.Add(new("libvirt", false, e.Message)); }
        var firmware = VmManager.FindFirmware();
        checks.Add(new("uefi", firmware is not null, firmware ?? "Install edk2-ovmf or ovmf; or set WINIX_OVMF_CODE and WINIX_OVMF_VARS."));
        var free = new DriveInfo(Path.GetPathRoot(state.Root)!).AvailableFreeSpace;
        checks.Add(new("disk", free >= 80L * 1024 * 1024 * 1024, $"{free / (1024L * 1024 * 1024)} GiB available; require at least 80 GiB to provision."));
        var strays = StrayMcpHosts();
        checks.Add(new("mcp-hosts", strays.Count == 0, strays.Count == 0 ? "No orphaned winix mcp hosts." : $"Orphaned winix mcp hosts (client gone): PIDs {string.Join(", ", strays)}. Stop them with kill."));
        checks.Add(new("ssh-mux", true, GuestConnection.Multiplex ? $"SSH multiplexing on ({GuestConnection.ControlPath()})." : "SSH multiplexing disabled by WINIX_SSH_MUX=0."));
        return checks;
    }
    /// <summary>MCP hosts whose client exited without closing them: a <c>winix mcp</c> process reparented to PID 1.</summary>
    public static List<int> StrayMcpHosts()
    {
        List<int> strays = [];
        if (!Directory.Exists("/proc")) return strays;
        foreach (var dir in Directory.GetDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == System.Environment.ProcessId) continue;
            try
            {
                var args = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (!args.Contains("mcp") || !args.Any(a => Path.GetFileName(a) is "winix" or "winix.dll")) continue;
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                if (int.Parse(fields[1]) == 1) strays.Add(pid);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException) { }
        }
        return strays;
    }
}
