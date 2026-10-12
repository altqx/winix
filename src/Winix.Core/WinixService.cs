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
    /// <summary>The longest a lease lasts from its first acquisition, renewals included. WINIX_LEASE_MAX_TTL (e.g. 12h) overrides.</summary>
    public static TimeSpan MaxLeaseTtl => System.Environment.GetEnvironmentVariable("WINIX_LEASE_MAX_TTL") is { Length: > 0 } max ? Paths.Duration(max) : TimeSpan.FromHours(8);
    /// <summary>A lease whose holder has no job running and makes no Winix call for this long ends by itself.</summary>
    public static readonly TimeSpan DefaultLeaseIdle = TimeSpan.FromMinutes(30);
    /// <summary>Lease calls wait this long for the state lock: queued waiters take it briefly every two seconds.</summary>
    private static readonly TimeSpan LeaseLockWait = TimeSpan.FromSeconds(5);
    /// <summary>A queued waiter that has not heartbeat for this long (its process ended) leaves the queue.</summary>
    private static readonly TimeSpan WaiterStale = TimeSpan.FromSeconds(30);
    public LeaseRecord? Lease(string vm) => store.TryRead<LeaseRecord>("leases", Paths.Id(vm)) is { Active: true } lease ? lease : null;
    /// <summary>Owners queued by <c>lease acquire --wait</c>, first come first served; stale waiters are left out.</summary>
    public List<LeaseWaiter> Waiters(string vm) => (store.TryRead<LeaseQueue>("lease-queue", Paths.Id(vm))?.Waiters ?? [])
        .Where(w => DateTimeOffset.UtcNow - w.Heartbeat < WaiterStale).ToList();
    public object LeaseStatus(string vm) => new
    {
        vm, lease = Lease(vm), maxTtl = MaxLeaseTtl.ToString(),
        waiting = Waiters(vm).Select((w, i) => new { position = i + 1, owner = w.Owner, since = w.Since, note = w.Note }).ToList()
    };
    public LeaseRecord Acquire(string vm, string owner, TimeSpan ttl, string? note = null, TimeSpan? idle = null)
    {
        ValidateLease(vm, owner, ttl, idle);
        using var guard = store.Lock(LeaseLockWait);
        if (TryGrant(vm, owner, ttl, note, idle) is { } lease) return lease;
        if (Lease(vm) is { } current) throw Leased(vm, current);
        var queued = Waiters(vm);
        throw new WinixException("vm_leased", $"VM {vm} is free, but {queued.Count} owner(s) queued first ({string.Join(", ", queued.Select(w => w.Owner))}). Use lease acquire --wait to join the queue.");
    }
    /// <summary>Joins the queue and returns once this owner holds the lease, first come first served. Leaving (timeout, interrupt) drops the place.</summary>
    public async Task<LeaseRecord> AcquireWait(string vm, string owner, TimeSpan ttl, string? note, TimeSpan? idle, TimeSpan timeout, CancellationToken ct = default)
    {
        ValidateLease(vm, owner, ttl, idle);
        var until = DateTimeOffset.UtcNow + timeout;
        var granted = false;
        try
        {
            while (true)
            {
                try
                {
                    using var guard = store.Lock();
                    var queue = Waiters(vm);
                    var index = queue.FindIndex(w => w.Owner == owner);
                    if (index < 0) queue.Add(new(owner, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, note));
                    else queue[index] = queue[index] with { Heartbeat = DateTimeOffset.UtcNow };
                    store.Write("lease-queue", vm, new LeaseQueue(queue));
                    if (TryGrant(vm, owner, ttl, note, idle) is { } lease) { granted = true; return lease; }
                }
                catch (WinixException e) when (e.Code == "busy") { /* another Winix call holds the state lock; try again shortly */ }
                if (DateTimeOffset.UtcNow >= until)
                    throw new WinixException("lease_timeout", $"VM {vm} was not free within {timeout}. {(Lease(vm) is { } held ? $"Held by {held.Owner} until {held.EndsAt:u}." : "")}");
                await Task.Delay(2000, ct);
            }
        }
        finally
        {
            if (!granted)
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    try { using var guard = store.Lock(LeaseLockWait); store.Write("lease-queue", vm, new LeaseQueue(Waiters(vm).Where(w => w.Owner != owner).ToList())); break; }
                    catch (WinixException e) when (e.Code == "busy") { Thread.Sleep(200); }
                }
        }
    }
    /// <summary>Grants or renews under the state lock, or returns null when another owner holds the VM or is queued first.</summary>
    private LeaseRecord? TryGrant(string vm, string owner, TimeSpan ttl, string? note, TimeSpan? idle)
    {
        var current = Lease(vm);
        if (current is not null && current.Owner != owner) return null;
        var queue = Waiters(vm);
        if (current is null && queue.TakeWhile(w => w.Owner != owner).Any()) return null;
        var now = DateTimeOffset.UtcNow;
        var acquired = current?.AcquiredAt ?? now;
        // Renewal moves the expiry but never past the maximum TTL, and is not activity: an idle holder still loses the lease.
        var expires = new[] { now + ttl, acquired + MaxLeaseTtl }.Min();
        var lease = new LeaseRecord(vm, owner, acquired, expires, note ?? current?.Note, current?.LastActivity ?? now,
            (int)(idle ?? (current is null ? DefaultLeaseIdle : TimeSpan.FromSeconds(current.IdleSeconds))).TotalSeconds, current?.BusyJob, current?.BusyUntil);
        store.Write("leases", vm, lease);
        if (queue.Any(w => w.Owner == owner)) store.Write("lease-queue", vm, new LeaseQueue(queue.Where(w => w.Owner != owner).ToList()));
        return lease;
    }
    private static void ValidateLease(string vm, string owner, TimeSpan ttl, TimeSpan? idle)
    {
        Paths.Id(vm); ValidateOwner(owner);
        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromDays(7)) throw new WinixException("usage", "Lease TTL must be between 1 second and 7 days.");
        if (idle is { } i && (i < TimeSpan.Zero || i > TimeSpan.FromDays(1))) throw new WinixException("usage", "Lease idle timeout must be 0 (never) to 1 day.");
    }
    public object Release(string vm, string? owner, bool force = false)
    {
        using var guard = store.Lock(LeaseLockWait);
        var current = Lease(vm);
        if (current is null) { store.Delete("leases", vm); return new { vm, released = false, reason = "no active lease" }; }
        if (!force && current.Owner != owner) throw Leased(vm, current);
        store.Delete("leases", vm);
        return new { vm, released = true, owner = current.Owner };
    }
    public void RequireAccess(string vm, string? owner)
    {
        if (Lease(vm) is { } lease)
        {
            if (lease.Owner != owner) throw Leased(vm, lease);
            Touch(vm, owner);
        }
    }
    /// <summary>Records activity by the lease holder (throttled). Best effort: a contended state lock skips it.</summary>
    public void Touch(string vm, string? owner) => UpdateLease(vm, lease => lease.Owner == owner && DateTimeOffset.UtcNow - (lease.LastActivity ?? lease.AcquiredAt) > TimeSpan.FromSeconds(15)
        ? lease with { LastActivity = DateTimeOffset.UtcNow } : null);
    private void UpdateLease(string vm, Func<LeaseRecord, LeaseRecord?> change)
    {
        if (Lease(vm) is not { } seen || change(seen) is null) return;
        try
        {
            using var guard = store.Lock();
            if (Lease(vm) is { } lease && change(lease) is { } updated) store.Write("leases", vm, updated);
        }
        catch (WinixException e) when (e.Code == "busy") { }
    }
    private static WinixException Leased(string vm, LeaseRecord lease) => new("vm_leased", $"VM {vm} is leased by {lease.Owner} until {lease.EndsAt:u}" +
        $"{(lease.IdleAt is { } idle && idle <= lease.ExpiresAt ? " (or longer while they stay active)" : "")}{(lease.Note is null ? "" : $" ({lease.Note})")}. Pass --owner {lease.Owner}, lease acquire --wait, or lease release --force.");
    private static void ValidateOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 64 || owner.Any(char.IsControl)) throw new WinixException("usage", "Provide --owner NAME (up to 64 printable characters).");
    }
    public async Task<object> VmStatus(string vm)
    {
        var status = await Vms.Status(vm);
        return new { vm, status, lease = Lease(vm), waiting = Waiters(vm).Select(w => w.Owner).ToList() };
    }
    public async Task Down(string vm, bool force, string? owner)
    {
        RequireAccess(vm, owner);
        await Vms.Down(vm, force);
    }

    // ---- Workspace sync ----
    /// <summary>The guest work tree to use: <c>--workspace</c>, else WINIX_WORKSPACE, else the config's <c>workspace</c>.</summary>
    public static string Workspace(WinixConfig config, string? workspace) =>
        Paths.Id(workspace ?? (System.Environment.GetEnvironmentVariable("WINIX_WORKSPACE") is { Length: > 0 } fromEnv ? fromEnv : config.Workspace));
    public async Task<SyncResult> Sync(string vm, string root, string configFile, string? owner = null, bool full = false, string? workspace = null, CancellationToken ct = default)
    {
        RequireAccess(vm, owner);
        var config = WinixConfig.Load(configFile);
        var name = Workspace(config, workspace);
        var stateId = Paths.Id(vm + "-" + name);
        var last = store.TryRead<SyncState>("sync", stateId);
        var previous = full ? null : last;
        var source = await WorkspaceSync.Source(runner, root);
        try { return await SyncOnce(vm, root, config, name, previous, stateId, source, last?.Manifest.Source, ct); }
        catch (WinixException e) when (previous is not null && e.Code is "incremental_stale" or "checksum_mismatch" or "invalid_archive")
        {
            // The guest no longer matches our record (restored image, reverted snapshot, manual edits): send everything once.
            return await SyncOnce(vm, root, config, name, null, stateId, source, last?.Manifest.Source, ct);
        }
    }
    private async Task<SyncResult> SyncOnce(string vm, string root, WinixConfig config, string workspace, SyncState? previous, string stateId, SourceInfo source, SourceInfo? before, CancellationToken ct)
    {
        var archive = Path.Combine(store.Root, "sync-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var packed = await WorkspaceSync.PackIncremental(runner, root, workspace, config.Exclude, archive, previous, source);
            await Guest(vm).Upload(archive, Path.GetFileName(archive), ct);
            var applied = Wire.Decode<SyncManifest>((await Guest(vm).Call("sync", new { archive = Path.GetFileName(archive) }, ct)).GetRawText());
            store.Write("sync", stateId, packed.State);
            return new(workspace, applied.Files.Count, packed.Added, packed.Changed, packed.Removed, packed.Unchanged, packed.Sent, packed.SentBytes, packed.Incremental, applied, source, before);
        }
        finally { if (File.Exists(archive)) File.Delete(archive); }
    }

    // ---- Jobs ----
    /// <summary>How long past its timeout (and teardown) a job may stay running before the host stops it. WINIX_TIMEOUT_GRACE seconds overrides.</summary>
    public static TimeSpan TimeoutGrace => TimeSpan.FromSeconds(double.TryParse(System.Environment.GetEnvironmentVariable("WINIX_TIMEOUT_GRACE"), out var s) ? s : 60);
    public async Task<JobRecord> Run(string vm, string configFile, string taskName, RunParameters? parameters = null, string? label = null,
        string? owner = null, TimeSpan? queue = null, string? workspace = null, CancellationToken ct = default)
    {
        RequireAccess(vm, owner);
        var config = WinixConfig.Load(configFile);
        if (!config.Tasks.TryGetValue(taskName, out var task)) throw new WinixException("task_missing", $"Unknown task: {taskName}");
        if (label is { Length: > 128 }) throw new WinixException("usage", "Labels are at most 128 characters.");
        var name = Workspace(config, workspace);
        var effective = task.Effective(parameters); effective.Validate();
        var id = "job-" + Guid.NewGuid().ToString("N");
        var source = store.TryRead<SyncState>("sync", Paths.Id(vm + "-" + name))?.Manifest.Source;
        // Persist before submitting; a lost response must not cause a duplicate execution.
        store.Write("jobs", id, new HostJob(vm, name, id, DateTimeOffset.UtcNow, label, parameters, owner, effective.TimeoutSeconds, effective.BudgetSeconds - effective.TimeoutSeconds, source));
        var request = new JobRequest(id, name, effective, label, parameters);
        var deadline = queue is { } wait ? DateTimeOffset.UtcNow + wait : (DateTimeOffset?)null;
        HashSet<string> enforced = [];
        while (true)
        {
            try
            {
                var started = Wire.Decode<JobRecord>((await Guest(vm).Call("job.start", request, ct)).GetRawText());
                // The lease stays alive while the job may still run, even if its holder goes quiet.
                UpdateLease(vm, lease => lease.Owner == owner ? lease with { BusyJob = id, BusyUntil = DateTimeOffset.UtcNow.AddSeconds(effective.BudgetSeconds) + TimeoutGrace } : null);
                return started;
            }
            catch (WinixException e) when (e.Code == "busy" && deadline is { } until && DateTimeOffset.UtcNow < until)
            {
                // Queued: another job owns the guest. The same durable ID is resubmitted, so nothing runs twice.
                // A job that outlived its timeout is stopped here, so one stuck job cannot stall the queue behind it.
                await EnforceTimeout(vm, enforced, ct);
                await Task.Delay(3000, ct);
            }
            catch (WinixException e) when (e.Code != "busy" || deadline is null)
            { throw new WinixException(e.Code, $"Job ID {id}: {e.Message} Query this ID before resubmitting; the guest may already have accepted it."); }
            catch (WinixException e) { throw new WinixException("queue_timeout", $"Job ID {id} was not started within the queue window: {e.Message}"); }
        }
    }
    private async Task EnforceTimeout(string vm, HashSet<string> enforced, CancellationToken ct)
    {
        try
        {
            var health = await Guest(vm).Call("health", new { }, ct);
            if (!health.TryGetProperty("activeJob", out var active) || active.GetString() is not { } activeId || enforced.Contains(activeId)) return;
            var record = Wire.Decode<JobRecord>((await Guest(vm).Call("job.status", new { id = activeId }, ct)).GetRawText());
            if (Deadline(record, store.TryRead<HostJob>("jobs", activeId)) is { } due && DateTimeOffset.UtcNow > due)
            {
                enforced.Add(activeId);
                await StopJob(vm, activeId, force: true, reason: "timed_out", ct);
            }
        }
        catch (WinixException e) when (e.Code is "transport_error" or "desktop_unavailable" or "job_not_found") { }
    }
    /// <summary>When the host stops waiting for a running job: its start plus timeout, teardown and <see cref="TimeoutGrace"/>. Null if the timeout is unknown.</summary>
    public static DateTimeOffset? Deadline(JobRecord record, HostJob? host)
    {
        if ((record.TimeoutSeconds ?? host?.TimeoutSeconds) is not { } timeout) return null;
        return (record.StartedAt ?? record.CreatedAt).AddSeconds(timeout + (host?.TeardownSeconds ?? 0)) + TimeoutGrace;
    }
    public async Task<JsonElement> Job(string id, string action, long offset = 0, CancellationToken ct = default)
    {
        var job = store.Read<HostJob>("jobs", id);
        Touch(job.Vm, job.Owner);
        return await Guest(job.Vm).Call("job." + action, new { id, offset }, ct);
    }
    public HostJob HostJob(string id) => store.Read<HostJob>("jobs", id);
    /// <summary>
    /// A job record with what the host knows beside it: VM, owner, source commit, and elapsed time against the timeout.
    /// <c>overdue</c> means the job is running past its timeout plus grace; <c>job cancel</c> (or a waiting <c>job wait</c>) stops it.
    /// </summary>
    public Dictionary<string, object?> Describe(object job)
    {
        var view = Wire.Typed("job", job);
        var record = Wire.Decode<JobRecord>(Wire.Encode(job));
        var host = store.TryRead<HostJob>("jobs", record.Id);
        if (host is not null)
        {
            view["vm"] = host.Vm;
            if (host.Owner is not null) view["owner"] = host.Owner;
            if (record.Source is null && host.Source is not null) view["source"] = Wire.Element(host.Source);
        }
        var timeout = record.TimeoutSeconds ?? host?.TimeoutSeconds;
        if (timeout is not null) view["timeoutSeconds"] = timeout;
        var elapsed = (int)((record.FinishedAt ?? DateTimeOffset.UtcNow) - (record.StartedAt ?? record.CreatedAt)).TotalSeconds;
        view["elapsedSeconds"] = Math.Max(0, elapsed);
        if (!record.Terminal && timeout is not null)
        {
            view["remainingSeconds"] = timeout - elapsed;
            var overdue = Deadline(record, host) is { } due && DateTimeOffset.UtcNow > due;
            view["overdue"] = overdue;
            if (overdue) view["hint"] = $"Running past its {timeout} s timeout; winix job cancel {record.Id} stops it (escalating to a worker restart).";
        }
        return view;
    }
    /// <summary>
    /// Polls until the job ends. Connection loss is retried for <see cref="ContactTimeout"/>; after that <c>contact_lost</c> is raised and the job keeps running.
    /// A job running past its deadline (<see cref="Deadline"/>) is stopped from here, escalating up to a worker restart.
    /// </summary>
    public async Task<JobRecord> Wait(string id, CancellationToken ct = default, TimeSpan? contactTimeout = null)
    {
        var limit = contactTimeout ?? ContactTimeout; DateTimeOffset? lostSince = null; var delay = 1000; var enforced = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var record = Wire.Decode<JobRecord>((await Job(id, "status", ct: ct)).GetRawText());
                lostSince = null; delay = 1000;
                if (record.Terminal) { Finished(id); return record; }
                if (!enforced && Deadline(record, store.TryRead<HostJob>("jobs", id)) is { } due && DateTimeOffset.UtcNow > due)
                {
                    enforced = true;
                    await StopJob(HostJob(id).Vm, id, force: true, reason: "timed_out", ct);
                    continue;
                }
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
    /// <summary>The job is over: the lease no longer needs to stay alive for it.</summary>
    private void Finished(string id)
    {
        if (store.TryRead<HostJob>("jobs", id) is not { } job) return;
        UpdateLease(job.Vm, lease => lease.BusyJob == id ? lease with { BusyJob = null, BusyUntil = null, LastActivity = DateTimeOffset.UtcNow } : null);
    }
    /// <summary>
    /// Stops a job and does not give up: a cooperative cancel (task tree terminated, teardown run), then a kill of everything the
    /// task started, then a restart of the guest worker through the QEMU guest agent. <paramref name="force"/> skips the first step.
    /// The job's owner may always cancel it; others need the lease.
    /// </summary>
    public async Task<object> Cancel(string id, bool force = false, string? owner = null, CancellationToken ct = default)
    {
        var job = HostJob(id);
        if (owner is null || owner != job.Owner) RequireAccess(job.Vm, owner);
        return await StopJob(job.Vm, id, force, "cancelled", ct);
    }
    private async Task<object> StopJob(string vm, string id, bool force, string reason, CancellationToken ct)
    {
        var host = store.TryRead<HostJob>("jobs", id);
        var teardown = TimeSpan.FromSeconds(host?.TeardownSeconds ?? 0);
        List<string> steps = [];
        JobRecord? record = null;
        async Task<bool> Stopped(TimeSpan within)
        {
            var until = DateTimeOffset.UtcNow + within;
            while (true)
            {
                try { record = Wire.Decode<JobRecord>((await Guest(vm).Call("job.status", new { id }, ct)).GetRawText()); if (record.Terminal) return true; }
                catch (WinixException e) when (e.Code is "transport_error" or "desktop_unavailable") { }
                if (DateTimeOffset.UtcNow >= until) return false;
                await Task.Delay(1000, ct);
            }
        }
        async Task Ask(bool kill)
        {
            steps.Add(kill ? "kill" : "cancel");
            try { await Guest(vm).Call("job.cancel", new { id, force = kill, reason }, ct); }
            catch (WinixException e) when (e.Code is "transport_error" or "desktop_unavailable") { }
        }
        var done = false;
        if (!force) { await Ask(false); done = await Stopped(TimeSpan.FromSeconds(20) + teardown); }
        if (!done) { await Ask(true); done = await Stopped(TimeSpan.FromSeconds(20) + teardown); }
        if (!done)
        {
            steps.Add("worker-restart");
            await RestartWorker(vm, ct);
            done = await Stopped(TimeSpan.FromSeconds(60) + teardown);
        }
        if (!done) throw new WinixException("cancel_failed", $"Job {id} is still running after {string.Join(", ", steps)}. Use vm down --force.");
        Finished(id);
        return new { id, stopped = true, escalation = steps, job = Describe(record!) };
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
        if (!ReadOnlyUi.Contains(action)) RequireAccess(vm, owner); else Touch(vm, owner);
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
    /// <summary>
    /// Restarts the desktop worker as SYSTEM through the QEMU guest agent, for a worker that no longer answers or a job that will
    /// not stop. The process trees of unfinished jobs are ended first. The new worker marks those jobs cancelled, timed out or
    /// interrupted (as asked before the restart) and runs their teardown.
    /// </summary>
    public async Task<object> RestartWorker(string vm, CancellationToken ct = default)
    {
        if (await Vms.Status(vm) != "running") throw new WinixException("vm_not_running", "The VM is not running.");
        var output = await Vms.GuestExec(vm, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(RestartScript))], TimeSpan.FromMinutes(2));
        JsonElement? after = null; string? error = null;
        for (var i = 0; i < 60 && after is null; i++)
        {
            await Task.Delay(2000, ct);
            try { after = await Guest(vm).Call("health", new { }, ct); } catch (WinixException e) { error = e.Message; }
        }
        if (after is null) throw new WinixException("worker_unavailable", $"The worker did not come back after the restart: {error}. Guest output: {output.Trim()}");
        return new { vm, restarted = true, version = after.Value.GetProperty("version").GetString(), guestOutput = output.Trim() };
    }
    public async Task<object> GuestRestart(string vm, string? owner, CancellationToken ct = default)
    {
        RequireAccess(vm, owner);
        return await RestartWorker(vm, ct);
    }
    private const string RestartScript = """
        $ErrorActionPreference = 'Continue'
        $root = 'C:\Winix'
        Stop-ScheduledTask -TaskName WinixDesktop -ErrorAction SilentlyContinue
        foreach ($dir in Get-ChildItem "$root\jobs" -Directory -ErrorAction SilentlyContinue) {
          $record = Join-Path $dir.FullName 'record.json'
          if (!(Test-Path -LiteralPath $record)) { continue }
          $state = (Get-Content -Raw -LiteralPath $record | ConvertFrom-Json).state
          if ($state -notin 'queued', 'running') { continue }
          foreach ($file in Get-ChildItem $dir.FullName -Filter 'pid*' -File) {
            $id = (Get-Content -Raw -LiteralPath $file.FullName).Trim()
            if ($id) { & taskkill.exe /PID $id /T /F 2>&1 | Out-Null }
          }
          Write-Output "stopped $($dir.Name)"
        }
        Get-Process winix-guest -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        Start-ScheduledTask -TaskName WinixDesktop
        Write-Output 'worker restarted'
        """;

    // ---- Snapshots: a known baseline for evidence runs. Taken and restored with the VM shut off. ----
    public async Task<SnapshotRecord> Snapshot(string vm, string name, string? note, string? owner)
    {
        RequireAccess(vm, owner);
        return await Vms.Snapshot(vm, name, note);
    }
    public async Task<object> SnapshotDelete(string vm, string name, string? owner)
    {
        RequireAccess(vm, owner);
        await Vms.SnapshotDelete(vm, name);
        return new { vm, snapshot = name, deleted = true };
    }
    /// <summary>
    /// Returns the VM to a snapshot. With <paramref name="restart"/> a running VM is shut down first (refused while a job runs)
    /// and started again from the config, waiting until the desktop is ready.
    /// </summary>
    public async Task<object> Revert(string vm, string name, string? owner, bool restart, string configFile, CancellationToken ct = default)
    {
        RequireAccess(vm, owner);
        var running = await Vms.Status(vm) is not ("shut off" or "undefined");
        if (running && !restart) throw new WinixException("vm_running", $"VM {vm} is running. Pass --restart to shut it down, revert and start it again, or run vm down first.");
        var config = restart ? WinixConfig.Load(configFile) : null;
        if (running) await Vms.Down(vm);
        await Vms.Revert(vm, name);
        if (config is null) return new { vm, snapshot = name, reverted = true, status = "shut off" };
        await Vms.Up(vm, config.Image, config.Resources, false, config.Vm);
        await Ready(vm, ct: ct);
        return new { vm, snapshot = name, reverted = true, status = "running" };
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
