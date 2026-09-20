using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Winix;
using Xunit;

namespace Winix.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "winix-test-" + Guid.NewGuid().ToString("N"));
    public CoreTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData("../secret")][InlineData("C:\\secret")][InlineData("/absolute")][InlineData("file:stream")]
    [InlineData("a/CON.txt")][InlineData("Lpt9.log")][InlineData("a.")][InlineData("a ")][InlineData("a//b")][InlineData("a\u0000b")]
    public void RejectsPathsWindowsCannotSafelyRepresent(string path) => Assert.Throws<WinixException>(() => Paths.Relative(path));

    [Fact]
    public void NormalizesRelativeWindowsSeparators() => Assert.Equal("src/file.cs", Paths.Relative("src\\file.cs"));

    [Fact]
    public void RejectsSymlinkEscape()
    {
        Directory.CreateSymbolicLink(Path.Combine(root, "escape"), Path.GetTempPath());
        Assert.Throws<WinixException>(() => Paths.Under(root, "escape/secret"));
    }

    [Fact]
    public void StateLockExcludesSecondWriter()
    {
        var store = new StateStore(root); using var guard = store.Lock();
        Assert.Throws<WinixException>(() => new StateStore(root).Lock());
    }

    [Fact]
    public void StateRoundTripsDurableJob()
    {
        var state = new StateStore(root); var record = new HostJob("dev", "app", "job-one", DateTimeOffset.UtcNow);
        state.Write("jobs", record.Id, record);
        Assert.Equal(record, new StateStore(root).Read<HostJob>("jobs", record.Id));
    }

    [Theory]
    [InlineData("qcow2", false, "cp")]
    [InlineData("qcow2", true, "qemu-img")]
    [InlineData("raw", false, "qemu-img")]
    public async Task BaselineCaptureOnlyCopiesStandaloneQcowDisks(string format, bool backed, string captureTool)
    {
        var runner = new DiskRunner(format, backed);
        await ImageBuilder.CaptureDisk(runner, "/source disk", "/baseline disk");
        Assert.Equal(captureTool, runner.Calls[1].Tool);
        Assert.Equal(new[] { "/source disk", "/baseline disk" }, runner.Calls[1].Args.TakeLast(2));
        if (captureTool == "qemu-img") Assert.Contains("convert", runner.Calls[1].Args);
        else
        {
            Assert.Contains("--reflink=auto", runner.Calls[1].Args);
            Assert.Contains("--sparse=auto", runner.Calls[1].Args);
        }
    }

    private sealed class DiskRunner(string format, bool backed) : ICommandRunner
    {
        public List<(string Tool, string[] Args)> Calls { get; } = [];
        public Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, string? input = null, string? cwd = null, CancellationToken cancellationToken = default)
        {
            var args = arguments.ToArray(); Calls.Add((executable, args));
            var info = new Dictionary<string, string> { ["format"] = format };
            if (backed) info["backing-filename"] = "/parent disk";
            return Task.FromResult(new ProcessResult(0, args[0] == "info" ? Wire.Encode(info) : "", ""));
        }
    }

    [Theory]
    [InlineData(false, true)][InlineData(true, false)][InlineData(true, true)]
    public async Task ReadinessRequiresDesktopAndToolchain(bool desktop, bool toolchain)
    {
        var store = new StateStore(root);
        var directory = store.Dir("vms", "dev");
        store.Write("vms", "dev", new VmRecord("dev", "base", directory, 2222, Guid.NewGuid().ToString()));
        File.WriteAllText(Path.Combine(directory, "hostkey.pub"), "ssh-ed25519 test");
        var runner = new HealthRunner(desktop, toolchain);
        var service = new WinixService(store, runner);
        if (desktop && toolchain) Assert.True((await service.Ready("dev", 1)).GetProperty("desktopReady").GetBoolean());
        else Assert.Equal("ready_timeout", (await Assert.ThrowsAsync<WinixException>(() => service.Ready("dev", 1))).Code);
    }

    private sealed class HealthRunner(bool desktop, bool toolchain) : ICommandRunner
    {
        public Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, string? input = null, string? cwd = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessResult(0, Wire.Encode(new GuestResponse(true,
                Wire.Element(new { desktopReady = desktop, toolchain = new { ready = toolchain } }))), ""));
    }

    [Fact]
    public void ConfigRejectsDuplicateKeysAndUnknownFields()
    {
        var file = Path.Combine(root, "winix.yaml");
        File.WriteAllText(file, "version: 1\nversion: 2\n"); Assert.ThrowsAny<Exception>(() => WinixConfig.Load(file));
        File.WriteAllText(file, "version: 1\nresorces: {}\n"); Assert.ThrowsAny<Exception>(() => WinixConfig.Load(file));
    }

    [Fact]
    public void TaskRequiresSingleExecutionMode()
    {
        Assert.Throws<WinixException>(() => new TaskSpec().Validate());
        Assert.Throws<WinixException>(() => new TaskSpec { Script = "test", Executable = "test" }.Validate());
        new TaskSpec { Script = "exit 0" }.Validate();
    }

    [Fact]
    public async Task ProcessRunnerPreservesArgumentsWithoutShellEvaluation()
    {
        var result = await new CommandRunner().Run("/usr/bin/printf", ["%s", "hello; $(touch NEVER) \"two words\""]);
        Assert.Equal(0, result.ExitCode); Assert.Equal("hello; $(touch NEVER) \"two words\"", result.Stdout);
    }

    [Fact]
    public async Task ProcessCancellationReturnsPromptly()
    {
        using var token = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CommandRunner().Run("sleep", ["30"], cancellationToken: token.Token));
    }

    private string Archive(string name, string workspace, params (string Path, string Text)[] files)
    {
        var path = Path.Combine(root, name + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            w.Write(Wire.Encode(new SyncManifest(workspace, files.Select(f => new SyncEntry(f.Path,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(f.Text))).ToLowerInvariant())).ToList())));
        foreach (var (relative, text) in files) { using var w = new StreamWriter(zip.CreateEntry("files/" + relative).Open()); w.Write(text); }
        return path;
    }

    [Fact]
    public void SyncAppliesEditsAndDeletesOnlyPreviouslyManagedFiles()
    {
        var work = Path.Combine(root, "work");
        WorkspaceSync.Apply(Archive("first", "app", ("one.txt", "one"), ("two.txt", "two")), work);
        File.WriteAllText(Path.Combine(work, "app", "build.exe"), "keep");
        WorkspaceSync.Apply(Archive("second", "app", ("two.txt", "changed")), work);
        Assert.False(File.Exists(Path.Combine(work, "app", "one.txt")));
        Assert.Equal("changed", File.ReadAllText(Path.Combine(work, "app", "two.txt")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(work, "app", "build.exe")));
    }

    [Fact]
    public void SyncPreservesUnchangedModificationTime()
    {
        var work = Path.Combine(root, "work");
        WorkspaceSync.Apply(Archive("first", "app", ("one.txt", "one")), work);
        var file = Path.Combine(work, "app", "one.txt"); var time = DateTime.UtcNow.AddDays(-2); File.SetLastWriteTimeUtc(file, time);
        WorkspaceSync.Apply(Archive("second", "app", ("one.txt", "one")), work);
        Assert.Equal(time, File.GetLastWriteTimeUtc(file), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void SyncDoesNotImportFutureZipTimestamps()
    {
        var archive = Archive("future", "app", ("one.txt", "one"));
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
            zip.GetEntry("files/one.txt")!.LastWriteTime = DateTimeOffset.Now.AddHours(7);
        var before = DateTime.UtcNow.AddSeconds(-1);
        WorkspaceSync.Apply(archive, Path.Combine(root, "work"));
        Assert.InRange(File.GetLastWriteTimeUtc(Path.Combine(root, "work", "app", "one.txt")), before, DateTime.UtcNow);
    }

    [Fact]
    public void RejectsCaseCollisionsBeforeDeletingExistingSources()
    {
        var work = Path.Combine(root, "work");
        WorkspaceSync.Apply(Archive("first", "app", ("existing", "keep")), work);
        Assert.Throws<WinixException>(() => WorkspaceSync.Apply(Archive("bad", "app", ("Foo", "a"), ("foo", "b")), work));
        Assert.True(File.Exists(Path.Combine(work, "app", "existing")));
    }

    [Fact]
    public void RejectsArchiveTraversal()
    {
        Assert.Throws<WinixException>(() => WorkspaceSync.Apply(Archive("bad", "app", ("../escape", "bad")), Path.Combine(root, "work")));
        Assert.False(File.Exists(Path.Combine(root, "escape")));
    }

    [Fact]
    public void ExclusionsMatchNestedSecretsAndExtensions()
    {
        Assert.True(WorkspaceSync.Excluded("project/.env", [".env"]));
        Assert.True(WorkspaceSync.Excluded("certs/test.pfx", ["*.pfx"]));
        Assert.False(WorkspaceSync.Excluded("src/file.cpp", ["*.pfx"]));
    }

    [Fact]
    public void DomainUsesLoopbackSshAndOwnFirmwareState()
    {
        var vm = new VmRecord("dev", "base", root, 2222, Guid.NewGuid().ToString());
        var xml = XDocument.Parse(VmManager.Domain(vm, new(), "/firmware.fd"));
        Assert.Equal(Path.Combine(root, "nvram.fd"), xml.Descendants("nvram").Single().Value);
        Assert.Contains("hostfwd=tcp:127.0.0.1:2222-:22", xml.ToString());
        Assert.Empty(xml.Descendants("filesystem"));
        Assert.Equal("utc", xml.Descendants("clock").Single().Attribute("offset")!.Value);
        Assert.Equal("qcow2", xml.Descendants("driver").Single().Attribute("type")!.Value);
    }

    [Fact]
    public void AnswerFileIsValidXmlAndTargetsOnlyVmDiskZero()
    {
        var xml = XDocument.Parse(ImageBuilder.AnswerFile("test&<password>"));
        XNamespace ns = "urn:schemas-microsoft-com:unattend";
        Assert.All(xml.Descendants(ns + "DiskID"), id => Assert.Equal("0", id.Value));
        Assert.Contains(xml.Descendants(ns + "Value"), value => value.Value == "test&<password>");
        Assert.Equal("WINIX-VM", xml.Descendants(ns + "ComputerName").Single().Value);
        Assert.Equal("UTC", xml.Descendants(ns + "TimeZone").Single().Value);
    }

    [Theory]
    [InlineData("abc😀tail")][InlineData("abcéend")][InlineData("ab中end")]
    public void LogPaginationNeverSplitsUnicodeCharacters(string text)
    {
        var file = Path.Combine(root, "output.log"); File.WriteAllText(file, text);
        long offset = 0; string result = "";
        while (true)
        {
            var page = Utf8Logs.Read(file, offset, true, 4); result += page.Text;
            if (page.End) break;
            Assert.True(page.NextOffset > offset); offset = page.NextOffset;
        }
        Assert.Equal(text, result);
    }
}
