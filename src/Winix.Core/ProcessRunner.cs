using System.Diagnostics;

namespace Winix;

public record ProcessResult(int ExitCode, string Stdout, string Stderr);
public interface ICommandRunner
{
    Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, string? input = null,
        string? cwd = null, CancellationToken cancellationToken = default);
}
public sealed class CommandRunner : ICommandRunner
{
    /// <summary>
    /// On Linux, children run in their own session (setsid), so a Ctrl-C or a SIGINT sent to the caller's process group
    /// never reaches ssh, scp or virsh mid-call; Winix stops them itself when it means to. WINIX_SETSID=0 disables this.
    /// </summary>
    private static readonly string? Setsid = OperatingSystem.IsLinux() && System.Environment.GetEnvironmentVariable("WINIX_SETSID") != "0"
        ? new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists) : null;
    private static bool OnPath(string executable) => executable.Contains('/') ? File.Exists(executable)
        : (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Any(dir => File.Exists(Path.Combine(dir, executable)));
    public async Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, string? input = null,
        string? cwd = null, CancellationToken cancellationToken = default)
    {
        // A missing executable is started directly so it still fails as command_unavailable.
        var detach = Setsid is not null && OnPath(executable);
        var start = new ProcessStartInfo(detach ? Setsid! : executable) { RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, UseShellExecute = false, WorkingDirectory = cwd ?? System.Environment.CurrentDirectory };
        if (detach) { start.ArgumentList.Add("--wait"); start.ArgumentList.Add(executable); }
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception e) { throw new WinixException("command_unavailable", $"Cannot start {executable}: {e.Message}"); }
        using var cancel = cancellationToken.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
        process.StandardInput.Close();
        await process.WaitForExitAsync(cancellationToken);
        return new(process.ExitCode, await stdout, await stderr);
    }
}
public static class CommandExtensions
{
    public static async Task<string> Checked(this ICommandRunner runner, string exe, IEnumerable<string> args,
        string? input = null, string? cwd = null, CancellationToken ct = default)
    {
        var result = await runner.Run(exe, args, input, cwd, ct);
        if (result.ExitCode != 0) throw new WinixException("command_failed", $"{exe} exited {result.ExitCode}: {result.Stderr.Trim()}");
        return result.Stdout.Trim();
    }
}
