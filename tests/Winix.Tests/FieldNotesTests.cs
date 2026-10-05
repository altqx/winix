using System.Text.Json;
using System.Xml.Linq;
using Winix;
using Xunit;

namespace Winix.Tests;

/// <summary>Behaviour added for the HikariSub field notes: run parameters, device options, leases, incremental sync, pointer safety, wait resilience.</summary>
public sealed class FieldNotesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "winix-notes-" + Guid.NewGuid().ToString("N"));
    public FieldNotesTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void TaskEffectiveMergesEnvAndAppendsArguments()
    {
        var task = new TaskSpec { Executable = "ctest", Arguments = ["-C", "Debug"], Environment = new() { ["A"] = "1", ["B"] = "task" }, Env = new() { ["B"] = "env" } };
        var effective = task.Effective(new(new() { ["B"] = "run", ["C"] = "3" }, ["--verbose"]));
        Assert.Equal(new[] { "-C", "Debug", "--verbose" }, effective.Arguments);
        Assert.Equal("1", effective.Environment["A"]); Assert.Equal("run", effective.Environment["B"]); Assert.Equal("3", effective.Environment["C"]);
        Assert.Equal("task", task.Environment["B"]); // the config task is untouched
        Assert.Throws<WinixException>(() => task.Effective(new(new() { ["BAD NAME"] = "x" }, [])));
    }

    [Fact]
    public void TaskRequiresExactlyOneModeAndAcceptsScriptFile()
    {
        new TaskSpec { ScriptFile = "tools/build.ps1" }.Validate();
        Assert.Throws<WinixException>(() => new TaskSpec { ScriptFile = "../escape.ps1" }.Validate());
        Assert.Throws<WinixException>(() => new TaskSpec { Script = "x", ScriptFile = "y.ps1" }.Validate());
        Assert.Throws<WinixException>(() => new TaskSpec { Script = "x", Detach = true, Elevated = true }.Validate());
    }

    [Fact]
    public void ConfigParsesVmOptionsAndTaskFlags()
    {
        var file = Path.Combine(root, "winix.yaml");
        File.WriteAllText(file, """
            version: 1
            vm: { audio: none, displays: 2, usb: [usb-audio, 'dead:beef'] }
            tasks:
              gate: { scriptFile: tools/gate.ps1, detach: true, env: { QT_SCALE_FACTOR: '1.5' } }
              driver: { script: pnputil, elevated: true, visualStudio: false }
            pipeline: []
            """);
        var config = WinixConfig.Load(file);
        Assert.Equal("none", config.Vm.Audio); Assert.Equal(2, config.Vm.Displays); Assert.Equal(["usb-audio", "dead:beef"], config.Vm.Usb);
        Assert.True(config.Tasks["gate"].Detach); Assert.True(config.Tasks["driver"].Elevated);
        Assert.Equal("1.5", config.Tasks["gate"].Effective(null).Environment["QT_SCALE_FACTOR"]);
        File.WriteAllText(file, "version: 1\nvm: { audio: loud }\n"); Assert.Throws<WinixException>(() => WinixConfig.Load(file));
        File.WriteAllText(file, "version: 1\nvm: { usb: [floppy] }\n"); Assert.Throws<WinixException>(() => WinixConfig.Load(file));
    }

    [Fact]
    public void DomainDeclaresAudioDisplaysAndUsbFromOptions()
    {
        var vm = new VmRecord("dev", "base", root, 2222, Guid.NewGuid().ToString());
        XNamespace qemu = "http://libvirt.org/schemas/domain/qemu/1.0";
        var plain = XDocument.Parse(VmManager.Domain(vm, new(), "/firmware.fd"));
        Assert.Empty(plain.Descendants("sound")); Assert.Single(plain.Descendants("video"));
        Assert.Equal("vga", plain.Descendants("video").Single().Element("model")!.Attribute("type")!.Value);
        var rich = XDocument.Parse(VmManager.Domain(vm, new(), "/firmware.fd", options: new() { Audio = "none", Displays = 2, Usb = ["usb-audio", "1d6b:0003"] }));
        Assert.Equal("ich9", rich.Descendants("sound").Single().Attribute("model")!.Value);
        Assert.Equal("none", rich.Root!.Element("devices")!.Elements("audio").Single().Attribute("type")!.Value);
        Assert.Equal(2, rich.Descendants("video").Count());
        Assert.All(rich.Descendants("video"), v => Assert.Equal("qxl", v.Element("model")!.Attribute("type")!.Value));
        Assert.Equal("0x1d6b", rich.Descendants("hostdev").Single().Descendants("vendor").Single().Attribute("id")!.Value);
        Assert.Contains(rich.Descendants(qemu + "arg"), a => a.Attribute("value")!.Value == "usb-audio,id=winix-boot-usb0,audiodev=audio1");
        Assert.Contains("hostfwd=tcp:127.0.0.1:2222-:22", rich.ToString());
    }

    [Theory]
    [InlineData(@"C:\Winix\work\app\out", true)][InlineData(@"%TEMP%\evidence", true)][InlineData("D:/drivers/qxl.inf", true)]
    [InlineData(@"C:\Winix\..\Windows", false)][InlineData("relative/path", false)][InlineData(@"\\server\share", false)][InlineData(@"C:\bad|name", false)]
    public void GuestPathsMustBeAbsoluteWithoutTraversal(string path, bool valid)
    {
        if (valid) Paths.GuestPath(path); else Assert.Throws<WinixException>(() => Paths.GuestPath(path));
    }

    [Theory]
    [InlineData("90s", 90)][InlineData("30m", 1800)][InlineData("2h", 7200)][InlineData("45", 45)]
    public void DurationsParse(string text, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), Paths.Duration(text));

    [Fact]
    public void DurationRejectsGarbage() => Assert.Throws<WinixException>(() => Paths.Duration("soon"));

    [Fact]
    public void TypedOutputCarriesDiscriminatorFirst()
    {
        var typed = Wire.Typed("job", new JobRecord("job-1", "app", JobState.Succeeded, DateTimeOffset.UtcNow, Label: "gate"));
        Assert.Equal("type", typed.Keys.First()); Assert.Equal("job", typed["type"]);
        Assert.Equal("gate", ((JsonElement)typed["label"]!).GetString());
    }

    [Fact]
    public void LeasesGateOtherOwnersUntilExpiry()
    {
        var store = new StateStore(root); var service = new WinixService(store, new CommandRunner());
        var lease = service.Acquire("dev", "gate", TimeSpan.FromHours(1), "docking gate");
        Assert.Equal("gate", lease.Owner);
        service.RequireAccess("dev", "gate");
        var denied = Assert.Throws<WinixException>(() => service.RequireAccess("dev", "ci")); Assert.Equal("vm_leased", denied.Code);
        Assert.Throws<WinixException>(() => service.RequireAccess("dev", null));
        Assert.Throws<WinixException>(() => service.Acquire("dev", "ci", TimeSpan.FromMinutes(5)));
        Assert.Throws<WinixException>(() => service.Release("dev", "ci"));
        service.Release("dev", null, force: true);
        Assert.Null(service.Lease("dev"));
        store.Write("leases", "dev", new LeaseRecord("dev", "old", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.Null(service.Lease("dev")); // expired leases are ignored
        service.RequireAccess("dev", "anyone");
    }

    private static string Sha(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [Fact]
    public async Task IncrementalPackSendsOnlyChangedFilesAndGuestVerifiesTheRest()
    {
        var runner = new CommandRunner(); var source = Path.Combine(root, "src"); Directory.CreateDirectory(source);
        await runner.Checked("git", ["init", "-q", source]);
        File.WriteAllText(Path.Combine(source, "a.txt"), "a"); File.WriteAllText(Path.Combine(source, "b.txt"), "b"); File.WriteAllText(Path.Combine(source, "c.txt"), "c");
        var guest = Path.Combine(root, "guest");
        var first = await WorkspaceSync.PackIncremental(runner, source, "app", [], Path.Combine(root, "1.zip"), null);
        Assert.Equal(3, first.Sent); Assert.False(first.Incremental);
        WorkspaceSync.Apply(Path.Combine(root, "1.zip"), guest);
        File.WriteAllText(Path.Combine(source, "b.txt"), "B"); File.Delete(Path.Combine(source, "c.txt")); File.WriteAllText(Path.Combine(source, "d.txt"), "d");
        var second = await WorkspaceSync.PackIncremental(runner, source, "app", [], Path.Combine(root, "2.zip"), first.State);
        Assert.True(second.Incremental); Assert.Equal(2, second.Sent); Assert.Equal(1, second.Changed); Assert.Equal(1, second.Added); Assert.Equal(1, second.Removed); Assert.Equal(1, second.Unchanged);
        Assert.Equal(3, second.Manifest.Files.Count);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(root, "2.zip"))) Assert.Equal(["files/b.txt", "files/d.txt", "manifest.json"], zip.Entries.Select(e => e.FullName).Order());
        WorkspaceSync.Apply(Path.Combine(root, "2.zip"), guest);
        Assert.Equal("B", File.ReadAllText(Path.Combine(guest, "app", "b.txt"))); Assert.Equal("d", File.ReadAllText(Path.Combine(guest, "app", "d.txt")));
        Assert.False(File.Exists(Path.Combine(guest, "app", "c.txt"))); Assert.Equal("a", File.ReadAllText(Path.Combine(guest, "app", "a.txt")));
        // A guest that drifted (restored overlay, manual edit) rejects the increment before touching anything.
        File.WriteAllText(Path.Combine(guest, "app", "a.txt"), "tampered");
        File.WriteAllText(Path.Combine(source, "d.txt"), "D");
        var third = await WorkspaceSync.PackIncremental(runner, source, "app", [], Path.Combine(root, "3.zip"), second.State);
        var stale = Assert.Throws<WinixException>(() => WorkspaceSync.Apply(Path.Combine(root, "3.zip"), guest));
        Assert.Equal("incremental_stale", stale.Code); Assert.Equal("d", File.ReadAllText(Path.Combine(guest, "app", "d.txt")));
    }

    [Fact]
    public async Task UnchangedFilesAreNotRehashedWhenSizeAndTimeMatch()
    {
        var runner = new CommandRunner(); var source = Path.Combine(root, "src"); Directory.CreateDirectory(source);
        await runner.Checked("git", ["init", "-q", source]);
        var file = Path.Combine(source, "a.txt"); File.WriteAllText(file, "a");
        var first = await WorkspaceSync.PackIncremental(runner, source, "app", [], Path.Combine(root, "1.zip"), null);
        // Same size and mtime: the cached hash is trusted even though the content differs, so this models the cache path exactly.
        var stat = first.State.Stats["a.txt"] with { Sha256 = Sha("cached") };
        var cached = new SyncState(new("app", [new("a.txt", Sha("cached"))]), new() { ["a.txt"] = stat });
        var second = await WorkspaceSync.PackIncremental(runner, source, "app", [], Path.Combine(root, "2.zip"), cached);
        Assert.Equal(Sha("cached"), second.Manifest.Files.Single().Sha256); Assert.Equal(0, second.Sent);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        var third = await WorkspaceSync.PackIncremental(runner, source, "app", [], Path.Combine(root, "3.zip"), cached);
        Assert.Equal(1, third.Sent);
        Assert.Equal(Sha("a"), third.Manifest.Files.Single().Sha256);
    }

    private sealed class ScriptedRunner(Func<string, string[], ProcessResult> script) : ICommandRunner
    {
        public List<(string Tool, string[] Args)> Calls { get; } = [];
        public Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, string? input = null, string? cwd = null, CancellationToken cancellationToken = default)
        { var args = arguments.ToArray(); Calls.Add((executable, args)); return Task.FromResult(script(executable, args)); }
    }

    [Fact]
    public async Task PointerReleasesTheButtonWhenAStepFails()
    {
        var store = new StateStore(root); store.Write("vms", "dev", new VmRecord("dev", "base", store.Dir("vms", "dev"), 2222, Guid.NewGuid().ToString()));
        var moves = 0;
        var runner = new ScriptedRunner((tool, args) =>
        {
            var command = args.Last();
            if (command.Contains("\"abs\"") && ++moves == 3) return new(1, "", "error: monitor gone");
            return new(0, "{\"return\":{}}", "");
        });
        var input = new HostInput(store, runner, _ => throw new WinixException("transport_error", "offline"));
        var error = await Assert.ThrowsAsync<WinixException>(() => input.Pointer("dev", [new(0, 0), new(100, 100)], "left", hold: true, release: false, steps: 4, delayMs: 0, map: "1920x1080", CancellationToken.None));
        Assert.Equal("qmp_error", error.Code);
        var buttons = runner.Calls.Select(c => c.Args.Last()).Where(c => c.Contains("\"btn\"")).ToList();
        Assert.Equal(2, buttons.Count); Assert.Contains("\"down\":true", buttons[0]); Assert.Contains("\"down\":false", buttons[1]);
    }

    [Fact]
    public async Task PointerMapsPixelsOntoTheTabletRangeAndHonoursHold()
    {
        var store = new StateStore(root); store.Write("vms", "dev", new VmRecord("dev", "base", store.Dir("vms", "dev"), 2222, Guid.NewGuid().ToString()));
        var runner = new ScriptedRunner((_, _) => new(0, "{\"return\":{}}", ""));
        var input = new HostInput(store, runner, _ => throw new WinixException("transport_error", "offline"));
        await input.Pointer("dev", [new(1919, 1079)], "right", hold: true, release: false, steps: 1, delayMs: 0, map: "1920x1080", CancellationToken.None);
        var commands = runner.Calls.Select(c => c.Args.Last()).ToList();
        Assert.Contains(commands, c => c.Contains("\"axis\":\"x\",\"value\":32767") && c.Contains("\"axis\":\"y\",\"value\":32767"));
        Assert.Single(commands, c => c.Contains("\"btn\"")); // held: no release
        await input.Pointer("dev", [], "right", hold: false, release: true, steps: 1, delayMs: 0, map: "1920x1080", CancellationToken.None);
        Assert.Contains("\"down\":false", runner.Calls.Last().Args.Last());
    }

    [Fact]
    public void PointPathsParse()
    {
        Assert.Equal([new(10, 20), new(30, 40)], HostInput.Point.ParsePath("10,20; 30,40"));
        Assert.Throws<WinixException>(() => HostInput.Point.Parse("10;20"));
    }

    [Fact]
    public async Task WaitRetriesTransportLossThenReportsContactLost()
    {
        var store = new StateStore(root); var directory = store.Dir("vms", "dev");
        store.Write("vms", "dev", new VmRecord("dev", "base", directory, 2222, Guid.NewGuid().ToString()));
        File.WriteAllText(Path.Combine(directory, "hostkey.pub"), "ssh-ed25519 test");
        store.Write("jobs", "job-1", new HostJob("dev", "app", "job-1", DateTimeOffset.UtcNow));
        var calls = 0;
        var flaky = new ScriptedRunner((_, _) => ++calls < 3
            ? new(255, "", "Connection timed out during banner exchange")
            : new(0, Wire.Encode(new GuestResponse(true, Wire.Element(new JobRecord("job-1", "app", JobState.Succeeded, DateTimeOffset.UtcNow)))), ""));
        var record = await new WinixService(store, flaky).Wait("job-1", CancellationToken.None, TimeSpan.FromMinutes(5));
        Assert.Equal(JobState.Succeeded, record.State); Assert.Equal(3, calls);
        var dead = new ScriptedRunner((_, _) => new(255, "", "Connection timed out during banner exchange"));
        var lost = await Assert.ThrowsAsync<WinixException>(() => new WinixService(store, dead).Wait("job-1", CancellationToken.None, TimeSpan.FromMilliseconds(1500)));
        Assert.Equal("contact_lost", lost.Code); Assert.Contains("job wait job-1", lost.Message);
    }

    [Fact]
    public async Task WaitForLogFindsMarkersAcrossPages()
    {
        var store = new StateStore(root); var directory = store.Dir("vms", "dev");
        store.Write("vms", "dev", new VmRecord("dev", "base", directory, 2222, Guid.NewGuid().ToString()));
        File.WriteAllText(Path.Combine(directory, "hostkey.pub"), "ssh-ed25519 test");
        store.Write("jobs", "job-1", new HostJob("dev", "app", "job-1", DateTimeOffset.UtcNow));
        var pages = new Queue<LogPage>([new("starting MARK", 13, false), new("ER-UNPLUG now", 26, true)]);
        var runner = new ScriptedRunner((_, _) => new(0, Wire.Encode(new GuestResponse(true, Wire.Element(pages.Count > 0 ? pages.Dequeue() : new LogPage("", 26, true)))), ""));
        Assert.True(await new WinixService(store, runner).WaitForLog("job-1", "MARKER-UNPLUG", TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task SshOptionsMultiplexThroughAShortControlPath()
    {
        var directory = Path.Combine(root, "vm"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "hostkey.pub"), "ssh-ed25519 test");
        var runner = new ScriptedRunner((_, _) => new(0, Wire.Encode(new GuestResponse(true, Wire.Element(new { }))), ""));
        await new GuestConnection(new VmRecord("dev", "base", directory, 2222, Guid.NewGuid().ToString()), runner).Call("health", new { });
        var args = runner.Calls.Single().Args;
        Assert.Contains("ControlMaster=auto", args); Assert.Contains(args, a => a.StartsWith("ControlPath=") && a.EndsWith("winix-ssh-%C"));
        Assert.Contains("StrictHostKeyChecking=yes", args);
    }

    [Fact]
    public void StrayHostDetectionIgnoresThisProcess() => Assert.DoesNotContain(System.Environment.ProcessId, Doctor.StrayMcpHosts());
}
