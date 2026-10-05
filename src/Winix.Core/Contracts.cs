using System.Text.Json;
using System.Text.Json.Serialization;

namespace Winix;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Decode<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw new WinixException("invalid_json", "Empty response.");
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
    /// <summary>A JSON object with a leading <c>type</c> discriminator, for one-object-per-line output.</summary>
    public static Dictionary<string, object?> Typed(string type, object value)
    {
        var result = new Dictionary<string, object?> { ["type"] = type };
        var element = value is JsonElement e ? e : Element(value);
        if (element.ValueKind == JsonValueKind.Object) foreach (var property in element.EnumerateObject()) result[property.Name] = property.Value.Clone();
        else result["value"] = element.Clone();
        return result;
    }
}

public sealed class WinixException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public record Check(string Name, bool Ok, string Detail);
public enum JobState { Queued, Running, Succeeded, Failed, Cancelled, TimedOut, Interrupted }
/// <summary>Per-run values supplied on the command line; merged into the task before submission and kept on the job record.</summary>
public record RunParameters(Dictionary<string, string> Env, List<string> Args);
public record JobRequest(string Id, string Workspace, TaskSpec Task, string? Label = null, RunParameters? Parameters = null);
public record JobRecord(string Id, string Workspace, JobState State, DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt = null, int? ExitCode = null, string? Error = null,
    string? Label = null, RunParameters? Parameters = null, bool Detached = false, bool Elevated = false)
{
    public bool Terminal => State is not (JobState.Queued or JobState.Running);
}
public record GuestRequest(string Method, JsonElement Arguments);
public record GuestResponse(bool Ok, JsonElement Result, string? Code = null, string? Error = null);
public record SyncEntry(string Path, string Sha256);
public record SyncManifest(string Workspace, List<SyncEntry> Files);
/// <summary>Host-side memory of the last successful sync: what the guest holds, plus size/mtime so unchanged files are not rehashed.</summary>
public record FileStat(long Size, long ModifiedTicks, string Sha256);
public record SyncState(SyncManifest Manifest, Dictionary<string, FileStat> Stats);
public record SyncResult(string Workspace, int Files, int Added, int Changed, int Removed, int Unchanged, int Sent, long SentBytes, bool Incremental, SyncManifest Manifest)
{
    public string Summary => $"{Workspace}: {Files} files, {Added} added, {Changed} changed, {Removed} removed, {Unchanged} unchanged; sent {Sent} ({SentBytes / 1024} KiB){(Incremental ? " incrementally" : "")}.";
}
public record LeaseRecord(string Vm, string Owner, DateTimeOffset AcquiredAt, DateTimeOffset ExpiresAt, string? Note = null)
{
    public bool Active => ExpiresAt > DateTimeOffset.UtcNow;
}
public record TransferResult(string Source, string Destination, string Sha256, long Size, bool Directory);
public record VmRecord(string Name, string Image, string Directory, int SshPort, string Uuid, bool Disposable = false);
public record ImageManifest(string Name, string Directory, string DomainXml, string DiskSha256,
    DateTimeOffset CreatedAt, JsonElement Toolchain);
public record HostJob(string Vm, string Workspace, string Id, DateTimeOffset CreatedAt, string? Label = null, RunParameters? Parameters = null);
public record LogPage(string Text, long NextOffset, bool End);
public record ArtifactInfo(string Path, long Size);
public record HotplugDevice(string Id, string Driver, Dictionary<string, string> Properties, DateTimeOffset AddedAt);
