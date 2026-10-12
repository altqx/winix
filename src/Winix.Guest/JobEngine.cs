using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Winix;

namespace Winix.Guest;

internal sealed class JobEngine : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, RunningJob> running = [];
    public string? Active { get { lock (gate) return running.Keys.FirstOrDefault(); } }
    private static string DirectoryFor(string id) => Path.Combine(Program.Root, "jobs", Paths.Id(id));
    /// <summary>How long the output pumps may run on after the wrapper exits. A child that inherited the pipes (a detached app) keeps them open forever.</summary>
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(5);

    /// <summary>A job holding the guest, from start through artifact collection and teardown.</summary>
    private sealed class RunningJob(JobRequest request)
    {
        public JobRequest Request { get; } = request;
        public CancellationTokenSource Cancel { get; } = new();
        /// <summary><c>cancelled</c> or <c>timed_out</c> once a stop was asked for; persisted as the job's <c>cancel</c> file.</summary>
        public string? Reason;
        private readonly object sync = new();
        private Process? process; private WindowsJob? job; private bool tearingDown;
        public void Attach(Process p, WindowsJob? j) { lock (sync) { process = p; job = j; } }
        /// <summary>From here on <see cref="Kill"/> leaves the wrapper alone: teardown always gets its own timeout, even after a forced stop.</summary>
        public void BeginTeardown() { lock (sync) tearingDown = true; }
        /// <summary>Kills the task's wrapper and everything it started, detached or not.</summary>
        public void Kill()
        {
            lock (sync)
            {
                if (tearingDown) return;
                try { job?.Terminate(); } catch (Exception) { }
                try { if (process is { HasExited: false }) process.Kill(true); } catch (Exception) { /* exited or disposed meanwhile */ }
            }
        }
    }

    public JobEngine()
    {
        foreach (var path in Directory.GetFiles(Path.Combine(Program.Root, "jobs"), "record.json", SearchOption.AllDirectories))
        {
            var old = Wire.Decode<JobRecord>(File.ReadAllText(path));
            if (old.Terminal) continue;
            // A stop that was asked for before the restart keeps its meaning; anything else was interrupted.
            var marker = Path.Combine(DirectoryFor(old.Id), "cancel");
            var reason = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "interrupted";
            var state = reason switch { "timed_out" => JobState.TimedOut, "cancelled" => JobState.Cancelled, _ => JobState.Interrupted };
            var final = old with { State = state, Error = state == JobState.Interrupted ? "Worker restarted; job was not automatically rerun." : "Worker restarted while the job was being stopped." };
            var requestFile = Path.Combine(DirectoryFor(old.Id), "request.json");
            var request = File.Exists(requestFile) ? Wire.Decode<JobRequest>(File.ReadAllText(requestFile)) : null;
            if (request is not null && !string.IsNullOrWhiteSpace(request.Task.Teardown)) Recover(request, final, reason);
            else Save(final with { FinishedAt = DateTimeOffset.UtcNow });
        }
    }
    /// <summary>Runs the teardown of a job the previous worker left behind. The guest stays busy until it finishes.</summary>
    private void Recover(JobRequest request, JobRecord final, string reason)
    {
        var run = new RunningJob(request);
        lock (gate) running[request.Id] = run;
        Save(final with { State = JobState.Running, Error = $"Worker restarted; running teardown ({reason})." });
        _ = Task.Run(async () =>
        {
            var teardown = await RunTeardown(run, reason);
            lock (gate) { Save(final with { FinishedAt = DateTimeOffset.UtcNow, Teardown = teardown }); running.Remove(request.Id); }
        });
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
            if (!Directory.Exists(root)) throw new WinixException("workspace_missing", $"Synchronize sources into workspace {request.Workspace} before starting a job.");
            Paths.Under(root, request.Task.WorkingDirectory, true);
            if (request.Task.ScriptFile is { } scriptFile && !File.Exists(Paths.Under(root, scriptFile))) throw new WinixException("script_missing", $"scriptFile not found in the workspace: {scriptFile}");
            Directory.CreateDirectory(DirectoryFor(request.Id));
            var record = new JobRecord(request.Id, request.Workspace, JobState.Queued, DateTimeOffset.UtcNow, Label: request.Label, Parameters: request.Parameters,
                Detached: request.Task.Detach, Elevated: request.Task.Elevated, TimeoutSeconds: request.Task.TimeoutSeconds, Source: SyncedSource(request.Workspace));
            Save(record);
            File.WriteAllText(Path.Combine(DirectoryFor(request.Id), "request.json"), Wire.Encode(request));
            var run = new RunningJob(request); running.Add(request.Id, run);
            _ = Task.Run(() => Execute(run, record));
            return record;
        }
    }
    /// <summary>The commit the workspace was last synchronized from, as recorded by <c>sync</c>.</summary>
    private static SourceInfo? SyncedSource(string workspace)
    {
        var state = Path.Combine(Program.Root, "work", workspace + ".manifest.json");
        try { return File.Exists(state) ? Wire.Decode<SyncManifest>(File.ReadAllText(state)).Source : null; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or WinixException) { return null; }
    }
    /// <summary>
    /// Stops a job. The first request cancels cooperatively (the task tree is terminated, teardown runs); <paramref name="force"/>
    /// also kills whatever wrapper is current, including processes a detached task left running. <paramref name="reason"/>
    /// <c>timed_out</c> marks a stop the host enforced because the job outlived its timeout.
    /// </summary>
    public JobRecord Cancel(string id, bool force = false, string? reason = null)
    {
        RunningJob? run;
        lock (gate)
        {
            if (running.TryGetValue(id, out run))
            {
                run.Reason ??= reason == "timed_out" ? "timed_out" : "cancelled";
                File.WriteAllText(Path.Combine(DirectoryFor(id), "cancel"), run.Reason);
                run.Cancel.Cancel();
            }
        }
        if (run is not null && force) run.Kill();
        return Status(id);
    }
    private async Task Execute(RunningJob run, JobRecord record)
    {
        var request = run.Request;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(request.Task.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Cancel.Token, timeout.Token);
        var dir = DirectoryFor(request.Id);
        string? stopped = null;
        try
        {
            record = record with { State = JobState.Running, StartedAt = DateTimeOffset.UtcNow };
            Save(record);
            int exitCode;
            if (request.Task.Elevated)
            {
                // Handed to the provisioned SYSTEM task; the log lands in this job's directory as usual.
                var elevatedDir = Path.Combine(Elevation.Root, request.Id); Directory.CreateDirectory(elevatedDir);
                File.WriteAllText(Path.Combine(elevatedDir, "request.json"), Wire.Encode(new { kind = "task", request, logPath = Path.Combine(dir, "output.log") }));
                var result = await Elevation.Submit(elevatedDir, linked.Token);
                if (!result.GetProperty("ok").GetBoolean()) throw new WinixException(result.TryGetProperty("code", out var code) ? code.GetString()! : "elevated_failed", result.TryGetProperty("error", out var error) ? error.GetString()! : "Elevated task failed.");
                exitCode = result.GetProperty("exitCode").GetInt32();
            }
            else exitCode = await RunTask(request, dir, Path.Combine(dir, "request.json"), Path.Combine(dir, "output.log"), request.Task.Detach, linked.Token, run.Attach);
            record = record with { State = exitCode == 0 ? JobState.Succeeded : JobState.Failed, ExitCode = exitCode };
        }
        catch (OperationCanceledException)
        {
            string reason; lock (gate) reason = run.Reason ?? "timed_out";
            stopped = reason;
            record = record with { State = reason == "cancelled" ? JobState.Cancelled : JobState.TimedOut };
            if (reason == "timed_out") record = record with { Error = $"Exceeded the {request.Task.TimeoutSeconds} s timeout." };
        }
        catch (Exception e) { record = record with { State = JobState.Failed, Error = e.Message }; }
        finally
        {
            try { Collect(request); }
            catch (Exception e) { record = record with { State = JobState.Failed, Error = $"Artifact collection failed: {e.Message}" }; }
            if (stopped is not null && !string.IsNullOrWhiteSpace(request.Task.Teardown)) record = record with { Teardown = await RunTeardown(run, stopped) };
            lock (gate)
            {
                Save(record with { FinishedAt = DateTimeOffset.UtcNow });
                running.Remove(request.Id);
            }
        }
    }
    /// <summary>The task's <c>teardown:</c> script, appended to the job log under <c>[teardown:…]</c> and bounded only by its own timeout.</summary>
    private static async Task<TeardownResult> RunTeardown(RunningJob run, string reason)
    {
        var request = run.Request; var task = request.Task; var dir = DirectoryFor(request.Id);
        var environment = new Dictionary<string, string>(task.Environment) { ["WINIX_TEARDOWN_REASON"] = reason };
        var teardown = request with { Task = new TaskSpec { Script = task.Teardown, WorkingDirectory = task.WorkingDirectory, VisualStudio = false,
            Architecture = task.Architecture, Environment = environment, TimeoutSeconds = task.TeardownTimeoutSeconds } };
        var file = Path.Combine(dir, "teardown.json");
        run.BeginTeardown();
        try
        {
            File.WriteAllText(file, Wire.Encode(teardown));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(task.TeardownTimeoutSeconds));
            return new(reason, await RunTask(teardown, dir, file, Path.Combine(dir, "output.log"), false, timeout.Token, run.Attach, "teardown"));
        }
        catch (OperationCanceledException) { return new(reason, null, $"Teardown exceeded {task.TeardownTimeoutSeconds} s and was stopped."); }
        catch (Exception e) { return new(reason, null, e.Message); }
    }
    /// <summary>
    /// Runs the task's PowerShell wrapper. Without <paramref name="detach"/> the process tree lives in a kill-on-close
    /// job object; with it, processes the task starts (an app under test) outlive the task and only the wrapper is killed on cancel.
    /// With <paramref name="tag"/> (teardown) the log is appended to and its lines are tagged.
    /// </summary>
    internal static async Task<int> RunTask(JobRequest request, string scratchDir, string requestFile, string logPath, bool detach, CancellationToken ct,
        Action<Process, WindowsJob?>? started = null, string? tag = null)
    {
        var script = Path.Combine(scratchDir, tag is null ? "invoke.ps1" : $"invoke-{tag}.ps1");
        File.WriteAllText(script, Wrapper, new UTF8Encoding(true));
        var workspace = Path.Combine(Program.Root, "work", request.Workspace);
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true, WorkingDirectory = Paths.Under(workspace, request.Task.WorkingDirectory, true) };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-RequestFile", requestFile, "-WorkspaceRoot", workspace, "-CacheDir", Program.Dir("cache") }) start.ArgumentList.Add(arg);
        foreach (var (key, value) in request.Task.Environment) start.Environment[key] = value;
        start.Environment["WINIX_JOB_ID"] = request.Id; start.Environment["WINIX_WORKSPACE"] = workspace;
        if (request.Label is not null) start.Environment["WINIX_JOB_LABEL"] = request.Label;
        start.Environment["WINIX_ARGS"] = Wire.Encode(request.Parameters?.Args ?? []);
        SharedCaches(start.Environment);
        using var process = new Process { StartInfo = start };
        using var job = detach ? null : new WindowsJob();
        process.Start(); job?.Assign(process); started?.Invoke(process, job);
        // Lets a worker restart from outside (winix guest restart) find and end the tree.
        try { File.WriteAllText(Path.Combine(scratchDir, tag is null ? "pid" : $"pid-{tag}"), process.Id.ToString()); } catch (IOException) { }
        await using var stream = new FileStream(logPath, tag is null ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        using var logGate = new SemaphoreSlim(1);
        var closed = false;
        async Task Pump(StreamReader source, string name)
        {
            try
            {
                while (await source.ReadLineAsync() is { } line)
                {
                    await logGate.WaitAsync();
                    try { if (!closed) await writer.WriteLineAsync($"[{name}] {line}"); } finally { logGate.Release(); }
                }
            }
            catch (Exception e) when (e is ObjectDisposedException or IOException) { /* abandoned after the drain limit */ }
        }
        var output = Pump(process.StandardOutput, tag is null ? "out" : tag + ":out"); var errors = Pump(process.StandardError, tag is null ? "err" : tag + ":err");
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            if (job is not null) job.Terminate(); else { try { process.Kill(false); } catch (InvalidOperationException) { } }
            // A process that ignores termination must not hold the job forever either.
            await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(10)));
            throw;
        }
        finally
        {
            if (process.HasExited) job?.Terminate();
            // Output still arriving after the wrapper is gone comes from a child holding our pipes; stop waiting for it.
            var drained = Task.WhenAll(output, errors);
            if (await Task.WhenAny(drained, Task.Delay(DrainLimit)) != drained)
            {
                await logGate.WaitAsync();
                try { closed = true; await writer.WriteLineAsync($"[winix] Output pipes still held open {DrainLimit.TotalSeconds:0} s after the wrapper exited (a child inherited them); log capture stopped."); }
                finally { logGate.Release(); }
            }
        }
        return process.ExitCode;
    }
    /// <summary>
    /// Compiler and package caches shared by every workspace, so a work tree per branch does not mean a cold build per branch.
    /// Values the task or the machine already set win.
    /// </summary>
    internal static void SharedCaches(IDictionary<string, string?> environment)
    {
        var cache = Program.Dir("cache");
        void Default(string key, string path)
        {
            if (environment.TryGetValue(key, out var existing) && !string.IsNullOrEmpty(existing)) return;
            try { Directory.CreateDirectory(path); environment[key] = path; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        Default("WINIX_CACHE", Path.Combine(cache, "shared"));
        Default("CCACHE_DIR", Path.Combine(cache, "ccache"));
        Default("SCCACHE_DIR", Path.Combine(cache, "sccache"));
        Default("VCPKG_DEFAULT_BINARY_CACHE", Path.Combine(cache, "vcpkg-binary"));
        // Paths under any work tree hash the same, so another branch's tree hits the same cache entries.
        if (!(environment.TryGetValue("CCACHE_BASEDIR", out var basedir) && !string.IsNullOrEmpty(basedir))) environment["CCACHE_BASEDIR"] = Path.Combine(Program.Root, "work");
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
    public void Dispose() { lock (gate) foreach (var run in running.Values) run.Cancel.Cancel(); }
    /// <summary>
    /// The in-guest task wrapper. The Visual Studio developer environment is captured once per VS version and
    /// architecture and replayed from a cache on later runs (PATH is prepended, other variables set).
    /// </summary>
    private const string Wrapper = """
param([string]$RequestFile, [string]$WorkspaceRoot, [string]$CacheDir)
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)
try {
  $r = Get-Content -Raw -Encoding UTF8 -LiteralPath $RequestFile | ConvertFrom-Json
  if ($r.task.visualStudio) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $vsInfo = & $vswhere -latest -products '*' -format json | ConvertFrom-Json | Select-Object -First 1
    if (!$vsInfo) { throw 'Visual Studio was not found.' }
    $vs = $vsInfo.installationPath
    $cache = $null
    if ($CacheDir) { $cache = Join-Path $CacheDir ("vsenv-" + $vsInfo.installationVersion + "-" + $r.task.architecture + ".json") }
    if ($cache -and (Test-Path -LiteralPath $cache)) {
      $saved = Get-Content -Raw -Encoding UTF8 -LiteralPath $cache | ConvertFrom-Json
      foreach ($p in $saved.set.PSObject.Properties) { Set-Item -Path ("Env:" + $p.Name) -Value $p.Value }
      if ($saved.pathPrefix) { $env:Path = $saved.pathPrefix + ';' + $env:Path }
    } else {
      $before = @{}; Get-ChildItem Env: | ForEach-Object { $before[$_.Name] = $_.Value }
      Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
      Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments "-arch=$($r.task.architecture) -host_arch=x64" | Out-Null
      if ($cache) {
        $set = @{}; $prefix = ''
        foreach ($e in Get-ChildItem Env:) {
          if ($e.Name -ieq 'Path') { $old = $before['Path']; if ($old -and $e.Value.EndsWith($old)) { $prefix = $e.Value.Substring(0, $e.Value.Length - $old.Length).TrimEnd(';') } else { $set[$e.Name] = $e.Value } }
          elseif (!$before.ContainsKey($e.Name) -or $before[$e.Name] -ne $e.Value) { $set[$e.Name] = $e.Value }
        }
        try { @{ version = $vsInfo.installationVersion; arch = $r.task.architecture; set = $set; pathPrefix = $prefix } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $cache -Encoding UTF8 } catch { Write-Warning "VS environment cache not written: $_" }
      }
    }
  }
  $global:LASTEXITCODE = 0
  $a = @($r.task.arguments)
  if ($r.task.script) { & ([scriptblock]::Create($r.task.script)) @a }
  elseif ($r.task.scriptFile) { & (Join-Path $WorkspaceRoot $r.task.scriptFile) @a }
  else { & $r.task.executable @a }
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
