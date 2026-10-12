using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace Winix;

public record PackResult(SyncManifest Manifest, SyncState State, int Added, int Changed, int Removed, int Unchanged, int Sent, long SentBytes, bool Incremental);

public static class WorkspaceSync
{
    public static bool Excluded(string relative, IEnumerable<string> patterns)
    {
        return patterns.Any(pattern => relative.Split('/').Any(part => Regex.IsMatch(part,
            "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase)) ||
            relative.Equals(pattern, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(pattern.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
    }
    public static async Task<SyncManifest> Pack(ICommandRunner runner, string root, string workspace, IEnumerable<string> excludes, string zipPath)
        => (await PackIncremental(runner, root, workspace, excludes, zipPath, null)).Manifest;

    /// <summary>The commit, branch and dirty flag of the tree being synced. No commit yet, or not a repository: commit is null.</summary>
    public static async Task<SourceInfo> Source(ICommandRunner runner, string root)
    {
        root = Path.GetFullPath(root);
        var head = await runner.Run("git", ["-C", root, "rev-parse", "--verify", "-q", "HEAD"]);
        var branch = await runner.Run("git", ["-C", root, "symbolic-ref", "-q", "--short", "HEAD"]);
        var status = await runner.Run("git", ["-C", root, "status", "--porcelain", "--untracked-files=normal"]);
        return new(head.ExitCode == 0 && head.Stdout.Trim().Length > 0 ? head.Stdout.Trim() : null,
            branch.ExitCode == 0 && branch.Stdout.Trim().Length > 0 ? branch.Stdout.Trim() : null,
            status.ExitCode != 0 || status.Stdout.Trim().Length > 0);
    }

    /// <summary>
    /// Packs the working tree. With <paramref name="previous"/> (what the guest already holds), only files whose
    /// content changed are included; the manifest still lists every file so the guest can verify and prune.
    /// Files whose size and mtime match the previous state are not rehashed.
    /// </summary>
    public static async Task<PackResult> PackIncremental(ICommandRunner runner, string root, string workspace, IEnumerable<string> excludes, string zipPath, SyncState? previous,
        SourceInfo? origin = null)
    {
        root = Path.GetFullPath(root); Paths.Id(workspace);
        var result = await runner.Run("git", ["-C", root, "ls-files", "--cached", "--others", "--exclude-standard", "-z"]);
        if (result.ExitCode != 0) throw new WinixException("git_failed", result.Stderr);
        var files = result.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
        var manifest = new SyncManifest(workspace, [], origin);
        var stats = new Dictionary<string, FileStat>();
        var held = previous?.Manifest.Files.ToDictionary(f => f.Path, f => f.Sha256) ?? [];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int added = 0, changed = 0, unchanged = 0, sent = 0; long sentBytes = 0;
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var relative in files.Order(StringComparer.Ordinal))
        {
            if (Excluded(relative, excludes) || Excluded(relative, WinixConfig.AlwaysExclude)) continue;
            var portable = Paths.Relative(relative);
            if (!names.Add(portable)) throw new WinixException("case_collision", $"Windows cannot distinguish case variants of {relative}.");
            var full = Paths.Under(root, portable);
            if (!File.Exists(full)) continue; // tracked deletion
            var info = new FileInfo(full);
            string sha;
            if (previous is not null && previous.Stats.TryGetValue(portable, out var stat) && stat.Size == info.Length && stat.ModifiedTicks == info.LastWriteTimeUtc.Ticks)
                sha = stat.Sha256;
            else
            {
                await using var source = File.OpenRead(full);
                sha = Convert.ToHexString(await SHA256.HashDataAsync(source)).ToLowerInvariant();
            }
            stats[portable] = new(info.Length, info.LastWriteTimeUtc.Ticks, sha);
            manifest.Files.Add(new(portable, sha));
            var previousSha = held.GetValueOrDefault(portable);
            if (previousSha == sha) { unchanged++; continue; }
            if (previousSha is null) added++; else changed++;
            var entry = zip.CreateEntry("files/" + portable, CompressionLevel.Fastest);
            using var hash = SHA256.Create();
            await using (var source = File.OpenRead(full))
            await using (var target = new CryptoStream(entry.Open(), hash, CryptoStreamMode.Write))
            { await source.CopyToAsync(target); await target.FlushFinalBlockAsync(); }
            var packed = Convert.ToHexString(hash.Hash!).ToLowerInvariant();
            if (packed != sha) { manifest.Files[^1] = new(portable, packed); stats[portable] = new(info.Length, info.LastWriteTimeUtc.Ticks, packed); }
            sent++; sentBytes += info.Length;
        }
        var removed = held.Keys.Count(path => !names.Contains(path));
        var metadata = zip.CreateEntry("manifest.json");
        await using (var writer = new StreamWriter(metadata.Open())) await writer.WriteAsync(Wire.Encode(manifest));
        return new(manifest, new(manifest, stats), added, changed, removed, unchanged, sent, sentBytes, previous is not null);
    }

    public static SyncManifest Apply(string zipPath, string workspacesRoot)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var metadata = zip.GetEntry("manifest.json") ?? throw new WinixException("invalid_archive", "Missing manifest.");
        using var reader = new StreamReader(metadata.Open());
        var manifest = Wire.Decode<SyncManifest>(reader.ReadToEnd());
        Paths.Id(manifest.Workspace);
        var root = Path.Combine(workspacesRoot, manifest.Workspace);
        Directory.CreateDirectory(root);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var provided = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            Paths.Relative(file.Path); var target = Paths.Under(root, file.Path);
            if (!seen.Add(file.Path)) throw new WinixException("case_collision", file.Path);
            if (zip.GetEntry("files/" + file.Path) is { } entry)
            {
                provided.Add(file.Path);
                using var content = entry.Open();
                if (Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant() != file.Sha256)
                    throw new WinixException("checksum_mismatch", $"Source archive checksum mismatch: {file.Path}");
            }
            else
            {
                // Incremental archive: the sender believes this file is already here unchanged. Verify before touching anything.
                if (!File.Exists(target)) throw new WinixException("incremental_stale", $"Missing from the guest workspace: {file.Path}. Resend a full archive.");
                using var existing = File.OpenRead(target);
                if (!Convert.ToHexString(SHA256.HashData(existing)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new WinixException("incremental_stale", $"Guest copy differs from the sender's record: {file.Path}. Resend a full archive.");
            }
        }
        if (zip.Entries.Count != provided.Count + 1) throw new WinixException("invalid_archive", "Unexpected archive entries.");
        var state = Path.Combine(workspacesRoot, manifest.Workspace + ".manifest.json");
        var old = File.Exists(state) ? Wire.Decode<SyncManifest>(File.ReadAllText(state)) : new(manifest.Workspace, []);
        // Only previously synchronized source files may be deleted. Build outputs remain intact.
        foreach (var removed in old.Files.Where(f => !seen.Contains(f.Path)))
        {
            var path = Paths.Under(root, removed.Path);
            if (File.Exists(path)) File.Delete(path);
        }
        foreach (var file in manifest.Files)
        {
            if (!provided.Contains(file.Path)) continue;
            var target = Paths.Under(root, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var entry = zip.GetEntry("files/" + file.Path)!;
            // Do not touch unchanged source mtimes: preserve incremental builds.
            if (File.Exists(target))
            {
                using var existing = File.OpenRead(target);
                if (Convert.ToHexString(SHA256.HashData(existing)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
            }
            var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                entry.ExtractToFile(temp);
                // ZIP timestamps have no timezone. New content gets guest arrival time;
                // unchanged content keeps its existing mtime for incremental builds.
                File.SetLastWriteTimeUtc(temp, DateTime.UtcNow);
                File.Move(temp, target, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        StateStore.Atomic(state, Wire.Encode(manifest));
        return manifest;
    }
}
