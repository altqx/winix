using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Winix;

namespace Winix.Guest;

/// <summary>
/// The explicit elevated path. The desktop worker (a standard user) drops a request into <c>C:\Winix\elevated\&lt;id&gt;</c>
/// and starts the provisioned <c>WinixElevated</c> scheduled task; the task runs <c>winix-guest.exe elevated</c> as SYSTEM,
/// which handles pending requests and writes <c>result.json</c>. Only narrow verbs and tasks flagged <c>elevated: true</c> get here.
/// </summary>
internal static class Elevation
{
    public const string TaskName = "WinixElevated";
    public static string Root => Program.Dir("elevated");
    public static bool Available => Directory.Exists(Path.Combine(Program.Root, "elevated"));

    public static async Task<JsonElement> Submit(string requestDir, CancellationToken ct)
    {
        var resultFile = Path.Combine(requestDir, "result.json");
        var lastTrigger = DateTime.MinValue; var triggered = false;
        while (!File.Exists(resultFile))
        {
            if (ct.IsCancellationRequested) { File.WriteAllText(Path.Combine(requestDir, "cancel"), DateTime.UtcNow.ToString("o")); ct.ThrowIfCancellationRequested(); }
            if (DateTime.UtcNow - lastTrigger > TimeSpan.FromSeconds(5))
            {
                // Retried: a run that started before this request was written may finish without seeing it.
                var result = await new CommandRunner().Run("schtasks.exe", ["/Run", "/TN", TaskName], cancellationToken: CancellationToken.None);
                var text = result.Stdout + result.Stderr;
                if (result.ExitCode != 0 && !triggered && !text.Contains("running", StringComparison.OrdinalIgnoreCase))
                    throw new WinixException("elevation_unavailable", $"The {TaskName} scheduled task cannot be started ({text.Trim()}). Rebuild the image with current provisioning, or install the task by hand; see docs/setup.md.");
                triggered = true; lastTrigger = DateTime.UtcNow;
            }
            await Task.Delay(500, CancellationToken.None);
        }
        await Task.Delay(100, CancellationToken.None); // let the writer's rename settle
        return Wire.Decode<JsonElement>(StateStore.ReadShared(resultFile));
    }

    /// <summary>Entry point of <c>winix-guest.exe elevated</c> (SYSTEM). Processes every pending request, then exits.</summary>
    public static async Task<int> RunPending()
    {
        var root = Path.Combine(Program.Root, "elevated");
        if (!Directory.Exists(root)) return 0;
        var log = Path.Combine(Program.Root, "elevated.log");
        bool processed;
        do
        {
            processed = false;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var requestFile = Path.Combine(dir, "request.json"); var resultFile = Path.Combine(dir, "result.json"); var started = Path.Combine(dir, "started");
                if (!File.Exists(requestFile) || File.Exists(resultFile) || File.Exists(started)) continue;
                File.WriteAllText(started, DateTime.UtcNow.ToString("o"));
                processed = true;
                object result;
                try { result = await Handle(dir, Wire.Decode<JsonElement>(File.ReadAllText(requestFile))); }
                catch (OperationCanceledException) { result = new { ok = false, code = "cancelled", error = "Cancelled or timed out." }; }
                catch (Exception e) { result = new { ok = false, code = (e as WinixException)?.Code ?? "elevated_failed", error = e.Message }; }
                StateStore.Atomic(resultFile, Wire.Encode(result));
                File.AppendAllText(log, $"{DateTime.UtcNow:o} {Path.GetFileName(dir)} {Wire.Encode(result)}\n");
            }
        } while (processed);
        return 0;
    }
    private static async Task<object> Handle(string dir, JsonElement envelope)
    {
        var kind = envelope.GetProperty("kind").GetString();
        using var cancel = new CancellationTokenSource();
        _ = Task.Run(async () => { while (!cancel.IsCancellationRequested) { if (File.Exists(Path.Combine(dir, "cancel"))) cancel.Cancel(); await Task.Delay(500); } });
        switch (kind)
        {
            case "task":
            {
                var request = Wire.Decode<JobRequest>(envelope.GetProperty("request").GetRawText());
                if (!request.Task.Elevated) throw new WinixException("not_elevated", "Only tasks flagged elevated: true run here.");
                var logPath = envelope.GetProperty("logPath").GetString()!;
                var requestFile = Path.Combine(dir, "job-request.json");
                File.WriteAllText(requestFile, Wire.Encode(request));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(request.Task.TimeoutSeconds));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, timeout.Token);
                var exitCode = await JobEngine.RunTask(request, dir, requestFile, logPath, false, linked.Token);
                return new { ok = true, exitCode };
            }
            case "driver-add":
            {
                var inf = envelope.GetProperty("arguments").GetProperty("inf").GetString() ?? throw new WinixException("invalid_args", "inf is required.");
                inf = System.Environment.ExpandEnvironmentVariables(Paths.GuestPath(inf));
                if (!File.Exists(inf) || !inf.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)) throw new WinixException("not_found", $"No .inf at {inf}");
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, timeout.Token);
                var result = await new CommandRunner().Run(Path.Combine(System.Environment.SystemDirectory, "pnputil.exe"), ["/add-driver", inf, "/install"], cancellationToken: linked.Token);
                var ok = result.ExitCode is 0 or 3010 or 259;
                return new { ok, exitCode = result.ExitCode, rebootRequired = result.ExitCode == 3010, output = (result.Stdout + result.Stderr).Trim(), code = ok ? null : "driver_failed", error = ok ? null : $"pnputil exited {result.ExitCode}" };
            }
            default: throw new WinixException("unknown_kind", $"Elevated verb {kind} is not allowed.");
        }
    }
}
