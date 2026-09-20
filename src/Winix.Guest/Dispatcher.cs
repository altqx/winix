using System.IO.Compression;
using System.Text.Json;
using Winix;

namespace Winix.Guest;

internal sealed class Dispatcher : IDisposable
{
    private readonly JobEngine jobs = new();
    private readonly Desktop desktop = new();
    private readonly SemaphoreSlim mutations = new(1, 1);
    public async Task<object> Call(GuestRequest request)
    {
        var a = request.Arguments;
        var id = a.TryGetProperty("id", out var p) ? p.GetString()! : "";
        if (request.Method == "health") return new { version = "0.1.0", user = System.Environment.UserName, interactive = System.Environment.UserInteractive,
            desktopReady = Desktop.IsUnlocked(), activeJob = jobs.Active, toolchain = File.Exists(@"C:\Winix\toolchain.json") ? Wire.Decode<JsonElement>(File.ReadAllText(@"C:\Winix\toolchain.json")) : Wire.Element(new { }) };
        if (request.Method == "job.status") return jobs.Status(id);
        if (request.Method == "job.logs") return jobs.Logs(id, a.TryGetProperty("offset", out var offset) ? offset.GetInt64() : 0);
        if (request.Method == "job.cancel") return jobs.Cancel(id);
        if (request.Method == "job.export") return jobs.Export(id);
        await mutations.WaitAsync();
        try
        {
            if (request.Method == "job.start") return jobs.Start(Wire.Decode<JobRequest>(a.GetRawText()));
            if (jobs.Active is not null) throw new WinixException("busy", $"Job {jobs.Active} owns the desktop/workspace. Wait or cancel it before mutation.");
            if (request.Method == "sync")
            {
                var path = Paths.Under(Path.Combine(Program.Root, "inbox"), a.GetProperty("archive").GetString()!);
                try { return WorkspaceSync.Apply(path, Path.Combine(Program.Root, "work")); }
                finally { File.Delete(path); }
            }
            if (request.Method.StartsWith("ui.")) return await desktop.Call(request.Method[3..], a);
            throw new WinixException("unknown_method", request.Method);
        }
        finally { mutations.Release(); }
    }
    public void Dispose() { desktop.Dispose(); jobs.Dispose(); mutations.Dispose(); }
}
