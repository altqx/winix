using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Winix;
using Xunit;

namespace Winix.Tests;

public sealed class SyncIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "winix-git-" + Guid.NewGuid().ToString("N"));
    public SyncIntegrationTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public async Task CapturesUncommittedChangesAndUntrackedFilesButNotIgnoredOrSecrets()
    {
        var runner = new CommandRunner();
        await runner.Checked("git", ["init", root]);
        File.WriteAllText(Path.Combine(root, "tracked.txt"), "before");
        await runner.Checked("git", ["-C", root, "add", "tracked.txt"]);
        File.WriteAllText(Path.Combine(root, "tracked.txt"), "after");
        File.WriteAllText(Path.Combine(root, "untracked.txt"), "new");
        File.WriteAllText(Path.Combine(root, ".gitignore"), "ignored.txt\n");
        File.WriteAllText(Path.Combine(root, "ignored.txt"), "no");
        File.WriteAllText(Path.Combine(root, ".env"), "secret");
        var archive = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        var guest = Path.Combine(root, "guest");
        try
        {
            var manifest = await WorkspaceSync.Pack(runner, root, "app", [".env"], archive);
            Assert.Contains(manifest.Files, f => f.Path == "untracked.txt");
            Assert.DoesNotContain(manifest.Files, f => f.Path is ".env" or "ignored.txt");
            WorkspaceSync.Apply(archive, guest);
            Assert.Equal("after", File.ReadAllText(Path.Combine(guest, "app", "tracked.txt")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(guest, "app", "untracked.txt")));
        }
        finally { File.Delete(archive); }
    }

    [Fact]
    public void TamperedArchiveFailsBeforeChangingWorkspace()
    {
        var archive = Path.Combine(root, "bad.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var manifest = new SyncManifest("app", [new("test.txt", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("expected"))).ToLowerInvariant())]);
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) writer.Write(Wire.Encode(manifest));
            using (var writer = new StreamWriter(zip.CreateEntry("files/test.txt").Open())) writer.Write("tampered");
        }
        Assert.Throws<WinixException>(() => WorkspaceSync.Apply(archive, Path.Combine(root, "work")));
        Assert.False(File.Exists(Path.Combine(root, "work", "app", "test.txt")));
    }
}
