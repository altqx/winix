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
    public async Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, string? input = null,
        string? cwd = null, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, UseShellExecute = false, WorkingDirectory = cwd ?? System.Environment.CurrentDirectory };
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
