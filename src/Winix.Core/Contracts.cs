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
}

public sealed class WinixException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public record Check(string Name, bool Ok, string Detail);
public enum JobState { Queued, Running, Succeeded, Failed, Cancelled, TimedOut, Interrupted }
public record JobRequest(string Id, string Workspace, TaskSpec Task);
public record JobRecord(string Id, string Workspace, JobState State, DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt = null, int? ExitCode = null, string? Error = null)
{
    public bool Terminal => State is not (JobState.Queued or JobState.Running);
}
public record GuestRequest(string Method, JsonElement Arguments);
public record GuestResponse(bool Ok, JsonElement Result, string? Code = null, string? Error = null);
public record SyncEntry(string Path, string Sha256);
public record SyncManifest(string Workspace, List<SyncEntry> Files);
public record VmRecord(string Name, string Image, string Directory, int SshPort, string Uuid, bool Disposable = false);
public record ImageManifest(string Name, string Directory, string DomainXml, string DiskSha256,
    DateTimeOffset CreatedAt, JsonElement Toolchain);
public record HostJob(string Vm, string Workspace, string Id, DateTimeOffset CreatedAt);
public record LogPage(string Text, long NextOffset, bool End);
public record ArtifactInfo(string Path, long Size);
