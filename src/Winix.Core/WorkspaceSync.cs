using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace Winix;

public static class WorkspaceSync
{
    public static bool Excluded(string relative, IEnumerable<string> patterns)
    {
        return patterns.Any(pattern => relative.Split('/').Any(part => Regex.IsMatch(part,
            "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase)) ||
            relative.Equals(pattern, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(pattern.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
    }
    public static async Task<SyncManifest> Pack(ICommandRunner runner, string root, string workspace, IEnumerable<string> excludes, string zipPath)
    {
        root = Path.GetFullPath(root); Paths.Id(workspace);
        var result = await runner.Run("git", ["-C", root, "ls-files", "--cached", "--others", "--exclude-standard", "-z"]);
        if (result.ExitCode != 0) throw new WinixException("git_failed", result.Stderr);
        var files = result.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
        var manifest = new SyncManifest(workspace, []);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var relative in files.Order(StringComparer.Ordinal))
        {
            if (Excluded(relative, excludes) || Excluded(relative, [".git", ".winix", "bin", "obj"])) continue;
            var portable = Paths.Relative(relative);
            if (!names.Add(portable)) throw new WinixException("case_collision", $"Windows cannot distinguish case variants of {relative}.");
            var full = Paths.Under(root, portable);
            if (!File.Exists(full)) continue; // tracked deletion
            var entry = zip.CreateEntry("files/" + portable, CompressionLevel.Fastest);
            using var hash = SHA256.Create();
            await using (var source = File.OpenRead(full))
            await using (var target = new CryptoStream(entry.Open(), hash, CryptoStreamMode.Write))
            { await source.CopyToAsync(target); await target.FlushFinalBlockAsync(); }
            manifest.Files.Add(new(portable, Convert.ToHexString(hash.Hash!).ToLowerInvariant()));
        }
        var metadata = zip.CreateEntry("manifest.json");
        await using (var writer = new StreamWriter(metadata.Open())) await writer.WriteAsync(Wire.Encode(manifest));
        return manifest;
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
        foreach (var file in manifest.Files)
        {
            Paths.Relative(file.Path); Paths.Under(root, file.Path);
            if (!seen.Add(file.Path)) throw new WinixException("case_collision", file.Path);
            if (zip.GetEntry("files/" + file.Path) is null) throw new WinixException("invalid_archive", $"Missing file {file.Path}");
            using var content = zip.GetEntry("files/" + file.Path)!.Open();
            if (Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant() != file.Sha256)
                throw new WinixException("checksum_mismatch", $"Source archive checksum mismatch: {file.Path}");
        }
        if (zip.Entries.Count != manifest.Files.Count + 1) throw new WinixException("invalid_archive", "Unexpected archive entries.");
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
