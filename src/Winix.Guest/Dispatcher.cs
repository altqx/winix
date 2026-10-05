using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Winix;

namespace Winix.Guest;

internal sealed class Dispatcher : IDisposable
{
    private readonly JobEngine jobs = new();
    private readonly Desktop desktop = new();
    private readonly SemaphoreSlim mutations = new(1, 1);
    private static readonly string[] ElevatedVerbs = ["driver-add"];
    public async Task<object> Call(GuestRequest request)
    {
        var a = request.Arguments;
        var id = a.TryGetProperty("id", out var p) ? p.GetString()! : "";
        switch (request.Method)
        {
            case "health":
                return new { version = "0.2.0", user = System.Environment.UserName, interactive = System.Environment.UserInteractive,
                    desktopReady = Desktop.IsUnlocked(), activeJob = jobs.Active, dpiAware = Program.DpiAware, elevationAvailable = Elevation.Available,
                    features = new[] { "windows", "focus", "state", "batch", "monitors", "keys-scancode", "launch-env", "push-pull", "dump", "elevated", "incremental-sync" },
                    toolchain = File.Exists(@"C:\Winix\toolchain.json") ? Wire.Decode<JsonElement>(File.ReadAllText(@"C:\Winix\toolchain.json")) : Wire.Element(new { }) };
            case "job.status": return jobs.Status(id);
            case "job.logs": return jobs.Logs(id, a.TryGetProperty("offset", out var offset) ? offset.GetInt64() : 0);
            case "job.cancel": return jobs.Cancel(id);
            case "job.export": return jobs.Export(id);
            case "process.kill":
            {
                using var process = Process.GetProcessById(a.GetProperty("pid").GetInt32());
                var name = process.ProcessName; process.Kill(!(a.TryGetProperty("single", out var single) && single.GetBoolean()));
                return new { ok = true, pid = process.Id, name };
            }
            case "process.dump": return ProcessTools.Dump(a.GetProperty("pid").GetInt32(), a.TryGetProperty("full", out var full) && full.GetBoolean(), !(a.TryGetProperty("stacks", out var stacks) && !stacks.GetBoolean()));
            case "system.devices": return await ProcessTools.Devices();
            case "file.put": return FilePut(a);
            case "file.get": return FileGet(a);
            case "file.remove":
            {
                var name = Paths.Relative(a.GetProperty("file").GetString()!);
                if (!name.StartsWith("pull-")) throw new WinixException("invalid_args", "Only pull exports can be removed.");
                var path = Paths.Under(Path.Combine(Program.Root, "exports"), name);
                if (File.Exists(path)) File.Delete(path);
                return new { ok = true };
            }
            case "elevated.run":
            {
                var kind = a.GetProperty("kind").GetString() ?? "";
                if (!ElevatedVerbs.Contains(kind)) throw new WinixException("invalid_args", $"Elevated verbs: {string.Join(", ", ElevatedVerbs)}.");
                var dir = Path.Combine(Elevation.Root, "verb-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "request.json"), Wire.Encode(new { kind, arguments = a.GetProperty("arguments") }));
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var result = await Elevation.Submit(dir, timeout.Token);
                if (!result.GetProperty("ok").GetBoolean()) throw new WinixException(result.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString()! : "elevated_failed",
                    (result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null) + (result.TryGetProperty("output", out var output) ? "\n" + output.GetString() : ""));
                return result;
            }
        }
        await mutations.WaitAsync();
        try
        {
            if (request.Method == "job.start") return jobs.Start(Wire.Decode<JobRequest>(a.GetRawText()));
            if (jobs.Active is not null) throw new WinixException("busy", $"Job {jobs.Active} owns the desktop/workspace. Wait or cancel it before mutation.");
            if (request.Method == "sync")
            {
                var path = Paths.Under(Path.Combine(Program.Root, "inbox"), a.GetProperty("archive").GetString()!);
                try { return WorkspaceSync.Apply(path, Path.Combine(Program.Root, "work")); }
                finally { File.Delete(path); }
            }
            if (request.Method.StartsWith("ui.")) return await desktop.Call(request.Method[3..], a);
            throw new WinixException("unknown_method", request.Method);
        }
        finally { mutations.Release(); }
    }

    private static string Expand(string guestPath)
    {
        var expanded = System.Environment.ExpandEnvironmentVariables(Paths.GuestPath(guestPath));
        if (expanded.Contains('%')) throw new WinixException("invalid_guest_path", $"Unresolved environment variable in {guestPath}");
        var full = Path.GetFullPath(expanded);
        if (full.StartsWith(Path.Combine(Program.Root, "agent"), StringComparison.OrdinalIgnoreCase)) throw new WinixException("forbidden_path", "The worker's own files cannot be replaced.");
        return full;
    }
    private static string Sha256(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static object FilePut(JsonElement a)
    {
        var inbox = Paths.Under(Path.Combine(Program.Root, "inbox"), a.GetProperty("inbox").GetString()!);
        try
        {
            var destination = Expand(a.GetProperty("destination").GetString()!);
            var directory = a.GetProperty("directory").GetBoolean(); var expected = a.GetProperty("sha256").GetString();
            var actual = Sha256(inbox);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new WinixException("checksum_mismatch", "Upload was corrupted in transit.");
            long size;
            if (directory)
            {
                Directory.CreateDirectory(destination);
                ZipFile.ExtractToDirectory(inbox, destination, true);
                size = 0;
                foreach (var file in Directory.GetFiles(destination, "*", SearchOption.AllDirectories)) { File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly); size += new FileInfo(file).Length; }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination)) File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
                File.Move(inbox, destination, true);
                size = new FileInfo(destination).Length;
            }
            return new TransferResult(a.GetProperty("inbox").GetString()!, destination, actual, size, directory);
        }
        finally { if (File.Exists(inbox)) File.Delete(inbox); }
    }
    private static object FileGet(JsonElement a)
    {
        var source = Expand(a.GetProperty("path").GetString()!);
        var directory = Directory.Exists(source);
        if (!directory && !File.Exists(source)) throw new WinixException("not_found", source);
        var name = "pull-" + Guid.NewGuid().ToString("N") + (directory ? ".zip" : ".bin");
        var export = Path.Combine(Program.Root, "exports", name);
        if (directory) ZipFile.CreateFromDirectory(source, export, CompressionLevel.Fastest, false);
        else
        {
            // Copy through a shared-read stream so files an app still holds open can be fetched.
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = File.Create(export); input.CopyTo(output);
        }
        return new { file = name, sha256 = Sha256(export), size = new FileInfo(export).Length, directory, source };
    }
    public void Dispose() { desktop.Dispose(); jobs.Dispose(); mutations.Dispose(); }
}

