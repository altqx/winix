namespace Winix;

public sealed class StateStore
{
    public string Root { get; }
    public StateStore(string? root = null)
    {
        Root = Path.GetFullPath(root ?? System.Environment.GetEnvironmentVariable("WINIX_HOME") ??
            Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "winix"));
        Directory.CreateDirectory(Root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public string Dir(string category, string id) => Path.Combine(Root, Paths.Id(category), Paths.Id(id));
    public bool Exists(string category, string id) => File.Exists(Path.Combine(Dir(category, id), "record.json"));
    public T Read<T>(string category, string id) => Exists(category, id)
        ? Wire.Decode<T>(File.ReadAllText(Path.Combine(Dir(category, id), "record.json")))
        : throw new WinixException("not_found", $"No {category} entry named {id}.");
    public T? TryRead<T>(string category, string id) where T : class => Exists(category, id) ? Read<T>(category, id) : null;
    public void Write<T>(string category, string id, T value)
    {
        var dir = Dir(category, id); Directory.CreateDirectory(dir);
        Atomic(Path.Combine(dir, "record.json"), Wire.Encode(value));
    }
    public void Delete(string category, string id)
    {
        var dir = Dir(category, id);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
    public List<T> List<T>(string category)
    {
        var dir = Path.Combine(Root, Paths.Id(category));
        return !Directory.Exists(dir) ? [] : Directory.GetDirectories(dir)
            .Where(d => File.Exists(Path.Combine(d, "record.json")))
            .Select(d => Wire.Decode<T>(File.ReadAllText(Path.Combine(d, "record.json")))).ToList();
    }
    public FileStream Lock() => Lock(TimeSpan.Zero);
    /// <summary>Takes the state lock, retrying for up to <paramref name="wait"/> for holders that only need it briefly (lease bookkeeping).</summary>
    public FileStream Lock(TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            try { return new FileStream(Path.Combine(Root, "state.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(50); }
            catch (IOException) { throw new WinixException("busy", "Another Winix operation is changing VM state. Retry shortly."); }
        }
    }
    public static void Atomic(string path, string text)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
}
