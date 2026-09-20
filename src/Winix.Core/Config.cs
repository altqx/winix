using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Winix;

public sealed class WinixConfig
{
    public int Version { get; set; } = 1;
    public string Image { get; set; } = "windows-dev";
    public string Workspace { get; set; } = "app";
    public Resources Resources { get; set; } = new();
    public List<string> Exclude { get; set; } = [".git", ".winix", ".env", "*.pfx", "*.key"];
    public Dictionary<string, TaskSpec> Tasks { get; set; } = new();
    public List<string> Pipeline { get; set; } = ["build", "test"];

    public static WinixConfig Load(string file)
    {
        if (!File.Exists(file)) throw new WinixException("config_missing", $"No configuration at {file}. Run winix init.");
        var value = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithDuplicateKeyChecking().Build().Deserialize<WinixConfig>(File.ReadAllText(file));
        value.Validate();
        return value;
    }
    public void Validate()
    {
        if (Version != 1) throw new WinixException("config_version", "Only config version 1 is supported.");
        Paths.Id(Image); Paths.Id(Workspace);
        if (Resources.Cpus is < 1 or > 64 || Resources.MemoryGiB is < 4 or > 256 || Resources.DiskGiB is < 64 or > 2048)
            throw new WinixException("invalid_resources", "Expected 1–64 CPUs, 4–256 GiB RAM and 64–2048 GiB disk.");
        foreach (var (name, task) in Tasks) { Paths.Id(name); task.Validate(); }
        foreach (var name in Pipeline)
            if (!Tasks.ContainsKey(name)) throw new WinixException("invalid_pipeline", $"Unknown pipeline task: {name}");
    }
}

public sealed class Resources
{
    public int Cpus { get; set; } = 6;
    public int MemoryGiB { get; set; } = 12;
    public int DiskGiB { get; set; } = 180;
}
public sealed class TaskSpec
{
    public string? Executable { get; set; }
    public List<string> Arguments { get; set; } = [];
    public string? Script { get; set; }
    public string WorkingDirectory { get; set; } = ".";
    public int TimeoutSeconds { get; set; } = 1800;
    public bool VisualStudio { get; set; } = true;
    public string Architecture { get; set; } = "x64";
    public Dictionary<string, string> Environment { get; set; } = new();
    public List<string> Artifacts { get; set; } = [];
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Executable) == string.IsNullOrWhiteSpace(Script))
            throw new WinixException("invalid_task", "Specify exactly one of executable or script.");
        if (TimeoutSeconds is < 1 or > 86400) throw new WinixException("invalid_timeout", "Timeout must be 1–86400 seconds.");
        if (Architecture is not ("x64" or "x86")) throw new WinixException("invalid_arch", "Architecture must be x64 or x86.");
        Paths.Relative(WorkingDirectory, allowDot: true);
        foreach (var artifact in Artifacts) Paths.Relative(artifact);
    }
}

public static partial class Paths
{
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$")]
    private static partial Regex Identifier();
    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Reserved();
    public static string Id(string value) => Identifier().IsMatch(value) ? value : throw new WinixException("invalid_id", $"Invalid identifier: {value}");
    public static string Relative(string value, bool allowDot = false)
    {
        if (allowDot && value == ".") return value;
        var parts = value.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.') ||
            p.Any(c => c < 32 || "<>:\"|?*".Contains(c)) || Reserved().IsMatch(p)))
            throw new WinixException("invalid_path", $"Not a portable Windows relative path: {value}");
        return string.Join('/', parts);
    }
    public static string Under(string root, string relative, bool allowDot = false)
    {
        var normalized = Relative(relative, allowDot);
        var fullRoot = Path.GetFullPath(root);
        var result = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var cursor = fullRoot;
        if (File.GetAttributes(fullRoot).HasFlag(FileAttributes.ReparsePoint))
            throw new WinixException("symlink", $"Reparse point is not allowed: {cursor}");
        foreach (var part in normalized.Split('/'))
        {
            cursor = Path.Combine(cursor, part);
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && File.GetAttributes(cursor).HasFlag(FileAttributes.ReparsePoint))
                throw new WinixException("symlink", $"Reparse point is not allowed: {cursor}");
        }
        return result;
    }
}
