using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Winix;

namespace Winix.Guest;

/// <summary>
/// The tools provisioning/debugtools.ps1 installed, read from <c>C:\Winix\debugtools.json</c> on every use, so a
/// <c>winix guest tools install</c> takes effect without restarting the worker.
/// </summary>
internal static class DebugTools
{
    private static string Inventory => Path.Combine(Program.Root, "debugtools.json");
    private static readonly string Kits = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86), @"Windows Kits\10\Debuggers\x64");
    public static JsonElement? Load()
    {
        try { return File.Exists(Inventory) ? Wire.Decode<JsonElement>(File.ReadAllText(Inventory)) : null; }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }
    /// <summary>A tool's path from the inventory, else its default location; null when it is not installed.</summary>
    public static string? Find(string name)
    {
        if (Load() is { } inventory && inventory.TryGetProperty("tools", out var tools) && tools.TryGetProperty(name, out var path) && path.GetString() is { } p && File.Exists(p)) return p;
        var fallback = Path.Combine(Kits, name + ".exe");
        return File.Exists(fallback) ? fallback : null;
    }
    /// <summary>Puts the tool directories on a job's PATH and gives it the symbol path; values the task set win.</summary>
    public static void Apply(IDictionary<string, string?> environment)
    {
        if (Load() is not { } inventory) return;
        if (inventory.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Array)
        {
            var current = environment.TryGetValue("PATH", out var existing) && existing is not null ? existing : System.Environment.GetEnvironmentVariable("PATH") ?? "";
            var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
            var added = paths.EnumerateArray().Select(p => p.GetString()).OfType<string>().Where(p => !parts.Contains(p, StringComparer.OrdinalIgnoreCase));
            environment["PATH"] = string.Join(';', parts.Concat(added));
        }
        if (inventory.TryGetProperty("symbolPath", out var symbols) && symbols.GetString() is { Length: > 0 } symbolPath &&
            !(environment.TryGetValue("_NT_SYMBOL_PATH", out var set) && !string.IsNullOrEmpty(set)))
            environment["_NT_SYMBOL_PATH"] = symbolPath;
    }
    private static string SymbolPath() =>
        System.Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH") is { Length: > 0 } set ? set
        : Load() is { } inventory && inventory.TryGetProperty("symbolPath", out var s) && s.GetString() is { Length: > 0 } p ? p
        : $"srv*{Path.Combine(Program.Root, "cache", "symbols")}*https://msdl.microsoft.com/download/symbols";

    /// <summary>Runs cdb over a dump file (<c>!analyze -v</c> by default) and keeps the transcript next to the worker's dumps.</summary>
    public static async Task<object> Analyze(string dump, string? commands, int timeoutSeconds)
    {
        if (!File.Exists(dump)) throw new WinixException("not_found", dump);
        var cdb = Find("cdb") ?? throw new WinixException("tool_missing", "cdb.exe is not installed; run winix guest tools install --components sdk.");
        commands = string.IsNullOrWhiteSpace(commands) ? "!analyze -v" : commands.Trim().TrimEnd(';');
        var transcript = Path.Combine(Program.Dir("dumps"), $"{Path.GetFileNameWithoutExtension(dump)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.analyze.txt");
        var start = new ProcessStartInfo(cdb) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "-z", dump, "-lines", "-c", commands + ";q" }) start.ArgumentList.Add(arg);
        start.Environment["_NT_SYMBOL_PATH"] = SymbolPath();
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var timedOut = false;
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { timedOut = true; try { process.Kill(true); } catch (InvalidOperationException) { } }
        var text = await stdout + await stderr;
        File.WriteAllText(transcript, text, new UTF8Encoding(false));
        var summary = CdbAnalysis.Parse(text);
        const int Limit = 48 * 1024;
        return new { dump, commands, transcript, exitCode = timedOut ? (int?)null : process.ExitCode, timedOut, summary = summary.Fields, stack = summary.Stack,
            output = text.Length > Limit ? text[..Limit] : text, truncated = text.Length > Limit,
            hint = timedOut ? "cdb timed out (symbol downloads are slow the first time); retry with a longer timeout." : "Fetch the transcript with winix pull <path> <local>." };
    }

    /// <summary>Dump files the worker or Windows Error Reporting wrote, newest first.</summary>
    public static object Dumps()
    {
        var dirs = new List<string> { Program.Dir("dumps") };
        if (Load() is { } inventory && inventory.TryGetProperty("crashDumps", out var crash) && crash.GetString() is { Length: > 0 } c) dirs.Add(System.Environment.ExpandEnvironmentVariables(c));
        var files = dirs.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.dmp", SearchOption.AllDirectories)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).Take(200)
            .Select(f => new { path = f.FullName, size = f.Length, modifiedAt = f.LastWriteTimeUtc });
        return new { directories = dirs, dumps = files.ToList() };
    }

    /// <summary>The static PE report, plus what only Windows can say: Authenticode status and version resources.</summary>
    public static async Task<object> Pe(string path)
    {
        if (!File.Exists(path)) throw new WinixException("not_found", path);
        var report = PeInspector.Inspect(path);
        var version = FileVersionInfo.GetVersionInfo(path);
        object? signature = null;
        var script = "$s = Get-AuthenticodeSignature -LiteralPath $env:WINIX_PE_PATH; @{ status = [string]$s.Status; message = $s.StatusMessage; signer = $s.SignerCertificate.Subject; " +
            "issuer = $s.SignerCertificate.Issuer; thumbprint = $s.SignerCertificate.Thumbprint; timestamp = $s.TimeStamperCertificate.Subject; catalog = [string]$s.SignatureType } | ConvertTo-Json -Compress";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
            start.Environment["WINIX_PE_PATH"] = path;
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            signature = Wire.Decode<JsonElement>(output);
        }
        catch (Exception e) when (e is OperationCanceledException or JsonException or System.ComponentModel.Win32Exception) { signature = new { status = "Unknown", message = e.Message }; }
        return new { pe = report, signature, version = new { file = version.FileVersion, product = version.ProductVersion, company = version.CompanyName, description = version.FileDescription,
            originalName = version.OriginalFilename, debug = version.IsDebug, prerelease = version.IsPreRelease } };
    }
}
