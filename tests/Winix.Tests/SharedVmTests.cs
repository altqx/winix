using System.Text.Json;
using Winix;
using Xunit;

namespace Winix.Tests;

/// <summary>Behaviour added after 0.2.0, when seven agents shared one VM: jobs that outlive their owner, leases, work trees, snapshots.</summary>
public sealed class SharedVmTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "winix-shared-" + Guid.NewGuid().ToString("N"));
    public SharedVmTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    /// <summary>Answers guest RPCs by method (read from the request on stdin) and fakes virsh/qemu-img.</summary>
    private sealed class GuestRunner(Func<string, JsonElement, object> guest, Func<string, string[], ProcessResult>? other = null) : ICommandRunner
    {
        public List<(string Method, JsonElement Arguments)> Requests { get; } = [];
        public List<(string Tool, string[] Args)> Commands { get; } = [];
        public Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, string? input = null, string? cwd = null, CancellationToken cancellationToken = default)
        {
            var args = arguments.ToArray();
            if (executable != "ssh" || input is null)
            {
                Commands.Add((executable, args));
                return Task.FromResult(other?.Invoke(executable, args) ?? new ProcessResult(0, "", ""));
            }
            var request = Wire.Decode<GuestRequest>(input);
            lock (Requests) Requests.Add((request.Method, request.Arguments));
            object result;
            try { result = guest(request.Method, request.Arguments); }
            catch (WinixException e) { return Task.FromResult(new ProcessResult(0, Wire.Encode(new GuestResponse(false, Wire.Element(new { }), e.Code, e.Message)), "")); }
            return Task.FromResult(new ProcessResult(0, Wire.Encode(new GuestResponse(true, Wire.Element(result))), ""));
        }
    }
    private StateStore StoreWithVm()
    {
        var store = new StateStore(root); var directory = store.Dir("vms", "dev");
        store.Write("vms", "dev", new VmRecord("dev", "base", directory, 2222, Guid.NewGuid().ToString()));
        File.WriteAllText(Path.Combine(directory, "hostkey.pub"), "ssh-ed25519 test");
        return store;
    }

    // ---- Leases ----

    [Fact]
    public void IdleLeasesEndUnlessAJobKeepsThemBusy()
    {
        var store = new StateStore(root); var service = new WinixService(store, new CommandRunner());
        var now = DateTimeOffset.UtcNow;
        store.Write("leases", "dev", new LeaseRecord("dev", "gone", now.AddHours(-1), now.AddHours(3), LastActivity: now.AddMinutes(-31), IdleSeconds: 1800));
        Assert.Null(service.Lease("dev"));
        service.Acquire("dev", "next", TimeSpan.FromHours(1)); // the idle holder no longer blocks anyone
        store.Write("leases", "dev", new LeaseRecord("dev", "busy", now.AddHours(-1), now.AddHours(3), LastActivity: now.AddMinutes(-31), IdleSeconds: 1800,
            BusyJob: "job-1", BusyUntil: now.AddMinutes(10)));
        Assert.Equal("busy", service.Lease("dev")!.Owner);
        Assert.Equal(now.AddMinutes(40), service.Lease("dev")!.EndsAt);
        store.Write("leases", "dev", new LeaseRecord("dev", "forever", now.AddHours(-1), now.AddHours(3), LastActivity: now.AddHours(-1), IdleSeconds: 0));
        Assert.NotNull(service.Lease("dev")); // idle 0 disables the idle timeout
    }

    [Fact]
    public void RenewingIsNotActivityAndCannotPassTheMaximumTtl()
    {
        var store = new StateStore(root); var service = new WinixService(store, new CommandRunner());
        var fresh = service.Acquire("dev", "gate", TimeSpan.FromDays(2));
        Assert.Equal(fresh.AcquiredAt + WinixService.MaxLeaseTtl, fresh.ExpiresAt);
        Assert.Equal((int)WinixService.DefaultLeaseIdle.TotalSeconds, fresh.IdleSeconds);
        var now = DateTimeOffset.UtcNow; var quiet = now.AddMinutes(-20);
        store.Write("leases", "dev", fresh with { AcquiredAt = now.AddHours(-7.5), LastActivity = quiet });
        var renewed = service.Acquire("dev", "gate", TimeSpan.FromHours(2));
        Assert.Equal(quiet, renewed.LastActivity);
        Assert.Equal(now.AddHours(-7.5) + WinixService.MaxLeaseTtl, renewed.ExpiresAt);
        service.RequireAccess("dev", "gate"); // a real operation by the holder is activity
        Assert.True(service.Lease("dev")!.LastActivity > quiet);
        Assert.Throws<WinixException>(() => service.Acquire("dev", "gate", TimeSpan.FromHours(1), idle: TimeSpan.FromDays(2)));
    }

    [Fact]
    public async Task WaitersAreServedInArrivalOrder()
    {
        var store = new StateStore(root); var service = new WinixService(store, new CommandRunner());
        service.Acquire("dev", "a", TimeSpan.FromHours(1));
        var b = service.AcquireWait("dev", "b", TimeSpan.FromHours(1), "second", null, TimeSpan.FromSeconds(60));
        await Until(() => service.Waiters("dev").Count == 1);
        var c = service.AcquireWait("dev", "c", TimeSpan.FromHours(1), null, null, TimeSpan.FromSeconds(60));
        await Until(() => service.Waiters("dev").Count == 2);
        var status = JsonSerializer.SerializeToElement(service.LeaseStatus("dev"), Wire.Json);
        Assert.Equal(["b", "c"], status.GetProperty("waiting").EnumerateArray().Select(w => w.GetProperty("owner").GetString()));
        service.Release("dev", "a");
        Assert.Equal("b", (await b).Owner);
        Assert.False(c.IsCompleted);
        Assert.Equal(["c"], service.Waiters("dev").Select(w => w.Owner));
        service.Release("dev", "b");
        // Free, but c is queued first: a caller that did not queue cannot jump ahead.
        var jumped = Assert.Throws<WinixException>(() => service.Acquire("dev", "d", TimeSpan.FromHours(1)));
        Assert.Equal("vm_leased", jumped.Code);
        Assert.Equal("c", (await c).Owner);
        Assert.Empty(service.Waiters("dev"));
    }

    [Fact]
    public async Task AWaiterThatGivesUpOrDiesLeavesTheQueue()
    {
        var store = new StateStore(root); var service = new WinixService(store, new CommandRunner());
        service.Acquire("dev", "a", TimeSpan.FromHours(1));
        var timeout = await Assert.ThrowsAsync<WinixException>(() => service.AcquireWait("dev", "b", TimeSpan.FromHours(1), null, null, TimeSpan.FromSeconds(1)));
        Assert.Equal("lease_timeout", timeout.Code);
        Assert.Empty(service.Waiters("dev"));
        service.Release("dev", "a");
        store.Write("lease-queue", "dev", new LeaseQueue([new("crashed", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-1))]));
        Assert.Empty(service.Waiters("dev"));
        Assert.Equal("c", service.Acquire("dev", "c", TimeSpan.FromHours(1)).Owner);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(100);
        Assert.True(condition());
    }

    // ---- Sources, work trees, run parameters ----

    [Fact]
    public async Task SyncRecordsCommitBranchAndDirtyFlagThroughToTheGuestManifest()
    {
        var runner = new CommandRunner(); var source = Path.Combine(root, "src"); Directory.CreateDirectory(source);
        await runner.Checked("git", ["init", "-q", "-b", "feature/docking", source]);
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");
        await runner.Checked("git", ["-C", source, "add", "."]);
        await runner.Checked("git", ["-C", source, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "one"]);
        var head = await runner.Checked("git", ["-C", source, "rev-parse", "HEAD"]);
        var clean = await WorkspaceSync.Source(runner, source);
        Assert.Equal(new SourceInfo(head, "feature/docking", false), clean);
        Assert.Equal($"feature/docking@{head[..12]}", clean.Describe);
        File.WriteAllText(Path.Combine(source, "a.txt"), "changed");
        var dirty = await WorkspaceSync.Source(runner, source);
        Assert.True(dirty.Dirty); Assert.EndsWith("+dirty", dirty.Describe);
        var packed = await WorkspaceSync.PackIncremental(runner, source, "docking", [], Path.Combine(root, "1.zip"), null, dirty);
        var applied = WorkspaceSync.Apply(Path.Combine(root, "1.zip"), Path.Combine(root, "guest"));
        Assert.Equal(dirty, applied.Source);
        Assert.Equal(dirty, Wire.Decode<SyncManifest>(File.ReadAllText(Path.Combine(root, "guest", "docking.manifest.json"))).Source);
        var plain = Path.Combine(root, "plain"); Directory.CreateDirectory(plain);
        Assert.Null((await WorkspaceSync.Source(runner, plain)).Commit);
    }

    [Fact]
    public void SyncResultFlagsAWorkTreeSharedAcrossBranches()
    {
        var manifest = new SyncManifest("app", []);
        Assert.True(new SyncResult("app", 0, 0, 0, 0, 0, 0, 0, true, manifest, new("b", "two", false), new("a", "one", false)).BranchChanged);
        Assert.False(new SyncResult("app", 0, 0, 0, 0, 0, 0, 0, true, manifest, new("b", "one", true), new("a", "one", false)).BranchChanged);
        Assert.False(new SyncResult("app", 0, 0, 0, 0, 0, 0, 0, false, manifest, new("b", "one", false)).BranchChanged);
    }

    [Fact]
    public void RunTimeoutOverridesTheTaskAndTeardownExtendsTheBudget()
    {
        var file = Path.Combine(root, "winix.yaml");
        File.WriteAllText(file, """
            version: 1
            tasks:
              gate: { scriptFile: tools/gate.ps1, timeoutSeconds: 120, teardown: 'Stop-Process -Name HikariSub', teardownTimeoutSeconds: 30 }
            pipeline: []
            """);
        var gate = WinixConfig.Load(file).Tasks["gate"];
        Assert.Equal("Stop-Process -Name HikariSub", gate.Teardown); Assert.Equal(150, gate.BudgetSeconds);
        var longer = gate.Effective(new([], [], 1200));
        Assert.Equal(1200, longer.TimeoutSeconds); Assert.Equal(1230, longer.BudgetSeconds); Assert.Equal(gate.Teardown, longer.Teardown);
        Assert.Equal(120, gate.Effective(null).TimeoutSeconds);
        Assert.Throws<WinixException>(() => gate.Effective(new([], [], 0)).Validate());
        Assert.Throws<WinixException>(() => new TaskSpec { Script = "x", TeardownTimeoutSeconds = 0 }.Validate());
        Assert.DoesNotContain("budget", Wire.Encode(gate), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunUsesTheChosenWorkspaceAndRecordsOwnerTimeoutAndSource()
    {
        var store = StoreWithVm();
        var config = Path.Combine(root, "winix.yaml");
        File.WriteAllText(config, "version: 1\nworkspace: app\ntasks:\n  test: { script: ctest, timeoutSeconds: 600 }\npipeline: []\n");
        var synced = new SourceInfo("0123456789abcdef", "feature-x", false);
        store.Write("sync", "dev-feature-x", new SyncState(new("feature-x", [], synced), []));
        JobRequest? submitted = null;
        var runner = new GuestRunner((method, args) =>
        {
            Assert.Equal("job.start", method);
            submitted = Wire.Decode<JobRequest>(args.GetRawText());
            return new JobRecord(submitted.Id, submitted.Workspace, JobState.Queued, DateTimeOffset.UtcNow, TimeoutSeconds: submitted.Task.TimeoutSeconds);
        });
        var service = new WinixService(store, runner);
        service.Acquire("dev", "agent-1", TimeSpan.FromHours(1));
        var job = await service.Run("dev", config, "test", new([], [], 42), owner: "agent-1", workspace: "feature-x");
        Assert.Equal("feature-x", submitted!.Workspace); Assert.Equal(42, submitted.Task.TimeoutSeconds);
        var host = service.HostJob(job.Id);
        Assert.Equal(("agent-1", 42, synced), (host.Owner, host.TimeoutSeconds, host.Source));
        var lease = service.Lease("dev")!;
        Assert.Equal(job.Id, lease.BusyJob); Assert.True(lease.BusyUntil > DateTimeOffset.UtcNow.AddSeconds(42));
        await Assert.ThrowsAsync<WinixException>(() => service.Run("dev", config, "test", owner: "agent-1", workspace: "../escape"));
    }

    // ---- Jobs that will not stop ----

    [Fact]
    public void DescribeShowsElapsedAgainstTheTimeoutAndFlagsOverdueJobs()
    {
        var store = new StateStore(root); var service = new WinixService(store, new CommandRunner());
        var started = DateTimeOffset.UtcNow.AddHours(-2);
        store.Write("jobs", "job-1", new HostJob("dev", "app", "job-1", started, Owner: "agent-1", TimeoutSeconds: 120, Source: new("abc", "main", false)));
        var view = service.Describe(new JobRecord("job-1", "app", JobState.Running, started, StartedAt: started));
        Assert.Equal("job", view["type"]); Assert.Equal("dev", view["vm"]); Assert.Equal("agent-1", view["owner"]);
        Assert.Equal(120, view["timeoutSeconds"]); Assert.True((int)view["elapsedSeconds"]! >= 7199);
        Assert.True((int)view["remainingSeconds"]! < 0); Assert.Equal(true, view["overdue"]);
        Assert.Contains("job cancel job-1", (string)view["hint"]!);
        Assert.Equal("abc", ((JsonElement)view["source"]!).GetProperty("commit").GetString());
        var fresh = service.Describe(new JobRecord("job-2", "app", JobState.Running, DateTimeOffset.UtcNow, StartedAt: DateTimeOffset.UtcNow, TimeoutSeconds: 600));
        Assert.Equal(false, fresh["overdue"]);
        var done = service.Describe(new JobRecord("job-3", "app", JobState.Succeeded, started, started.AddSeconds(30), StartedAt: started));
        Assert.Equal(30, done["elapsedSeconds"]); Assert.False(done.ContainsKey("overdue"));
    }

    [Fact]
    public async Task WaitStopsAJobRunningPastItsTimeout()
    {
        var store = StoreWithVm();
        var started = DateTimeOffset.UtcNow.AddHours(-3);
        store.Write("jobs", "job-1", new HostJob("dev", "app", "job-1", started, TimeoutSeconds: 120));
        var state = JobState.Running;
        var runner = new GuestRunner((method, args) =>
        {
            if (method == "job.cancel" && args.GetProperty("force").GetBoolean() && args.GetProperty("reason").GetString() == "timed_out") state = JobState.TimedOut;
            return new JobRecord("job-1", "app", state, started, StartedAt: started, TimeoutSeconds: 120);
        });
        var record = await new WinixService(store, runner).Wait("job-1", CancellationToken.None);
        Assert.Equal(JobState.TimedOut, record.State);
        Assert.Single(runner.Requests, r => r.Method == "job.cancel");
    }

    [Fact]
    public async Task CancelReportsItsStepsAndLetsTheOwnerStopTheirJobUnderAnotherLease()
    {
        var store = StoreWithVm();
        store.Write("jobs", "job-1", new HostJob("dev", "app", "job-1", DateTimeOffset.UtcNow, Owner: "agent-1"));
        var state = JobState.Running;
        var runner = new GuestRunner((method, _) =>
        {
            if (method == "job.cancel") state = JobState.Cancelled;
            return new JobRecord("job-1", "app", state, DateTimeOffset.UtcNow);
        });
        var service = new WinixService(store, runner);
        service.Acquire("dev", "agent-2", TimeSpan.FromHours(1));
        await Assert.ThrowsAsync<WinixException>(() => service.Cancel("job-1", owner: "agent-3"));
        var result = JsonSerializer.SerializeToElement(await service.Cancel("job-1", owner: "agent-1"), Wire.Json);
        Assert.Equal(["cancel"], result.GetProperty("escalation").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal("cancelled", result.GetProperty("job").GetProperty("state").GetString());
        state = JobState.Running;
        var forced = JsonSerializer.SerializeToElement(await service.Cancel("job-1", force: true, owner: "agent-2"), Wire.Json);
        Assert.Equal(["kill"], forced.GetProperty("escalation").EnumerateArray().Select(s => s.GetString()));
        Assert.True(runner.Requests.Last(r => r.Method == "job.cancel").Arguments.GetProperty("force").GetBoolean());
    }

    // ---- Snapshots ----

    [Fact]
    public async Task SnapshotsCaptureDiskFirmwareAndTpmOfAShutOffVm()
    {
        var store = StoreWithVm(); var directory = store.Dir("vms", "dev");
        File.WriteAllText(Path.Combine(directory, "nvram.fd"), "vars-1");
        Directory.CreateDirectory(Path.Combine(directory, "tpm", "tpm2")); File.WriteAllText(Path.Combine(directory, "tpm", "tpm2", "state"), "tpm-1");
        File.WriteAllText(Path.Combine(directory, "devices.json"), "[]");
        var domain = "shut off";
        var runner = new GuestRunner((_, _) => new { }, (tool, args) => tool == "virsh" && args.Contains("domstate") ? new(0, domain + "\n", "") : new(0, "", ""));
        var service = new WinixService(store, runner);
        var snapshot = await service.Snapshot("dev", "baseline", "clean fonts", null);
        Assert.Equal(("baseline", "clean fonts"), (snapshot.Name, snapshot.Note));
        Assert.Contains(runner.Commands, c => c.Tool == "qemu-img" && c.Args.SequenceEqual(["snapshot", "-c", "winix-baseline", Path.Combine(directory, "disk.qcow2")]));
        Assert.Equal("baseline", Assert.Single(service.Vms.Snapshots("dev")).Name);
        await Assert.ThrowsAsync<WinixException>(() => service.Snapshot("dev", "baseline", null, null));
        File.WriteAllText(Path.Combine(directory, "nvram.fd"), "vars-2"); File.WriteAllText(Path.Combine(directory, "tpm", "tpm2", "state"), "tpm-2");
        File.WriteAllText(Path.Combine(directory, "tpm", "stray"), "x");
        await service.Revert("dev", "baseline", null, restart: false, Path.Combine(root, "winix.yaml"));
        Assert.Contains(runner.Commands, c => c.Tool == "qemu-img" && c.Args.Take(3).SequenceEqual(["snapshot", "-a", "winix-baseline"]));
        Assert.Equal("vars-1", File.ReadAllText(Path.Combine(directory, "nvram.fd")));
        Assert.Equal("tpm-1", File.ReadAllText(Path.Combine(directory, "tpm", "tpm2", "state")));
        Assert.False(File.Exists(Path.Combine(directory, "tpm", "stray"))); Assert.False(File.Exists(Path.Combine(directory, "devices.json")));
        domain = "running";
        Assert.Equal("vm_running", (await Assert.ThrowsAsync<WinixException>(() => service.Snapshot("dev", "later", null, null))).Code);
        Assert.Equal("vm_running", (await Assert.ThrowsAsync<WinixException>(() => service.Revert("dev", "baseline", null, false, "winix.yaml"))).Code);
        domain = "shut off";
        Assert.Equal("snapshot_missing", (await Assert.ThrowsAsync<WinixException>(() => service.Revert("dev", "nope", null, false, "winix.yaml"))).Code);
        await service.SnapshotDelete("dev", "baseline", null);
        Assert.Empty(service.Vms.Snapshots("dev"));
        service.Acquire("dev", "gate", TimeSpan.FromHours(1));
        Assert.Equal("vm_leased", (await Assert.ThrowsAsync<WinixException>(() => service.Snapshot("dev", "other", null, "someone-else"))).Code);
    }

    // ---- Interrupts ----

    [Fact]
    public async Task ChildCommandsRunInTheirOwnSessionSoATerminalInterruptMissesThem()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/setsid") && !File.Exists("/bin/setsid")) return;
        static string Session(string stat) => stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[3];
        var child = await new CommandRunner().Checked("sh", ["-c", "cat /proc/$$/stat"]);
        Assert.NotEqual(Session(File.ReadAllText("/proc/self/stat")), Session(child));
        var missing = await Assert.ThrowsAsync<WinixException>(() => new CommandRunner().Run("winix-no-such-tool", []));
        Assert.Equal("command_unavailable", missing.Code);
    }

    [Fact]
    public async Task AGuestCallIsNotStartedAfterAnInterruptButOneInFlightIsNotCut()
    {
        var store = StoreWithVm();
        using var interrupt = new CancellationTokenSource();
        var runner = new GuestRunner((_, _) => { interrupt.Cancel(); return new { started = true }; });
        var guest = new WinixService(store, runner).Guest("dev");
        var result = await guest.Call("job.start", new { }, interrupt.Token);
        Assert.True(result.GetProperty("started").GetBoolean()); // the job ID still reaches the caller
        await Assert.ThrowsAsync<OperationCanceledException>(() => guest.Call("health", new { }, interrupt.Token));
        Assert.Single(runner.Requests);
    }
}
