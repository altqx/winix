using System.Xml.Linq;
using Winix;
using Xunit;

namespace Winix.Tests;

/// <summary>PE inspection, cdb output triage, the debug-tools verbs' argument checks, and the Windows PE stage that turns Defender off.</summary>
public sealed class DebuggingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "winix-debug-" + Guid.NewGuid().ToString("N"));
    public DebuggingTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void InspectsAManagedImage()
    {
        // An AnyCPU assembly is a PE32 image that imports mscoree.dll and carries a CodeView record for its portable PDB.
        var report = PeInspector.Inspect(typeof(PeInspector).Assembly.Location);
        Assert.Equal("PE32", report.Format);
        Assert.Equal("dll", report.Kind);
        Assert.True(report.Mitigations["aslr"]); Assert.True(report.Mitigations["dep"]);
        var import = Assert.Single(report.Imports);
        Assert.Equal("mscoree.dll", import.Dll, ignoreCase: true);
        Assert.Equal(["_CorDllMain"], import.Functions);
        Assert.Contains(report.Sections, s => s.Name == ".text" && s.Access == "r-x");
        var pdb = Assert.Single(report.Pdbs);
        Assert.EndsWith("Winix.Core.pdb", pdb.Path);
        Assert.Equal($"Winix.Core.pdb/{pdb.Guid:N}{pdb.Age:X}".ToUpperInvariant(), pdb.SymbolKey.ToUpperInvariant());
        Assert.NotNull(report.Managed);
        Assert.True(report.Managed!.IlOnly);
        Assert.Equal("Winix.Core", report.Managed.Assembly);
        Assert.StartsWith(".NETCoreApp,Version=v", report.Managed.TargetFramework);
        Assert.Empty(report.Exports);
    }

    [Fact]
    public void RejectsFilesThatAreNotPeImages()
    {
        var file = Path.Combine(root, "notes.exe");
        File.WriteAllText(file, "MZ but nothing else of a PE image");
        Assert.Equal("not_pe", Assert.Throws<WinixException>(() => PeInspector.Inspect(file)).Code);
    }

    [Fact]
    public void ParsesAnalyzeOutput()
    {
        const string text = """
            0:000> !analyze -v
            *******************************************************************************
            EXCEPTION_RECORD:  (.exr -1)
            ExceptionAddress: 00007ff6a1b21234 (App!Crash+0x14)
               ExceptionCode: c0000005 (Access violation)
            PROCESS_NAME:  App.exe
            ERROR_CODE: (NTSTATUS) 0xc0000005 - The instruction at 0x%p referenced memory at 0x%p.
            EXCEPTION_CODE_STR:  c0000005
            STACK_TEXT:
            000000a1`2f6ff8d0 00007ff6`a1b21290 : App!Crash+0x14
            000000a1`2f6ff900 00007ffd`1c2e7374 : App!main+0x20

            FAULTING_SOURCE_LINE_NUMBER:  12
            SYMBOL_NAME:  App!Crash+14
            MODULE_NAME: App
            FAILURE_BUCKET_ID:  NULL_POINTER_WRITE_c0000005_App.exe!Crash
            """;
        var summary = CdbAnalysis.Parse(text);
        Assert.Equal("NULL_POINTER_WRITE_c0000005_App.exe!Crash", summary.Fields["FAILURE_BUCKET_ID"]);
        Assert.Equal("App.exe", summary.Fields["PROCESS_NAME"]);
        Assert.Equal("App!Crash+14", summary.Fields["SYMBOL_NAME"]);
        Assert.Equal("c0000005", summary.Fields["EXCEPTION_CODE_STR"]);
        Assert.Equal("12", summary.Fields["FAULTING_SOURCE_LINE_NUMBER"]);
        Assert.Equal(2, summary.Stack.Count);
        Assert.EndsWith("App!main+0x20", summary.Stack[1]);
        Assert.Empty(CdbAnalysis.Parse("").Fields);
    }

    [Fact]
    public async Task ToolsInstallAndDebugVerbsRejectUnknownNames()
    {
        var service = new WinixService(new StateStore(root), new CommandRunner());
        var install = await Assert.ThrowsAsync<WinixException>(() => service.InstallDebugTools("dev", ["sdk", "ida"], null));
        Assert.Equal("usage", install.Code);
        Assert.Contains("ida", install.Message);
        Assert.Equal("usage", (await Assert.ThrowsAsync<WinixException>(() => service.Debug("dev", "disassemble", new { }, null))).Code);
    }

    [Fact]
    public void RecipeListsTheComponentsTheHostAccepts()
    {
        var recipe = File.ReadAllText(Path.Combine(RepoRoot(), "provisioning", "debugtools.ps1"));
        foreach (var component in WinixService.DebugToolComponents) Assert.Contains($"'{component}'", recipe);
        Assert.Contains("debugtools.ps1", File.ReadAllText(Path.Combine(RepoRoot(), "provisioning", "bootstrap.ps1")));
    }

    [Fact]
    public void PeStageDisablesDefenderOfflineUnlessKept()
    {
        XNamespace ns = "urn:schemas-microsoft-com:unattend";
        string Command(bool keep) => XDocument.Parse(ImageBuilder.AnswerFile("pw", keep)).Descendants(ns + "RunSynchronousCommand").Single().Element(ns + "Path")!.Value;
        Assert.EndsWith(@"call %d:\winix-pe.cmd """, Command(false));
        Assert.EndsWith(@"call %d:\winix-pe.cmd keep-defender""", Command(true));
        Assert.True(Command(true).Length < 260); // the RunSynchronous path limit
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "provisioning", "winix-pe.cmd"));
        Assert.Contains("\r\n", script); // cmd labels need CRLF
        Assert.Contains("echo select disk 0", script);
        Assert.DoesNotContain("select disk 1", script);
        var defender = script[script.IndexOf("keep-defender\" goto handoff", StringComparison.Ordinal)..script.IndexOf(":handoff", StringComparison.Ordinal)];
        foreach (var service in new[] { "WinDefend", "WdFilter", "WdBoot", "WdNisSvc", "WdNisDrv", "Sense" }) Assert.Contains(service, defender);
        Assert.Contains(@"W:\Windows\System32\config\SYSTEM", defender);
        Assert.Contains("/d 4", defender);
        Assert.Contains(@"W:\Windows\Panther\unattend.xml", script);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Winix.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
