using System.Security.Cryptography;

namespace Winix;

public static class Media
{
    // Microsoft CDN URL and SHA-256 also published by dockur/windows src/define.sh.
    public const string WindowsUrl = "https://software-static.download.prss.microsoft.com/dbazure/888969d5-f34g-4e03-ac9d-1f9786c66749/26200.6584.250915-1905.25h2_ge_release_svc_refresh_CLIENT_CONSUMER_x64FRE_en-us.iso";
    public const string WindowsSha256 = "d141f6030fed50f75e2b03e1eb2e53646c4b21e5386047cb860af5223f102a32";
    public const string VirtioUrl = "https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/archive-virtio/virtio-win-0.1.302-1/virtio-win-0.1.302.iso";
    public static async Task<string> Download(ICommandRunner runner, string url, string path, string? expectedHash = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (!File.Exists(path))
        {
            var completePartial = expectedHash is not null && File.Exists(path + ".part") &&
                (await Hash(path + ".part", ct)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
            if (!completePartial)
                await runner.Checked("curl", ["--fail", "--location", "--proto", "=https", "--proto-redir", "=https", "--retry", "3", "--connect-timeout", "30", "--speed-limit", "1024", "--speed-time", "60", "--continue-at", "-", "--output", path + ".part", url], ct: ct);
            var hash = await Hash(path + ".part", ct);
            if (expectedHash is not null && !hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new WinixException("checksum_mismatch", $"Checksum mismatch for {path}. Partial download preserved for inspection.");
            File.Move(path + ".part", path);
        }
        var finalHash = await Hash(path, ct);
        if (expectedHash is not null && !finalHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new WinixException("checksum_mismatch", $"Existing media failed checksum validation: {path}");
        StateStore.Atomic(path + ".json", Wire.Encode(new { url, sha256 = finalHash, verifiedAgainstExpected = expectedHash is not null, downloadedAt = DateTimeOffset.UtcNow }));
        return Path.GetFullPath(path);
    }
    public static async Task<string> Hash(string path, CancellationToken ct = default)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
    }
}
