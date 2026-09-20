using System.Text.Json;

namespace Winix;

public sealed class WinixService(StateStore store, ICommandRunner runner)
{
    public StateStore Store => store;
    public VmManager Vms { get; } = new(store, runner);
    public GuestConnection Guest(string name) => new(store.Read<VmRecord>("vms", name), runner);
    public Task<List<Check>> Doctor(CancellationToken ct = default) => Winix.Doctor.Run(runner, store, ct);
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
    public async Task<JsonElement> Sync(string vm, string root, string configFile)
    {
        var config = WinixConfig.Load(configFile);
        var archive = Path.Combine(store.Root, "sync-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await WorkspaceSync.Pack(runner, root, config.Workspace, config.Exclude, archive);
            await Guest(vm).Upload(archive, Path.GetFileName(archive));
            return await Guest(vm).Call("sync", new { archive = Path.GetFileName(archive) });
        }
        finally { if (File.Exists(archive)) File.Delete(archive); }
    }
    public async Task<JobRecord> Run(string vm, string configFile, string taskName)
    {
        var config = WinixConfig.Load(configFile);
        if (!config.Tasks.TryGetValue(taskName, out var task)) throw new WinixException("task_missing", $"Unknown task: {taskName}");
        var id = "job-" + Guid.NewGuid().ToString("N");
        // Persist before submitting; a lost response must not cause a duplicate execution.
        store.Write("jobs", id, new HostJob(vm, config.Workspace, id, DateTimeOffset.UtcNow));
        try { return Wire.Decode<JobRecord>((await Guest(vm).Call("job.start", new JobRequest(id, config.Workspace, task))).GetRawText()); }
        catch (WinixException e) { throw new WinixException(e.Code, $"Job ID {id}: {e.Message} Query this ID before resubmitting; the guest may already have accepted it."); }
    }
    public async Task<JsonElement> Job(string id, string action, long offset = 0)
    {
        var job = store.Read<HostJob>("jobs", id);
        return await Guest(job.Vm).Call("job." + action, new { id, offset });
    }
    public async Task<JobRecord> Wait(string id, CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var record = Wire.Decode<JobRecord>((await Job(id, "status")).GetRawText());
            if (record.Terminal) return record;
            await Task.Delay(1000, ct);
        }
    }
    public async Task<string> Artifacts(string id, string destination)
    {
        var job = store.Read<HostJob>("jobs", id);
        var response = await Guest(job.Vm).Call("job.export", new { id });
        var file = response.GetProperty("file").GetString()!;
        Directory.CreateDirectory(destination);
        var path = Path.Combine(Path.GetFullPath(destination), Paths.Relative(file));
        await Guest(job.Vm).Download(file, path);
        return path;
    }
    public async Task<JsonElement> Ui(string vm, string action, JsonElement arguments) => await Guest(vm).Call("ui." + action, arguments);
    public async Task<object> Ci(string configFile, string root, string destination, CancellationToken ct = default)
    {
        var config = WinixConfig.Load(configFile);
        var name = "ci-" + Guid.NewGuid().ToString("N")[..12];
        List<JobRecord> jobs = []; var created = false; var safeToDestroy = false;
        try
        {
            await Vms.Up(name, config.Image, config.Resources, true); created = true;
            await Ready(name, ct: ct);
            await Sync(name, root, configFile);
            foreach (var task in config.Pipeline)
            {
                var started = await Run(name, configFile, task);
                JobRecord completed;
                try { completed = await Wait(started.Id, ct); }
                catch (OperationCanceledException) { await Job(started.Id, "cancel"); throw; }
                jobs.Add(completed);
                await Artifacts(completed.Id, destination);
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
