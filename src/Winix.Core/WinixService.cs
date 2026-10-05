using System.IO.Compression;
using System.Text.Json;

namespace Winix;

public sealed class WinixService(StateStore store, ICommandRunner runner)
{
    public StateStore Store => store;
    public VmManager Vms { get; } = new(store, runner);
    public GuestConnection Guest(string name) => new(store.Read<VmRecord>("vms", name), runner);
    public HostInput Input => new(store, runner, Guest);
    public Task<List<Check>> Doctor(CancellationToken ct = default) => Winix.Doctor.Run(runner, store, ct);
    /// <summary>How long <see cref="Wait"/> tolerates a lost connection before giving up with <c>contact_lost</c>. WINIX_CONTACT_TIMEOUT seconds overrides.</summary>
    public static TimeSpan ContactTimeout => TimeSpan.FromSeconds(double.TryParse(System.Environment.GetEnvironmentVariable("WINIX_CONTACT_TIMEOUT"), out var s) ? s : 600);
    /// <summary>The caller's identity for leases: <c>--owner</c>, else WINIX_OWNER.</summary>
    public static string? DefaultOwner => System.Environment.GetEnvironmentVariable("WINIX_OWNER") is { Length: > 0 } owner ? owner : null;

    public async Task<JsonElement> Ready(string name, int timeoutSeconds = 600, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow.AddSeconds(timeoutSeconds); string? error = null;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var health = await Guest(name).Call("health", new { }, ct);
                if (health.GetProperty("desktopReady").GetBoolean() &&
                    health.GetProperty("toolchain").TryGetProperty("ready", out var ready) && ready.GetBoolean()) return health;
                error = "The guest is reachable but its desktop or toolchain is not ready.";
            }
            catch (WinixException e) { error = e.Message; }
            await Task.Delay(2000, ct);
        }
        throw new WinixException("ready_timeout", $"Windows was not ready: {error}");
    }

    // ---- Leases: who holds a VM, honoured by sync, run, ui, push/pull and vm down. ----
    public LeaseRecord? Lease(string vm) => store.TryRead<LeaseRecord>("leases", Paths.Id(vm)) is { Active: true } lease ? lease : null;
    public LeaseRecord Acquire(string vm, string owner, TimeSpan ttl, string? note = null)
    {
        Paths.Id(vm); ValidateOwner(owner);
        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromDays(7)) throw new WinixException("usage", "Lease TTL must be between 1 second and 7 days.");
        using var guard = store.Lock();
        var current = Lease(vm);
        if (current is not null && current.Owner != owner) throw Leased(vm, current);
        var lease = new LeaseRecord(vm, owner, current?.AcquiredAt ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow + ttl, note ?? current?.Note);
        store.Write("leases", vm, lease);
        return lease;
    }
    public object Release(string vm, string? owner, bool force = false)
    {
        using var guard = store.Lock();
        var current = Lease(vm);
        if (current is null) { store.Delete("leases", vm); return new { vm, released = false, reason = "no active lease" }; }
        if (!force && current.Owner != owner) throw Leased(vm, current);
        store.Delete("leases", vm);
        return new { vm, released = true, owner = current.Owner };
    }
    public void RequireAccess(string vm, string? owner)
    {
        if (Lease(vm) is { } lease && lease.Owner != owner) throw Leased(vm, lease);
    }
    private static WinixException Leased(string vm, LeaseRecord lease) => new("vm_leased", $"VM {vm} is leased by {lease.Owner} until {lease.ExpiresAt:u}{(lease.Note is null ? "" : $" ({lease.Note})")}. Pass --owner {lease.Owner}, wait, or lease release --force.");
    private static void ValidateOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 64 || owner.Any(char.IsControl)) throw new WinixException("usage", "Provide --owner NAME (up to 64 printable characters).");
    }
    public async Task<object> VmStatus(string vm)
    {
        var status = await Vms.Status(vm);
        return new { vm, status, lease = Lease(vm) };
    }
    public async Task Down(string vm, bool force, string? owner)
    {
        RequireAccess(vm, owner);
        await Vms.Down(vm, force);
    }

    // ---- Workspace sync ----
    public async Task<SyncResult> Sync(string vm, string root, string configFile, string? owner = null, bool full = false, CancellationToken ct = default)
    {
        RequireAccess(vm, owner);
        var config = WinixConfig.Load(configFile);
        var stateId = Paths.Id(vm + "-" + config.Workspace);
        var previous = full ? null : store.TryRead<SyncState>("sync", stateId);
        try { return await SyncOnce(vm, root, config, previous, stateId, ct); }
        catch (WinixException e) when (previous is not null && e.Code is "incremental_stale" or "checksum_mismatch" or "invalid_archive")
        {
            // The guest no longer matches our record (restored image, manual edits): send everything once.
            return await SyncOnce(vm, root, config, null, stateId, ct);
        }
    }
    private async Task<SyncResult> SyncOnce(string vm, string root, WinixConfig config, SyncState? previous, string stateId, CancellationToken ct)
    {
        var archive = Path.Combine(store.Root, "sync-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var packed = await WorkspaceSync.PackIncremental(runner, root, config.Workspace, config.Exclude, archive, previous);
            await Guest(vm).Upload(archive, Path.GetFileName(archive), ct);
            var applied = Wire.Decode<SyncManifest>((await Guest(vm).Call("sync", new { archive = Path.GetFileName(archive) }, ct)).GetRawText());
            store.Write("sync", stateId, packed.State);
            return new(config.Workspace, applied.Files.Count, packed.Added, packed.Changed, packed.Removed, packed.Unchanged, packed.Sent, packed.SentBytes, packed.Incremental, applied);
        }
        finally { if (File.Exists(archive)) File.Delete(archive); }
    }

    // ---- Jobs ----
    public async Task<JobRecord> Run(string vm, string configFile, string taskName, RunParameters? parameters = null, string? label = null,
        string? owner = null, TimeSpan? queue = null, CancellationToken ct = default)
    {
        RequireAccess(vm, owner);
        var config = WinixConfig.Load(configFile);
        if (!config.Tasks.TryGetValue(taskName, out var task)) throw new WinixException("task_missing", $"Unknown task: {taskName}");
        if (label is { Length: > 128 }) throw new WinixException("usage", "Labels are at most 128 characters.");
        var id = "job-" + Guid.NewGuid().ToString("N");
        // Persist before submitting; a lost response must not cause a duplicate execution.
        store.Write("jobs", id, new HostJob(vm, config.Workspace, id, DateTimeOffset.UtcNow, label, parameters));
        var request = new JobRequest(id, config.Workspace, task.Effective(parameters), label, parameters);
        var deadline = queue is { } wait ? DateTimeOffset.UtcNow + wait : (DateTimeOffset?)null;
        while (true)
        {
            try { return Wire.Decode<JobRecord>((await Guest(vm).Call("job.start", request, ct)).GetRawText()); }
            catch (WinixException e) when (e.Code == "busy" && deadline is { } until && DateTimeOffset.UtcNow < until)
            {
                // Queued: another job owns the guest. The same durable ID is resubmitted, so nothing runs twice.
                await Task.Delay(3000, ct);
            }
            catch (WinixException e) when (e.Code != "busy" || deadline is null)
            { throw new WinixException(e.Code, $"Job ID {id}: {e.Message} Query this ID before resubmitting; the guest may already have accepted it."); }
            catch (WinixException e) { throw new WinixException("queue_timeout", $"Job ID {id} was not started within the queue window: {e.Message}"); }
        }
    }
    public async Task<JsonElement> Job(string id, string action, long offset = 0, CancellationToken ct = default)
    {
        var job = store.Read<HostJob>("jobs", id);
        return await Guest(job.Vm).Call("job." + action, new { id, offset }, ct);
    }
    public HostJob HostJob(string id) => store.Read<HostJob>("jobs", id);
    /// <summary>Polls until the job ends. Connection loss is retried for <see cref="ContactTimeout"/>; after that <c>contact_lost</c> is raised and the job keeps running.</summary>
    public async Task<JobRecord> Wait(string id, CancellationToken ct = default, TimeSpan? contactTimeout = null)
    {
        var limit = contactTimeout ?? ContactTimeout; DateTimeOffset? lostSince = null; var delay = 1000;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var record = Wire.Decode<JobRecord>((await Job(id, "status", ct: ct)).GetRawText());
                lostSince = null; delay = 1000;
                if (record.Terminal) return record;
            }
            catch (WinixException e) when (e.Code is "transport_error" or "desktop_unavailable")
            {
                lostSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - lostSince > limit)
                    throw new WinixException("contact_lost", $"No contact with the VM for {limit.TotalMinutes:0} minutes ({e.Message}). Job {id} may still be running; resume with winix job wait {id}.");
                delay = Math.Min(delay * 2, 15000);
            }
            await Task.Delay(delay, ct);
        }
    }
    /// <summary>Reads pages until <paramref name="marker"/> appears in the job output. Returns false if the job ended without it.</summary>
    public async Task<bool> WaitForLog(string id, string marker, TimeSpan timeout, CancellationToken ct = default)
    {
        if (marker.Length == 0) throw new WinixException("usage", "Marker must not be empty.");
        long offset = 0; var tail = ""; var until = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < until)
        {
            var page = Wire.Decode<LogPage>((await Job(id, "logs", offset, ct)).GetRawText());
            tail += page.Text;
            if (tail.Contains(marker, StringComparison.Ordinal)) return true;
            if (tail.Length > marker.Length) tail = tail[^(marker.Length - 1 > 0 ? marker.Length - 1 : 0)..];
            offset = page.NextOffset;
            if (page.End) return false;
            await Task.Delay(1000, ct);
        }
        throw new WinixException("marker_timeout", $"'{marker}' did not appear in job {id} within {timeout}.");
    }
    public async Task<string> Artifacts(string id, string destination, CancellationToken ct = default)
    {
        var job = store.Read<HostJob>("jobs", id);
        var response = await Guest(job.Vm).Call("job.export", new { id }, ct);
        var file = response.GetProperty("file").GetString()!;
        Directory.CreateDirectory(destination);
        var path = Path.Combine(Path.GetFullPath(destination), Paths.Relative(file));
        await Guest(job.Vm).Download(file, path, ct);
        return path;
    }
    /// <summary>Fetches one ad-hoc guest path for a job: absolute, <c>%TEMP%</c>-rooted, or relative to the job's workspace.</summary>
    public Task<TransferResult> JobFile(string id, string guestPath, string destination, string? owner, CancellationToken ct = default)
    {
        var job = store.Read<HostJob>("jobs", id);
        var resolved = Paths.IsGuestAbsolute(guestPath) ? guestPath : $"C:\\Winix\\work\\{job.Workspace}\\{Paths.Relative(guestPath).Replace('/', '\\')}";
        return Pull(job.Vm, resolved, destination, owner, ct);
    }

    // ---- Files outside the synced tree ----
    public async Task<TransferResult> Push(string vm, string hostPath, string guestPath, string? owner = null, CancellationToken ct = default)
    {
        RequireAccess(vm, owner); Paths.GuestPath(guestPath);
        hostPath = Path.GetFullPath(hostPath);
        var directory = Directory.Exists(hostPath);
        if (!directory && !File.Exists(hostPath)) throw new WinixException("not_found", hostPath);
        var name = "push-" + Guid.NewGuid().ToString("N") + (directory ? ".zip" : ".bin");
        var upload = hostPath; string? temporary = null;
        try
        {
            if (directory) { temporary = Path.Combine(store.Root, name); ZipFile.CreateFromDirectory(hostPath, temporary, CompressionLevel.Fastest, false); upload = temporary; }
            var sha256 = await Media.Hash(upload, ct);
            await Guest(vm).Upload(upload, name, ct);
            return Wire.Decode<TransferResult>((await Guest(vm).Call("file.put", new { inbox = name, destination = guestPath, directory, sha256 }, ct)).GetRawText());
        }
        finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<TransferResult> Pull(string vm, string guestPath, string hostPath, string? owner = null, CancellationToken ct = default)
    {
        RequireAccess(vm, owner); Paths.GuestPath(guestPath);
        var export = await Guest(vm).Call("file.get", new { path = guestPath }, ct);
        var file = export.GetProperty("file").GetString()!; var sha256 = export.GetProperty("sha256").GetString()!;
        var directory = export.GetProperty("directory").GetBoolean(); var source = export.GetProperty("source").GetString()!;
        var temporary = Path.Combine(store.Root, Paths.Relative(file));
        try
        {
            await Guest(vm).Download(file, temporary, ct);
            if (await Media.Hash(temporary, ct) != sha256) throw new WinixException("checksum_mismatch", $"Transfer of {guestPath} was corrupted.");
            hostPath = Path.GetFullPath(hostPath);
            if (directory) { Directory.CreateDirectory(hostPath); ZipFile.ExtractToDirectory(temporary, hostPath, true); }
            else
            {
                if (Directory.Exists(hostPath) || hostPath.EndsWith(Path.DirectorySeparatorChar)) { Directory.CreateDirectory(hostPath); hostPath = Path.Combine(hostPath, Path.GetFileName(source.Replace('\\', '/'))); }
                Directory.CreateDirectory(Path.GetDirectoryName(hostPath)!);
                File.Move(temporary, hostPath, true);
            }
            return new(source, hostPath, sha256, export.GetProperty("size").GetInt64(), directory);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            try { await Guest(vm).Call("file.remove", new { file }, CancellationToken.None); } catch (WinixException) { }
        }
    }

    // ---- Desktop ----
    private static readonly HashSet<string> ReadOnlyUi = ["screenshot", "windows", "state", "monitors", "tree", "find", "wait", "calibrate"];
    public async Task<JsonElement> Ui(string vm, string action, JsonElement arguments, string? owner = null, CancellationToken ct = default)
    {
        if (!ReadOnlyUi.Contains(action)) RequireAccess(vm, owner);
        switch (action)
        {
            case "pointer":
            {
                var path = Get(arguments, "path") is { } p ? HostInput.Point.ParsePath(p) : [];
                return Wire.Element(await Input.Pointer(vm, path, Get(arguments, "button") ?? "left", Flag(arguments, "hold"), Flag(arguments, "release"),
                    Int(arguments, "steps", 20), Int(arguments, "delayMs", 15), Get(arguments, "map") ?? "virtual", ct));
            }
            case "drag":
                return Wire.Element(await Input.Drag(vm, HostInput.Point.Parse(Get(arguments, "from") ?? throw new WinixException("usage", "drag needs from and to.")),
                    HostInput.Point.Parse(Get(arguments, "to") ?? throw new WinixException("usage", "drag needs from and to.")),
                    Get(arguments, "button") ?? "left", Int(arguments, "steps", 20), Int(arguments, "delayMs", 15), Get(arguments, "map") ?? "virtual", ct));
            case "calibrate":
                return Wire.Element(await Input.Calibrate(vm, HostInput.Point.Parse(Get(arguments, "point") ?? "100,100"), Get(arguments, "map") ?? "virtual", ct));
            case "kill" or "dump":
                return await Guest(vm).Call("process." + action, arguments, ct);
            default:
                return await Guest(vm).Call("ui." + action, arguments, ct);
        }
    }
    private static string? Get(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? p.ToString() : null;
    private static bool Flag(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && (p.ValueKind == JsonValueKind.True || (p.ValueKind == JsonValueKind.String && p.GetString() == "true"));
    private static int Int(JsonElement e, string name, int fallback) => Get(e, name) is { } s && int.TryParse(s, out var v) ? v : fallback;

    /// <summary>Guest-side device inventory: adapters, monitors, audio and PnP status.</summary>
    public Task<JsonElement> Devices(string vm, CancellationToken ct = default) => Guest(vm).Call("system.devices", new { }, ct);
    /// <summary>A narrow elevated verb executed by the provisioned SYSTEM task.</summary>
    public Task<JsonElement> Elevated(string vm, string kind, object arguments, string? owner, CancellationToken ct = default)
    {
        RequireAccess(vm, owner);
        return Guest(vm).Call("elevated.run", new { kind, arguments }, ct);
    }

    /// <summary>
    /// Replaces the desktop worker on a running VM with a freshly published guest. The archive goes in over SSH; the swap runs
    /// as SYSTEM through the QEMU guest agent because C:\Winix\agent is read-only to the test user. Also creates the data
    /// directories and the WinixElevated task that newer provisioning would have made, so an existing image gains them.
    /// </summary>
    public async Task<object> UpdateGuest(string vm, string guestDirectory, string? owner = null, CancellationToken ct = default)
    {
        RequireAccess(vm, owner);
        if (!File.Exists(Path.Combine(guestDirectory, "winix-guest.exe"))) throw new WinixException("guest_missing", $"No winix-guest.exe in {guestDirectory}; run scripts/publish.sh or dotnet publish src/Winix.Guest.");
        if (await Vms.Status(vm) != "running") throw new WinixException("vm_not_running", "Start the VM before updating its worker.");
        string? previous = null;
        try
        {
            var health = await Guest(vm).Call("health", new { }, ct);
            if (health.TryGetProperty("activeJob", out var active) && active.ValueKind != JsonValueKind.Null) throw new WinixException("busy", $"Job {active.GetString()} is running; wait or cancel it first.");
            previous = health.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch (WinixException e) when (e.Code is "transport_error" or "desktop_unavailable") { /* a dead worker is exactly what an update fixes */ }
        var name = "agent-" + Guid.NewGuid().ToString("N") + ".zip";
        var archive = Path.Combine(store.Root, name);
        try
        {
            ZipFile.CreateFromDirectory(guestDirectory, archive, CompressionLevel.Fastest, false);
            await Guest(vm).Upload(archive, name, ct);
        }
        finally { if (File.Exists(archive)) File.Delete(archive); }
        var script = UpdateScript.Replace("__ARCHIVE__", name);
        var output = await Vms.GuestExec(vm, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))], TimeSpan.FromMinutes(5));
        JsonElement? after = null; string? error = null;
        for (var i = 0; i < 60 && after is null; i++)
        {
            await Task.Delay(2000, ct);
            try { after = await Guest(vm).Call("health", new { }, ct); } catch (WinixException e) { error = e.Message; }
        }
        if (after is null) throw new WinixException("worker_unavailable", $"The worker did not come back after the update: {error}. Guest output: {output.Trim()}");
        return new { vm, previousVersion = previous, version = after.Value.GetProperty("version").GetString(), elevationAvailable = after.Value.TryGetProperty("elevationAvailable", out var el) && el.GetBoolean(),
            features = after.Value.TryGetProperty("features", out var f) ? f : default, guestOutput = output.Trim() };
    }
    private const string UpdateScript = """
        $ErrorActionPreference = 'Stop'
        $root = 'C:\Winix'; $zip = "$root\inbox\__ARCHIVE__"
        if (!(Test-Path -LiteralPath $zip)) { throw "Archive not found: $zip" }
        Stop-ScheduledTask -TaskName WinixDesktop -ErrorAction SilentlyContinue
        Get-Process winix-guest -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        $new = "$root\agent.new"; $old = "$root\agent.old"
        foreach ($d in @($new, $old)) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
        Expand-Archive -LiteralPath $zip -DestinationPath $new -Force
        if (!(Test-Path "$new\winix-guest.exe")) { throw 'The archive contains no winix-guest.exe.' }
        $swapped = $false
        for ($i = 0; $i -lt 10 -and !$swapped; $i++) {
          try { Rename-Item "$root\agent" $old -ErrorAction Stop; $swapped = $true } catch { Start-Sleep -Seconds 1 }
        }
        if (!$swapped) { throw 'C:\Winix\agent is still in use.' }
        Rename-Item $new "$root\agent"
        Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
        $user = Get-LocalUser -Name winix
        foreach ($dir in @('work','jobs','inbox','exports','elevated','dumps','profiles','cache','launch','tools')) {
          New-Item -ItemType Directory "$root\$dir" -Force | Out-Null
          & icacls "$root\$dir" /grant "*$($user.SID):(OI)(CI)M" | Out-Null
        }
        $scheduler = New-Object -ComObject Schedule.Service; $scheduler.Connect()
        $definition = $scheduler.NewTask(0)
        $definition.RegistrationInfo.Description = 'Winix elevated runner (tasks flagged elevated: true, winix guest driver add)'
        $definition.Principal.UserId = 'SYSTEM'; $definition.Principal.LogonType = 5; $definition.Principal.RunLevel = 1
        $definition.Settings.ExecutionTimeLimit = 'PT4H'; $definition.Settings.MultipleInstances = 2; $definition.Settings.AllowDemandStart = $true
        $definition.Settings.DisallowStartIfOnBatteries = $false; $definition.Settings.StopIfGoingOnBatteries = $false; $definition.Settings.StartWhenAvailable = $true
        $action = $definition.Actions.Create(0); $action.Path = "$root\agent\winix-guest.exe"; $action.Arguments = 'elevated'; $action.WorkingDirectory = $root
        $sddl = "D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;$($user.SID.Value))"
        $scheduler.GetFolder('\').RegisterTaskDefinition('WinixElevated', $definition, 6, 'SYSTEM', $null, 5, $sddl) | Out-Null
        Start-ScheduledTask -TaskName WinixDesktop
        Write-Output "agent replaced; WinixElevated registered; WinixDesktop restarted"
        """;

    public async Task<object> Ci(string configFile, string root, string destination, CancellationToken ct = default)
    {
        var config = WinixConfig.Load(configFile);
        var name = "ci-" + Guid.NewGuid().ToString("N")[..12];
        List<JobRecord> jobs = []; var created = false; var safeToDestroy = false;
        try
        {
            await Vms.Up(name, config.Image, config.Resources, true, config.Vm); created = true;
            await Ready(name, ct: ct);
            await Sync(name, root, configFile, full: true, ct: ct);
            foreach (var task in config.Pipeline)
            {
                var started = await Run(name, configFile, task, label: "ci", ct: ct);
                JobRecord completed;
                try { completed = await Wait(started.Id, ct); }
                catch (OperationCanceledException) { await Job(started.Id, "cancel"); throw; }
                jobs.Add(completed);
                await Artifacts(completed.Id, destination, ct);
                if (completed.State != JobState.Succeeded) break;
            }
            safeToDestroy = true;
            return new { succeeded = jobs.Count == config.Pipeline.Count && jobs.All(j => j.State == JobState.Succeeded), jobs };
        }
        finally
        {
            // Preserve a failed-to-export instance for recovery; never delete the sole evidence.
            if (created && safeToDestroy && jobs.All(j => File.Exists(Path.Combine(destination, j.Id + ".zip"))))
            {
                await Vms.Down(name, force: true);
                await Vms.Delete(name);
            }
        }
    }
}