internal static class ProcessTools
{
    public static object Dump(int pid, bool full, bool stacks)
    {
        using var process = Process.GetProcessById(pid);
        var dir = Program.Dir("dumps");
        var baseName = Path.Combine(dir, $"{string.Concat(process.ProcessName.Where(char.IsLetterOrDigit))}-{pid}-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        var dumpPath = baseName + ".dmp";
        using (var file = new FileStream(dumpPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            // MiniDumpWithDataSegs|WithHandleData|WithIndirectlyReferencedMemory|WithThreadInfo; full adds MiniDumpWithFullMemory.
            uint type = 0x1 | 0x4 | 0x40 | 0x1000; if (full) type |= 0x2;
            if (!MiniDumpWriteDump(process.Handle, (uint)pid, file.SafeFileHandle.DangerousGetHandle(), type, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "MiniDumpWriteDump failed");
        }
        string? stackFile = null; string? stackError = null;
        if (stacks)
        {
            var cdb = new[] { @"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe", @"C:\Program Files\Windows Kits\10\Debuggers\x64\cdb.exe" }.FirstOrDefault(File.Exists);
            if (cdb is null) stackError = "cdb.exe not found; install Debugging Tools for Windows (Windows SDK) in the image.";
            else
            {
                stackFile = baseName + ".stacks.txt";
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                // Noninvasive attach: the hung process is left exactly as it was.
                var result = new CommandRunner().Run(cdb, ["-pv", "-p", pid.ToString(), "-lines", "-c", "~*kv;qd"], cancellationToken: timeout.Token).GetAwaiter().GetResult();
                File.WriteAllText(stackFile, result.Stdout + result.Stderr, new UTF8Encoding(false));
                if (result.ExitCode != 0) stackError = $"cdb exited {result.ExitCode}";
            }
        }
        return new { pid, name = process.ProcessName, dump = dumpPath, size = new FileInfo(dumpPath).Length, stacks = stackFile, stacksError = stackError, hint = "Fetch with winix pull <path> <local>." };
    }
    public static async Task<object> Devices()
    {
        const string script = """
            $ErrorActionPreference = 'Continue'
            $pnp = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.Class -in 'Display','Monitor','Media','AudioEndpoint','USB','HIDClass','SoftwareDevice','Mouse','Keyboard' } | Select-Object Class,FriendlyName,Status,InstanceId,Problem
            $video = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,Status,PNPDeviceID
            $audio = Get-CimInstance Win32_SoundDevice -ErrorAction SilentlyContinue | Select-Object Name,Status,Manufacturer,PNPDeviceID
            @{ pnp = @($pnp); video = @($video); audio = @($audio) } | ConvertTo-Json -Depth 4 -Compress
            """;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var output = await new CommandRunner().Checked("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], ct: timeout.Token);
        var inventory = Wire.Decode<JsonElement>(output);
        return new { monitors = Desktop.Monitors(), pnp = inventory.GetProperty("pnp"), video = inventory.GetProperty("video"), audio = inventory.GetProperty("audio") };
    }
    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr process, uint pid, IntPtr file, uint type, IntPtr exception, IntPtr userStream, IntPtr callback);
}
