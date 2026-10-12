using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ModelContextProtocol;
using Winix;

namespace Winix.Cli;

internal static class McpHost
{
    public static async Task Run(WinixService service, CancellationToken ct)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton(service);
        builder.Services.AddMcpServer(o =>
        {
            o.ProtocolVersion = "2026-07-28";
            o.Capabilities = new() { Tools = new() };
            o.Filters.Request.ListToolsFilters.Add(next => (request, token) =>
            { RequireVersion(request.JsonRpcMessage); return next(request, token); });
            o.Filters.Request.CallToolFilters.Add(next => (request, token) =>
            { RequireVersion(request.JsonRpcMessage); return next(request, token); });
            o.ServerInfo = new() { Name = "winix", Version = WinixVersion.Current };
            o.ServerInstructions = "Build/test Windows apps on Linux. Pass explicit vm and job IDs. Start jobs, poll status/logs, collect artifacts. Desktop mutations require an idle VM; pointer/drag go through the hypervisor and work on the lock screen. Take a lease (vm_lease; wait=true queues) when several agents share a VM, and give each branch its own workspace. Use the CLI skill for image provisioning.";
        }).WithStdioServerTransport().WithTools<WinixTools>(Wire.Json);
        WatchParent(ct);
        await builder.Build().RunAsync(ct);
    }
    /// <summary>The host exits with its client. Stdin EOF already stops the server; this covers a client killed without closing the pipe.</summary>
    private static void WatchParent(CancellationToken ct)
    {
        var parent = ParentPid();
        if (parent <= 1) return;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, CancellationToken.None);
                if (ParentPid() != parent || !Directory.Exists($"/proc/{parent}")) System.Environment.Exit(0);
            }
        }, CancellationToken.None);
    }
    private static int ParentPid()
    {
        try { var stat = File.ReadAllText("/proc/self/stat"); return int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[1]); }
        catch (Exception e) when (e is IOException or FormatException or IndexOutOfRangeException) { return -1; }
    }
    private static void RequireVersion(JsonRpcMessage message)
    {
        if (message is not JsonRpcRequest request ||
            request.Params?["_meta"]?["io.modelcontextprotocol/protocolVersion"]?.ToString() != "2026-07-28" ||
            request.Params?["_meta"]?["io.modelcontextprotocol/clientCapabilities"] is not System.Text.Json.Nodes.JsonObject)
            throw new McpProtocolException("Each request must specify MCP protocol version 2026-07-28.", McpErrorCode.UnsupportedProtocolVersion);
    }
}

