using System.Text.Json;

namespace Winix;

public sealed class GuestConnection(VmRecord vm, ICommandRunner runner)
{
    /// <summary>One SSH connection is kept open per VM (OpenSSH ControlMaster) so each short command skips the handshake. Set WINIX_SSH_MUX=0 to disable.</summary>
    public static bool Multiplex => System.Environment.GetEnvironmentVariable("WINIX_SSH_MUX") != "0";
    public static string ControlPath()
    {
        var dir = System.Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = Path.GetTempPath();
        // %C hashes local host, remote host, port and user, so the socket name stays short and unique per VM.
        return Path.Combine(dir, "winix-ssh-%C");
    }
    private List<string> Options(bool scp = false)
    {
        var keyFile = Path.Combine(vm.Directory, "hostkey.pub");
        if (!File.Exists(keyFile)) throw new WinixException("hostkey_missing", "Run winix vm trust after the QEMU guest agent is installed.");
        var known = Path.Combine(vm.Directory, "known_hosts");
        StateStore.Atomic(known, $"[127.0.0.1]:{vm.SshPort} {File.ReadAllText(keyFile).Trim()}\n");
        List<string> options = [scp ? "-P" : "-p", vm.SshPort.ToString(), "-i", Path.Combine(vm.Directory, "id_ed25519"),
            "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes", "-o", "ConnectTimeout=10", "-o", "StrictHostKeyChecking=yes", "-o", "UserKnownHostsFile=" + known,
            "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=4"];
        if (Multiplex) options.AddRange(["-o", "ControlMaster=auto", "-o", "ControlPath=" + ControlPath(), "-o", "ControlPersist=600"]);
        return options;
    }
    public async Task<JsonElement> Call(string method, object arguments, CancellationToken ct = default)
    {
        var result = await runner.Run("ssh", Options().Concat(["winix@127.0.0.1", "C:/Winix/agent/winix-guest.exe rpc"]),
            Wire.Encode(new GuestRequest(method, Wire.Element(arguments))) + "\n", cancellationToken: ct);
        if (result.ExitCode != 0) throw new WinixException("transport_error", $"Windows SSH failed ({result.ExitCode}): {result.Stderr.Trim()}");
        var response = Wire.Decode<GuestResponse>(result.Stdout);
        if (!response.Ok) throw new WinixException(response.Code ?? "guest_error", response.Error ?? "Windows operation failed.");
        return response.Result;
    }
    public async Task Upload(string localFile, string name, CancellationToken ct = default)
    {
        Paths.Relative(name);
        await runner.Checked("scp", Options(true).Concat([Path.GetFullPath(localFile), $"winix@127.0.0.1:C:/Winix/inbox/{name}"]), ct: ct);
    }
    public async Task Download(string guestFile, string localFile, CancellationToken ct = default)
    {
        // Only Winix-owned exported files, never caller-controlled scp globs or paths.
        Paths.Relative(guestFile);
        await runner.Checked("scp", Options(true).Concat([$"winix@127.0.0.1:C:/Winix/exports/{guestFile}", Path.GetFullPath(localFile)]), ct: ct);
    }
    /// <summary>Closes the multiplexed master connection, e.g. before the VM shuts down. Failures are ignored.</summary>
    public async Task Disconnect(CancellationToken ct = default)
    {
        if (!Multiplex || !File.Exists(Path.Combine(vm.Directory, "hostkey.pub"))) return;
        try { await runner.Run("ssh", ["-O", "exit", "-o", "ControlPath=" + ControlPath(), "-p", vm.SshPort.ToString(), "winix@127.0.0.1"], cancellationToken: ct); }
        catch (WinixException) { }
    }
}
