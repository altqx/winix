using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Winix;

namespace Winix.Guest;

internal sealed class JobEngine : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, CancellationTokenSource> running = [];
    public string? Active { get { lock (gate) return running.Keys.FirstOrDefault(); } }
    private static string DirectoryFor(string id) => Path.Combine(Program.Root, "jobs", Paths.Id(id));
    public JobEngine()
    {
        foreach (var path in Directory.GetFiles(Path.Combine(Program.Root, "jobs"), "record.json", SearchOption.AllDirectories))
        {
            var old = Wire.Decode<JobRecord>(File.ReadAllText(path));
            if (!old.Terminal) Save(old with { State = JobState.Interrupted, FinishedAt = DateTimeOffset.UtcNow, Error = "Worker restarted; job was not automatically rerun." });
        }
    }
    private static void Save(JobRecord record) => StateStore.Atomic(Path.Combine(DirectoryFor(record.Id), "record.json"), Wire.Encode(record));
    public JobRecord Status(string id)
    {
        var file = Path.Combine(DirectoryFor(id), "record.json");
        return File.Exists(file) ? Wire.Decode<JobRecord>(StateStore.ReadShared(file)) : throw new WinixException("job_not_found", id);
    }
    public JobRecord Start(JobRequest request)
    {
        Paths.Id(request.Id); Paths.Id(request.Workspace); request.Task.Validate();
        lock (gate)
        {
            if (File.Exists(Path.Combine(DirectoryFor(request.Id), "record.json"))) return Status(request.Id);
            if (running.Count > 0) throw new WinixException("busy", "A job is already running.");
            var root = Path.Combine(Program.Root, "work", request.Workspace);
            if (!Directory.Exists(root)) throw new WinixException("workspace_missing", "Synchronize sources before starting a job.");
            Paths.Under(root, request.Task.WorkingDirectory, true);
            Directory.CreateDirectory(DirectoryFor(request.Id));
            var record = new JobRecord(request.Id, request.Workspace, JobState.Queued, DateTimeOffset.UtcNow);
            Save(record);
            File.WriteAllText(Path.Combine(DirectoryFor(request.Id), "request.json"), Wire.Encode(request));
            var cancel = new CancellationTokenSource(); running.Add(request.Id, cancel);
            _ = Task.Run(() => Execute(request, record, cancel));
            return record;
        }
    }
    public JobRecord Cancel(string id)
    {
        lock (gate) { if (running.TryGetValue(id, out var cancel)) cancel.Cancel(); }
        return Status(id);
    }
    private async Task Execute(JobRequest request, JobRecord record, CancellationTokenSource cancelled)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(request.Task.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancelled.Token, timeout.Token);
        var dir = DirectoryFor(request.Id);
        try
        {
            Save(record with { State = JobState.Running });
            File.WriteAllText(Path.Combine(dir, "invoke.ps1"), Wrapper, new UTF8Encoding(true));
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true, WorkingDirectory = Paths.Under(Path.Combine(Program.Root, "work", request.Workspace), request.Task.WorkingDirectory, true) };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(dir, "invoke.ps1"), "-RequestFile", Path.Combine(dir, "request.json") }) start.ArgumentList.Add(arg);
            foreach (var (key, value) in request.Task.Environment) start.Environment[key] = value;
            using var process = new Process { StartInfo = start };
            using var job = new WindowsJob();
            process.Start(); job.Assign(process);
            await using var stream = new FileStream(Path.Combine(dir, "output.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            using var logGate = new SemaphoreSlim(1);
            async Task Pump(StreamReader source, string name)
            {
                while (await source.ReadLineAsync() is { } line)
                {
                    await logGate.WaitAsync();
                    try { await writer.WriteLineAsync($"[{name}] {line}"); } finally { logGate.Release(); }
                }
            }
            var output = Pump(process.StandardOutput, "out"); var errors = Pump(process.StandardError, "err");
            try { await process.WaitForExitAsync(linked.Token); }
            catch (OperationCanceledException) { job.Terminate(); await process.WaitForExitAsync(); throw; }
            finally { if (process.HasExited) job.Terminate(); await Task.WhenAll(output, errors); }
            record = record with { State = process.ExitCode == 0 ? JobState.Succeeded : JobState.Failed, ExitCode = process.ExitCode };
        }
        catch (OperationCanceledException) { record = record with { State = cancelled.IsCancellationRequested ? JobState.Cancelled : JobState.TimedOut }; }
        catch (Exception e) { record = record with { State = JobState.Failed, Error = e.Message }; }
        finally
        {
            try { Collect(request); }
            catch (Exception e) { record = record with { State = JobState.Failed, Error = $"Artifact collection failed: {e.Message}" }; }
            lock (gate)
            {
                Save(record with { FinishedAt = DateTimeOffset.UtcNow });
                running.Remove(request.Id);
            }
            cancelled.Dispose();
        }
    }
    private static void Collect(JobRequest request)
    {
        var source = Path.Combine(Program.Root, "work", request.Workspace);
        var target = Path.Combine(DirectoryFor(request.Id), "artifacts"); Directory.CreateDirectory(target);
        foreach (var relative in request.Task.Artifacts)
        {
            var path = Paths.Under(source, relative); var dest = Path.Combine(target, relative);
            if (File.Exists(path)) { Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(path, dest, true); }
            else if (Directory.Exists(path)) CopySafe(path, dest);
        }
    }
    private static void CopySafe(string from, string to)
    {
        if (File.GetAttributes(from).HasFlag(FileAttributes.ReparsePoint)) throw new WinixException("symlink", from);
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new WinixException("symlink", file);
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.GetDirectories(from)) CopySafe(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
    public LogPage Logs(string id, long offset)
    {
        var state = Status(id);
        var path = Path.Combine(DirectoryFor(id), "output.log");
        return Utf8Logs.Read(path, offset, state.Terminal);
    }
    public object Export(string id)
    {
        if (!Status(id).Terminal) throw new WinixException("job_running", "Wait for completion before exporting artifacts.");
        var path = Path.Combine(Program.Root, "exports", id + ".zip");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.GetFiles(DirectoryFor(id), "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(DirectoryFor(id), file).Replace('\\', '/');
                    if (relative is "record.json" or "output.log" || relative.StartsWith("artifacts/")) archive.CreateEntryFromFile(file, relative);
                }
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new { file = id + ".zip", size = new FileInfo(path).Length };
    }
    public void Dispose() { lock (gate) foreach (var source in running.Values) source.Cancel(); }
    private const string Wrapper = """
param([string]$RequestFile)
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)
try {
  $r = Get-Content -Raw -Encoding UTF8 -LiteralPath $RequestFile | ConvertFrom-Json
  if ($r.task.visualStudio) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $vs = & $vswhere -latest -products '*' -property installationPath
    if (!$vs) { throw 'Visual Studio was not found.' }
    Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
    Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments "-arch=$($r.task.architecture) -host_arch=x64" | Out-Null
  }
  $global:LASTEXITCODE = 0
  if ($r.task.script) { & ([scriptblock]::Create($r.task.script)) }
  else { $a = @($r.task.arguments); & $r.task.executable @a }
  if ($LASTEXITCODE) { exit $LASTEXITCODE }
  exit 0
} catch { Write-Error $_ -ErrorAction Continue; exit 1 }
""";
}

internal sealed class WindowsJob : IDisposable
{
    private readonly IntPtr handle;
    public WindowsJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var info = new ExtendedLimit { BasicLimitInformation = new BasicLimit { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimit>())) throw new System.ComponentModel.Win32Exception();
    }
    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle)) { process.Kill(true); throw new System.ComponentModel.Win32Exception(); }
    }
    public void Terminate() => TerminateJobObject(handle, 1);
    public void Dispose() => CloseHandle(handle);
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit { public BasicLimit BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref ExtendedLimit info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