internal sealed class WinixTools(WinixService service)
{
    private static string? Owner(string? owner) => owner ?? WinixService.DefaultOwner;
    /// <summary>Winix errors reach the client as <c>code: message</c>; the SDK hides other exception messages behind a generic text.</summary>
    private static async Task<T> Guard<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (WinixException e) { throw new McpException($"{e.Code}: {e.Message}"); }
    }
    private static T Guard<T>(Func<T> call)
    {
        try { return call(); }
        catch (WinixException e) { throw new McpException($"{e.Code}: {e.Message}"); }
    }

    [McpServerTool(Name = "host_doctor", ReadOnly = true), Description("Check host virtualization prerequisites and list orphaned MCP hosts.")]
    public Task<List<Check>> Doctor(CancellationToken cancellationToken) => Guard(() => service.Doctor(cancellationToken));

    [McpServerTool(Name = "vm_list", ReadOnly = true), Description("List managed Windows VMs.")]
    public List<VmRecord> List() => Guard(() => service.Store.List<VmRecord>("vms"));

    [McpServerTool(Name = "vm_up", UseStructuredContent = true), Description("Start a Windows VM using the image, resources and vm device options from a project config.")]
    public Task<VmRecord> Up(string vm, string configPath)
        => Guard(() => { var config = WinixConfig.Load(configPath); return service.Vms.Up(vm, config.Image, config.Resources, false, config.Vm); });

    [McpServerTool(Name = "vm_status", ReadOnly = true), Description("Get the libvirt state of a managed VM and its current lease, if any.")]
    public Task<object> Status(string vm) => Guard(() => service.VmStatus(vm));

    [McpServerTool(Name = "vm_health", ReadOnly = true), Description("Check Windows worker readiness, desktop lock state, active job, DPI awareness, elevation availability and installed toolchain.")]
    public Task<JsonElement> Health(string vm) => Guard(() => service.Guest(vm).Call("health", new { }));

    [McpServerTool(Name = "vm_down"), Description("Shut down a Windows VM. Force stops it immediately and may lose unsaved work. Refused while another owner holds the lease.")]
    public Task<object> Down(string vm, bool force = false, string? owner = null) => Guard<object>(async () => { await service.Down(vm, force, Owner(owner)); return new { vm, stopped = true }; });

    [McpServerTool(Name = "vm_lease"), Description("Coordinate several agents on one VM. action: acquire (owner, ttl such as 2h, idle such as 30m, note; wait=true queues first come first served for up to waitTimeout), release (owner; force), status (holder and queue). A lease ends when its holder has no job running and makes no call for the idle time, and never lasts past the maximum TTL (8h); renewing is not activity. sync, job_start, desktop_action and vm_down are refused for other owners while a lease is active.")]
    public Task<object> Lease(string vm, string action, string? owner = null, string ttl = "2h", string? idle = null, string? note = null, bool force = false,
        bool wait = false, string waitTimeout = "4h", CancellationToken cancellationToken = default) => Guard<object>(async () => action switch
    {
        "acquire" when wait => await service.AcquireWait(vm, Owner(owner) ?? throw new WinixException("usage", "owner is required."), Paths.Duration(ttl), note,
            idle is null ? null : Paths.Duration(idle), Paths.Duration(waitTimeout), cancellationToken),
        "acquire" => service.Acquire(vm, Owner(owner) ?? throw new WinixException("usage", "owner is required."), Paths.Duration(ttl), note, idle is null ? null : Paths.Duration(idle)),
        "release" => service.Release(vm, Owner(owner), force),
        "status" => service.LeaseStatus(vm),
        _ => throw new WinixException("usage", "action must be acquire, release or status.")
    });

    [McpServerTool(Name = "vm_snapshot"), Description("A known baseline for evidence runs. action: create (name, note), list, delete (name), revert (name; restart=true shuts a running VM down, reverts, starts it from configPath and waits for the desktop). Create and delete need the VM shut off.")]
    public Task<object> Snapshot(string vm, string action, string? name = null, string? note = null, bool restart = false, string? configPath = null, string? owner = null,
        CancellationToken cancellationToken = default) => Guard<object>(async () =>
    {
        string Name() => name ?? throw new WinixException("usage", "name is required.");
        return action switch
        {
            "create" => await service.Snapshot(vm, Name(), note, Owner(owner)),
            "list" => service.Vms.Snapshots(vm),
            "delete" => await service.SnapshotDelete(vm, Name(), Owner(owner)),
            "revert" => await service.Revert(vm, Name(), Owner(owner), restart, configPath ?? (restart ? throw new WinixException("usage", "configPath is required with restart.") : "winix.yaml"), cancellationToken),
            _ => throw new WinixException("usage", "action must be create, list, delete or revert.")
        };
    });

    [McpServerTool(Name = "vm_device"), Description("Hot-plug virtual hardware through QEMU. action: add (driver usb-audio|usb-kbd|usb-mouse|usb-tablet|usb-hub|usb-host, optional id and properties), remove (id), list. Optionally wait for markerJobId's log to contain marker first.")]
    public Task<object> Device(string vm, string action, string? driver = null, string? id = null, Dictionary<string, string>? properties = null, string? markerJobId = null, string? marker = null, CancellationToken cancellationToken = default) => Guard<object>(async () =>
    {
        if (markerJobId is not null && !await service.WaitForLog(markerJobId, marker ?? throw new WinixException("usage", "marker is required with markerJobId."), TimeSpan.FromMinutes(10), cancellationToken))
            throw new WinixException("marker_missing", "The job finished without the marker; device not changed.");
        return action switch
        {
            "add" => await service.Vms.DeviceAdd(vm, driver ?? throw new WinixException("usage", "driver is required."), id, properties ?? [], cancellationToken),
            "remove" => await service.Vms.DeviceRemove(vm, id ?? throw new WinixException("usage", "id is required."), cancellationToken),
            "list" => await service.Vms.DeviceList(vm, cancellationToken),
            _ => throw new WinixException("usage", "action must be add, remove or list.")
        };
    });

    [McpServerTool(Name = "vm_devices", ReadOnly = true), Description("Guest-side device inventory: monitors with DPI, display adapters, audio devices and PnP status.")]
    public Task<JsonElement> Devices(string vm, CancellationToken cancellationToken) => Guard(() => service.Devices(vm, cancellationToken));

    [McpServerTool(Name = "workspace_sync"), Description("Synchronize tracked and nonignored untracked working-tree files into Windows. Incremental by default; full resends everything. workspace picks a separate guest work tree (one per branch keeps build state apart; compiler caches are shared). Returns a summary with the manifest and the source commit, branch and dirty flag.")]
    public Task<SyncResult> Sync(string vm, string root, string configPath, string? owner = null, bool full = false, string? workspace = null, CancellationToken cancellationToken = default)
        => Guard(() => service.Sync(vm, root, configPath, Owner(owner), full, workspace, cancellationToken));

    [McpServerTool(Name = "workspace_push"), Description("Copy a host file or directory to an absolute guest path (C:\\... or %TEMP%\\...) outside the synced tree. Returns the SHA-256.")]
    public Task<TransferResult> Push(string vm, string hostPath, string guestPath, string? owner = null, CancellationToken cancellationToken = default) => Guard(() => service.Push(vm, hostPath, guestPath, Owner(owner), cancellationToken));

    [McpServerTool(Name = "workspace_pull"), Description("Copy a guest file or directory to the host; the transfer hash is verified.")]
    public Task<TransferResult> Pull(string vm, string guestPath, string hostPath, string? owner = null, CancellationToken cancellationToken = default) => Guard(() => service.Pull(vm, guestPath, hostPath, Owner(owner), cancellationToken));

    [McpServerTool(Name = "job_start", UseStructuredContent = true), Description("Start a named build/test task. Returns a durable job ID immediately; do not retry blindly after transport loss. env/args add per-run values, timeoutSeconds overrides the task's timeout, workspace selects the guest work tree it builds in, label names the caller, queueSeconds waits for a busy guest instead of failing (a job stuck past its timeout is stopped meanwhile).")]
    public Task<JobRecord> Start(string vm, string configPath, string task, Dictionary<string, string>? env = null, string[]? args = null, int? timeoutSeconds = null, string? workspace = null,
        string? label = null, int queueSeconds = 0, string? owner = null, CancellationToken cancellationToken = default)
        => Guard(() => service.Run(vm, configPath, task, env is null && args is null && timeoutSeconds is null ? null : new(env ?? [], [.. args ?? []], timeoutSeconds), label, Owner(owner),
            queueSeconds > 0 ? TimeSpan.FromSeconds(queueSeconds) : null, workspace, cancellationToken));

    [McpServerTool(Name = "job_status", ReadOnly = true), Description("Read a job's state, exit code, label, parameters, errors and teardown result, with its VM, owner, source commit, and elapsedSeconds against timeoutSeconds (overdue when it runs past its timeout).")]
    public Task<Dictionary<string, object?>> JobStatus(string jobId) => Guard(async () => service.Describe(await service.Job(jobId, "status")));

    [McpServerTool(Name = "job_wait"), Description("Block until a job ends (connection loss is retried for up to 10 minutes; a job running past its timeout is stopped). Returns the final record.")]
    public Task<Dictionary<string, object?>> Wait(string jobId, CancellationToken cancellationToken) => Guard(async () => service.Describe(await service.Wait(jobId, cancellationToken)));

    [McpServerTool(Name = "job_logs", ReadOnly = true), Description("Read up to 64 KiB of output from a byte offset. Continue using nextOffset; all=true concatenates every page currently available.")]
    public Task<JsonElement> Logs(string jobId, long offset = 0, bool all = false, CancellationToken cancellationToken = default) => Guard(async () =>
    {
        if (!all) return await service.Job(jobId, "logs", offset, cancellationToken);
        var text = new System.Text.StringBuilder(); LogPage page;
        do { page = Wire.Decode<LogPage>((await service.Job(jobId, "logs", offset, cancellationToken)).GetRawText()); text.Append(page.Text); offset = page.NextOffset; }
        while (page.Text.Length > 0 && !page.End);
        return Wire.Element(new LogPage(text.ToString(), offset, page.End));
    });

    [McpServerTool(Name = "job_cancel"), Description("Cancel a job: terminate its Windows process tree and run its teardown; if it does not stop, kill everything it started, then restart the guest worker. force skips the cooperative step. Returns the escalation steps taken and the final record.")]
    public Task<object> Cancel(string jobId, bool force = false, string? owner = null, CancellationToken cancellationToken = default) => Guard(() => service.Cancel(jobId, force, Owner(owner), cancellationToken));

    [McpServerTool(Name = "job_artifacts"), Description("Download a finished job's logs/reports/binaries ZIP to a local directory, or one ad-hoc guestPath (absolute, %TEMP%-rooted or workspace-relative).")]
    public Task<object> Artifacts(string jobId, string destination, string? guestPath = null, string? owner = null, CancellationToken cancellationToken = default)
        => Guard<object>(async () => guestPath is null ? new { path = await service.Artifacts(jobId, destination, cancellationToken) } : await service.JobFile(jobId, guestPath, destination, Owner(owner), cancellationToken));

    [McpServerTool(Name = "job_dump"), Description("Write a minidump and cdb thread stacks of a guest process (pid); fetch the paths with workspace_pull.")]
    public Task<JsonElement> Dump(string vm, int pid, bool full = false, string? owner = null, CancellationToken cancellationToken = default) => Guard(() => service.Ui(vm, "dump", Wire.Element(new { pid, full }), Owner(owner), cancellationToken));

    [McpServerTool(Name = "desktop_action"), Description("Control the Windows desktop. Actions: windows (pid lists every top-level and owned window), launch (exe/appId, arguments, env, cwd, noConsole, freshProfile), close, focus, state, tree, find, click, rightclick, doubleclick, type, text, keys (scan codes; 'CTRL+S DOWN*3 ENTER'), wait, assert, monitors, kill, batch (steps[]), pointer/drag/calibrate (host-side QMP tablet: path 'x1,y1;x2,y2', from/to, button, hold, map), deploy, uninstall. Selectors are scoped to pid; window picks a title, index or hwnd:N.")]
    public Task<JsonElement> Desktop(string vm, string action, JsonElement arguments, string? owner = null, CancellationToken cancellationToken = default)
    {
        if (action == "screenshot") throw new McpException("use_screenshot_tool: Use desktop_screenshot for MCP image output.");
        return Guard(() => service.Ui(vm, action, arguments, Owner(owner), cancellationToken));
    }

    [McpServerTool(Name = "desktop_screenshot", ReadOnly = true), Description("Capture the Windows desktop (or one monitor) as a PNG image in physical pixels.")]
    public async Task<CallToolResult> Screenshot(string vm, int? monitor = null, CancellationToken cancellationToken = default)
    {
        var result = await Guard(() => service.Ui(vm, "screenshot", Wire.Element(monitor is null ? new { } : (object)new { monitor }), null, cancellationToken));
        return new() { Content = [ImageContentBlock.FromBytes(Convert.FromBase64String(result.GetProperty("data").GetString()!), "image/png")] };
    }

    [McpServerTool(Name = "guest_restart"), Description("Restart a stuck guest worker through the QEMU guest agent. Unfinished jobs are ended (cancelled, timed out or interrupted) and their teardown runs.")]
    public Task<object> Restart(string vm, string? owner = null, CancellationToken cancellationToken = default) => Guard(() => service.GuestRestart(vm, Owner(owner), cancellationToken));

    [McpServerTool(Name = "guest_driver_add"), Description("Install a driver (.inf path inside the guest) through the provisioned elevated SYSTEM task. Push the driver directory first with workspace_push.")]
    public Task<JsonElement> DriverAdd(string vm, string inf, string? owner = null, CancellationToken cancellationToken = default) => Guard(() => service.Elevated(vm, "driver-add", new { inf }, Owner(owner), cancellationToken));
}
