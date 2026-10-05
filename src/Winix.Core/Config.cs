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
    public VmOptions Vm { get; set; } = new();
    /// <summary>
    /// Paths and name globs never copied into Windows, in addition to anything git ignores.
    /// Defaults keep local secrets out of the guest: <c>.env</c>, <c>*.pfx</c>, <c>*.key</c>.
    /// </summary>
    public List<string> Exclude { get; set; } = [.. DefaultExclude];
    public static readonly string[] DefaultExclude = [".git", ".winix", ".env", "*.pfx", "*.key"];
    public static readonly string[] AlwaysExclude = [".git", ".winix", "bin", "obj"];
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
        Vm.Validate();
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

/// <summary>Virtual hardware beyond CPU/RAM/disk. Changing these redefines the domain the next time the VM is started from shut off.</summary>
public sealed class VmOptions
{
    /// <summary><c>off</c>/unset: no sound device. <c>none</c>: an ICH9 HDA endpoint backed by QEMU's silent audio backend (WASAPI works, nothing is audible). <c>spice</c>: audio to the SPICE console.</summary>
    public string Audio { get; set; } = "off";
    /// <summary>Number of monitors. More than one uses one QXL device per monitor.</summary>
    public int Displays { get; set; } = 1;
    /// <summary><c>vga</c>, <c>qxl</c> or <c>virtio</c>.</summary>
    public string Video { get; set; } = "vga";
    /// <summary>Extra USB devices present at boot: emulated models (<c>usb-audio</c>, <c>usb-kbd</c>, <c>usb-mouse</c>, <c>usb-tablet</c>, <c>usb-hub</c>) or host devices as <c>VVVV:PPPP</c>.</summary>
    public List<string> Usb { get; set; } = [];
    public static readonly string[] EmulatedUsb = ["usb-audio", "usb-kbd", "usb-mouse", "usb-tablet", "usb-hub"];
    public static bool IsHostUsb(string spec) => Regex.IsMatch(spec, "^[0-9a-fA-F]{4}:[0-9a-fA-F]{4}$");
    public void Validate()
    {
        if (Audio is not ("off" or "none" or "spice")) throw new WinixException("invalid_vm", "vm.audio must be off, none or spice.");
        if (Displays is < 1 or > 4) throw new WinixException("invalid_vm", "vm.displays must be 1–4.");
        if (Video is not ("vga" or "qxl" or "virtio")) throw new WinixException("invalid_vm", "vm.video must be vga, qxl or virtio.");
        foreach (var usb in Usb)
            if (!EmulatedUsb.Contains(usb) && !IsHostUsb(usb)) throw new WinixException("invalid_vm", $"Unknown USB device {usb}; use {string.Join(", ", EmulatedUsb)} or VVVV:PPPP.");
    }
    public bool HasAudioBackend => Audio != "off";
}

