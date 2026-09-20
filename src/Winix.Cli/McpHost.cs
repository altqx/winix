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
            o.ServerInfo = new() { Name = "winix", Version = "0.1.0" };
            o.ServerInstructions = "Build/test Windows apps on Linux. Pass explicit vm and job IDs. Start jobs, poll status/logs, collect artifacts. Desktop mutations require an idle VM. Use the CLI skill for image provisioning.";
        }).WithStdioServerTransport().WithTools<WinixTools>(Wire.Json);
        await builder.Build().RunAsync(ct);
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
    [McpServerTool(Name = "host_doctor", ReadOnly = true, UseStructuredContent = true), Description("Check host virtualization prerequisites.")]
    public Task<List<Check>> Doctor(CancellationToken cancellationToken) => service.Doctor(cancellationToken);

    [McpServerTool(Name = "vm_list", ReadOnly = true, UseStructuredContent = true), Description("List managed Windows VMs.")]
    public List<VmRecord> List() => service.Store.List<VmRecord>("vms");

    [McpServerTool(Name = "vm_up", UseStructuredContent = true), Description("Start a Windows VM using the image and resources from a project config.")]
    public Task<VmRecord> Up(string vm, string configPath)
    { var config = WinixConfig.Load(configPath); return service.Vms.Up(vm, config.Image, config.Resources); }

    [McpServerTool(Name = "vm_status", ReadOnly = true, UseStructuredContent = true), Description("Get the libvirt state of a managed VM.")]
    public async Task<object> Status(string vm) => new { vm, status = await service.Vms.Status(vm) };

    [McpServerTool(Name = "vm_health", ReadOnly = true, UseStructuredContent = true), Description("Check Windows worker readiness, desktop lock state, active job and installed toolchain.")]
    public Task<JsonElement> Health(string vm) => service.Guest(vm).Call("health", new { });

    [McpServerTool(Name = "vm_down", UseStructuredContent = true), Description("Shut down a Windows VM. Force stops it immediately and may lose unsaved work.")]
    public async Task<object> Down(string vm, bool force = false) { await service.Vms.Down(vm, force); return new { vm, stopped = true }; }

    [McpServerTool(Name = "workspace_sync", UseStructuredContent = true), Description("Synchronize tracked and nonignored untracked working-tree files into Windows.")]
    public Task<JsonElement> Sync(string vm, string root, string configPath) => service.Sync(vm, root, configPath);

    [McpServerTool(Name = "job_start", UseStructuredContent = true), Description("Start a named build/test task. Returns a durable job ID immediately; do not retry blindly after transport loss.")]
    public Task<JobRecord> Start(string vm, string configPath, string task) => service.Run(vm, configPath, task);

    [McpServerTool(Name = "job_status", ReadOnly = true, UseStructuredContent = true), Description("Read a job's state, exit code and errors.")]
    public Task<JsonElement> JobStatus(string jobId) => service.Job(jobId, "status");

    [McpServerTool(Name = "job_logs", ReadOnly = true, UseStructuredContent = true), Description("Read up to 64 KiB of output from a byte offset. Continue using nextOffset.")]
    public Task<JsonElement> Logs(string jobId, long offset = 0) => service.Job(jobId, "logs", offset);

    [McpServerTool(Name = "job_cancel", UseStructuredContent = true), Description("Cancel a job and terminate its Windows child processes.")]
    public Task<JsonElement> Cancel(string jobId) => service.Job(jobId, "cancel");

    [McpServerTool(Name = "job_artifacts", UseStructuredContent = true), Description("Download a finished job's logs/reports/binaries to a local directory.")]
    public async Task<object> Artifacts(string jobId, string destination) => new { path = await service.Artifacts(jobId, destination) };

    [McpServerTool(Name = "desktop_action", UseStructuredContent = true), Description("Control the Windows desktop. Actions: windows, launch, close, tree, find, click, type, keys, wait, assert, deploy, uninstall. Arguments: pid, automationId/name, text, keys, exe/appId, package/dependencies. UI selectors are scoped to pid.")]
    public Task<JsonElement> Desktop(string vm, string action, JsonElement arguments)
    {
        if (action == "screenshot") throw new WinixException("use_screenshot_tool", "Use desktop_screenshot for MCP image output.");
        return service.Ui(vm, action, arguments);
    }

    [McpServerTool(Name = "desktop_screenshot", ReadOnly = true), Description("Capture the active Windows desktop as a PNG image.")]
    public async Task<CallToolResult> Screenshot(string vm)
    {
        var result = await service.Ui(vm, "screenshot", Wire.Element(new { }));
        return new() { Content = [ImageContentBlock.FromBytes(Convert.FromBase64String(result.GetProperty("data").GetString()!), "image/png")] };
    }
}