public sealed partial class TaskSpec
{
    public string? Executable { get; set; }
    public List<string> Arguments { get; set; } = [];
    public string? Script { get; set; }
    /// <summary>A PowerShell file relative to the workspace root, for tasks too long for a YAML scalar.</summary>
    public string? ScriptFile { get; set; }
    public string WorkingDirectory { get; set; } = ".";
    public int TimeoutSeconds { get; set; } = 1800;
    public bool VisualStudio { get; set; } = true;
    public string Architecture { get; set; } = "x64";
    public Dictionary<string, string> Environment { get; set; } = new();
    /// <summary>Alias of <see cref="Environment"/>; both are merged, then <c>run --env</c> values override.</summary>
    public Dictionary<string, string> Env { get; set; } = new();
    public List<string> Artifacts { get; set; } = [];
    /// <summary>Processes the task starts survive its completion (no kill-on-close job object). For launching an app a later UI step drives.</summary>
    public bool Detach { get; set; }
    /// <summary>Run through the provisioned <c>WinixElevated</c> SYSTEM task instead of the desktop user. Explicit opt-in; no desktop access.</summary>
    public bool Elevated { get; set; }
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_().]*$")] private static partial Regex EnvName();
    public void Validate()
    {
        var modes = new[] { Executable, Script, ScriptFile }.Count(m => !string.IsNullOrWhiteSpace(m));
        if (modes != 1) throw new WinixException("invalid_task", "Specify exactly one of executable, script or scriptFile.");
        if (ScriptFile is not null) Paths.Relative(ScriptFile);
        if (TimeoutSeconds is < 1 or > 86400) throw new WinixException("invalid_timeout", "Timeout must be 1–86400 seconds.");
        if (Architecture is not ("x64" or "x86")) throw new WinixException("invalid_arch", "Architecture must be x64 or x86.");
        Paths.Relative(WorkingDirectory, allowDot: true);
        foreach (var artifact in Artifacts) Paths.Relative(artifact);
        foreach (var key in Environment.Keys.Concat(Env.Keys)) ValidateEnvName(key);
        if (Detach && Elevated) throw new WinixException("invalid_task", "An elevated task cannot also detach.");
    }
    public static string ValidateEnvName(string key) => EnvName().IsMatch(key) ? key : throw new WinixException("invalid_env", $"Invalid environment variable name: {key}");
    /// <summary>The task as submitted: <c>env</c> and <c>environment</c> merged, then per-run values applied.</summary>
    public TaskSpec Effective(RunParameters? parameters)
    {
        var environment = new Dictionary<string, string>(Environment);
        foreach (var (key, value) in Env) environment[key] = value;
        foreach (var (key, value) in parameters?.Env ?? []) environment[ValidateEnvName(key)] = value;
        return new TaskSpec
        {
            Executable = Executable, Arguments = [.. Arguments, .. parameters?.Args ?? []], Script = Script, ScriptFile = ScriptFile,
            WorkingDirectory = WorkingDirectory, TimeoutSeconds = TimeoutSeconds, VisualStudio = VisualStudio, Architecture = Architecture,
            Environment = environment, Artifacts = [.. Artifacts], Detach = Detach, Elevated = Elevated
        };
    }
}

public static partial class Paths
{
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$")]
    private static partial Regex Identifier();
    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Reserved();
    [GeneratedRegex("^(?:[A-Za-z]:|%[A-Za-z_][A-Za-z0-9_()]*%)(?:\\\\|$)")]
    private static partial Regex GuestRoot();
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
    /// <summary>An absolute Windows path (<c>C:\...</c>) or one rooted at an environment variable (<c>%TEMP%\...</c>), without traversal.</summary>
    public static string GuestPath(string value)
    {
        var normalized = value.Replace('/', '\\').TrimEnd('\\');
        var root = GuestRoot().Match(normalized);
        if (!root.Success) throw new WinixException("invalid_guest_path", $"Guest paths must be absolute (C:\\...) or start with %VARIABLE%: {value}");
        var rest = normalized[root.Value.TrimEnd('\\').Length..].TrimStart('\\');
        if (rest.Length > 0) Relative(rest);
        return normalized;
    }
    public static bool IsGuestAbsolute(string value) => GuestRoot().IsMatch(value.Replace('/', '\\'));
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
    /// <summary>Parses durations such as <c>2h</c>, <c>30m</c>, <c>90s</c> or plain seconds.</summary>
    public static TimeSpan Duration(string value)
    {
        var match = Regex.Match(value.Trim(), "^(\\d+)\\s*([smhd]?)$", RegexOptions.IgnoreCase);
        if (!match.Success) throw new WinixException("usage", $"Invalid duration: {value}. Use 90s, 30m, 2h or 1d.");
        var amount = long.Parse(match.Groups[1].Value);
        return match.Groups[2].Value.ToLowerInvariant() switch { "m" => TimeSpan.FromMinutes(amount), "h" => TimeSpan.FromHours(amount), "d" => TimeSpan.FromDays(amount), _ => TimeSpan.FromSeconds(amount) };
    }
}
