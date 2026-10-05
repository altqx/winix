using System.Text.Json;
using Winix;
using Winix.Cli;

return await Entry.Run(args);

internal static class Entry
{
    public static async Task<int> Run(string[] raw)
    {
        var json = raw.Contains("--json");
        try
        {
            var args = new Arguments(raw);
            if (args.Words.Count == 0 || args.Flag("help")) { Console.WriteLine(Help); return 0; }
            var store = new StateStore(args.Option("home")); var runner = new CommandRunner(); var service = new WinixService(store, runner);
            var vm = args.Option("vm") ?? "dev";
            var owner = args.Option("owner") ?? WinixService.DefaultOwner;
            var configFile = Path.GetFullPath(args.Option("config") ?? "winix.yaml");
            var root = Path.GetDirectoryName(configFile)!;
            using var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
            var ct = cancel.Token;
            object result;
            switch (args.Word(0))
            {
                case "init":
                    if (File.Exists(configFile)) throw new WinixException("config_exists", "Configuration already exists.");
                    File.WriteAllText(configFile, DefaultConfig); result = new { config = configFile }; break;
                case "doctor":
                    var checks = await service.Doctor(ct); Print(checks, json); return checks.All(c => c.Ok) ? 0 : 3;
                case "config": result = WinixConfig.Load(configFile); break;
                case "media":
                    var path = Path.GetFullPath(args.Option("output") ?? Path.Combine(store.Root, "media", "windows11.iso"));
                    result = new { path = await Media.Download(runner, Media.WindowsUrl, path, Media.WindowsSha256, ct) }; break;
                case "image":
                    var builder = new ImageBuilder(store, runner);
                    result = args.Word(1) switch
                    {
                        "build" => await builder.Build(args.Option("name") ?? "windows-dev", args.Option("iso"),
                            Path.GetFullPath(args.Option("guest") ?? (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "guest")) ? Path.Combine(AppContext.BaseDirectory, "guest") : "artifacts/guest")),
                            args.Option("edition") ?? "Community", nvda: args.Option("nvda-url") is { } nvdaUrl ? new(nvdaUrl, args.Option("nvda-sha256") ?? throw new WinixException("usage", "--nvda-url needs --nvda-sha256.")) : null),
                        "seal" => await builder.Seal(args.Option("vm") ?? "build-windows-dev", args.Option("name") ?? "windows-dev"),
                        "list" => store.List<ImageManifest>("images"),
                        "status" => await service.Vms.ProvisionStatus(args.Option("vm") ?? "build-windows-dev"),
                        _ => throw new WinixException("usage", "image build|seal|list|status")
                    }; break;
                case "vm":
                    var name = args.Words.Count > 2 && args.Word(1) != "device" ? args.Word(2) : vm;
                    switch (args.Word(1))
                    {
                        case "up":
                            var config = WinixConfig.Load(configFile);
                            result = await service.Vms.Up(name, config.Image, config.Resources, false, config.Vm);
                            if (args.Flag("wait")) await service.Ready(name, ct: ct);
                            break;
                        case "down": await service.Down(name, args.Flag("force"), owner); result = new { vm = name, status = "shut off" }; break;
                        case "delete": await service.Vms.Delete(name); result = new { deleted = name }; break;
                        case "status": result = await service.VmStatus(name); break;
                        case "list": result = store.List<VmRecord>("vms"); break;
                        case "ready": result = await service.Ready(name, ct: ct); break;
                        case "health": result = await service.Guest(name).Call("health", new { }, ct); break;
                        case "trust": await service.Vms.PinHostKey(store.Read<VmRecord>("vms", name)); result = new { vm = name, trusted = true }; break;
                        case "devices": result = await service.Devices(name, ct); break;
                        case "device":
                            // Optional trigger: wait for a marker in a job's log (and/or a delay) before changing hardware.
                            if (args.Option("job") is { } triggerJob)
                            {
                                var marker = args.Option("marker") ?? throw new WinixException("usage", "--job needs --marker TEXT.");
                                if (!await service.WaitForLog(triggerJob, marker, Paths.Duration(args.Option("timeout") ?? "10m"), ct))
                                    throw new WinixException("marker_missing", $"Job {triggerJob} finished without printing '{marker}'; device not changed.");
                            }
                            if (args.Option("delay") is { } delay) await Task.Delay(Paths.Duration(delay), ct);
                            result = args.Word(2) switch
                            {
                                "add" => await service.Vms.DeviceAdd(vm, args.Word(3), args.Option("id"), Pairs(args.Option("props")), ct),
                                "remove" => await service.Vms.DeviceRemove(vm, args.Word(3), ct),
                                "list" => await service.Vms.DeviceList(vm, ct),
                                _ => throw new WinixException("usage", "vm device add DRIVER [--id ID] [--props k=v,..] | remove ID | list  (--job ID --marker TEXT, --delay 5s)")
                            };
                            break;
                        default: throw new WinixException("usage", "vm up|down|delete|status|list|ready|health|trust|devices [name] | vm device add|remove|list");
                    }
                    break;
                case "console":
                    await runner.Checked("virt-viewer", ["--connect", VmManager.Connection, "winix-" + Paths.Id(vm)], ct: ct);
                    result = new { vm }; break;
                case "lease":
                    result = args.Word(1) switch
                    {
                        "acquire" => service.Acquire(vm, owner ?? throw new WinixException("usage", "lease acquire needs --owner NAME (or WINIX_OWNER)."), Paths.Duration(args.Option("ttl") ?? "2h"), args.Option("note")),
                        "release" => service.Release(vm, owner, args.Flag("force")),
                        "status" => new { vm, lease = service.Lease(vm) },
                        _ => throw new WinixException("usage", "lease acquire --owner NAME [--ttl 2h] [--note TEXT] | release [--force] | status")
                    };
                    break;
                case "sync":
                {
                    var sync = await service.Sync(vm, args.Words.Count > 1 ? Path.GetFullPath(args.Word(1)) : root, configFile, owner, args.Flag("full"), ct);
                    if (json) Print(Wire.Typed("sync", sync), true); else Console.WriteLine(sync.Summary);
                    return 0;
                }
                case "push": result = await service.Push(vm, args.Word(1), args.Word(2), owner, ct); break;
                case "pull": result = await service.Pull(vm, args.Word(1), args.Word(2), owner, ct); break;
                case "run":
                {
                    var queue = args.Flag("queue") ? Paths.Duration(args.Option("timeout") ?? "1h") : (TimeSpan?)null;
                    var started = await service.Run(vm, configFile, args.Word(1), Parameters(args), args.Option("label"), owner, queue, ct);
                    if (!args.Flag("wait")) { PrintJob(started, service, json); return 0; }
                    // The ID goes out first so a caller that loses this process can still find its job.
                    if (json) Print(Wire.Typed("started", new { id = started.Id, vm, label = started.Label }), true);
                    else Console.Error.WriteLine($"Started {started.Id}; waiting. Interrupting this client leaves the Windows job running (resume with winix job wait {started.Id}).");
                    var record = await service.Wait(started.Id, ct);
                    await Fetch(service, record, args.Option("artifacts"), json, ct);
                    PrintJob(record, service, json); return record.State == JobState.Succeeded ? 0 : 1;
                }
                case "job":
                    switch (args.Word(1))
                    {
                        case "wait":
                        {
                            var record = await service.Wait(args.Word(2), ct);
                            await Fetch(service, record, args.Option("artifacts"), json, ct);
                            PrintJob(record, service, json); return record.State == JobState.Succeeded ? 0 : 1;
                        }
                        case "status": PrintJob(await service.Job(args.Word(2), "status", ct: ct), service, json); return 0;
                        case "cancel": result = await service.Job(args.Word(2), "cancel", ct: ct); break;
                        case "logs": return await Logs(service, args.Word(2), long.Parse(args.Option("offset") ?? "0"), args.Flag("follow"), args.Flag("all"), json, ct);
                        case "artifacts":
                            result = args.Option("path") is { } guestPath
                                ? await service.JobFile(args.Word(2), guestPath, args.Option("output") ?? "artifacts/runs", owner, ct)
                                : new { path = await service.Artifacts(args.Word(2), args.Option("output") ?? "artifacts/runs", ct) };
                            break;
                        case "dump":
                            result = await service.Ui(vm, "dump", Wire.Element(new { pid = int.Parse(args.Option("pid") ?? throw new WinixException("usage", "job dump needs --pid N.")), full = args.Flag("full") }), owner, ct);
                            break;
                        default: throw new WinixException("usage", "job status|logs|wait|cancel|artifacts ID | job dump --pid N");
                    }
                    break;
                case "ui":
                {
                    var action = args.Word(1);
                    var response = await service.Ui(vm, action, UiArguments(args, action), owner, ct);
                    if (action == "screenshot")
                    {
                        var output = Path.GetFullPath(args.Option("output") ?? "winix-screenshot.png");
                        await File.WriteAllBytesAsync(output, Convert.FromBase64String(response.GetProperty("data").GetString()!), ct);
                        result = new { path = output, width = response.GetProperty("width").GetInt32(), height = response.GetProperty("height").GetInt32(),
                            monitor = response.TryGetProperty("monitor", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : (int?)null };
                    }
                    else result = response;
                    break;
                }
                case "guest":
                    if (args.Word(1) == "driver" && args.Word(2) == "add") result = await service.Elevated(vm, "driver-add", new { inf = args.Word(3) }, owner, ct);
                    else if (args.Word(1) == "update")
                        result = await service.UpdateGuest(vm, Path.GetFullPath(args.Option("guest") ?? new[] { "artifacts/release/guest", "artifacts/guest" }.FirstOrDefault(d => File.Exists(Path.Combine(d, "winix-guest.exe"))) ?? "artifacts/guest"), owner, ct);
                    else throw new WinixException("usage", "guest driver add GUEST_INF_PATH | guest update [--guest DIR]");
                    break;
                case "ci":
                    result = await service.Ci(configFile, root, args.Option("output") ?? "artifacts/runs", ct);
                    Print(result, json); return Wire.Element(result).GetProperty("succeeded").GetBoolean() ? 0 : 1;
                case "mcp": await McpHost.Run(service, ct); return 0;
                default: throw new WinixException("usage", "Unknown command. Run winix --help.");
            }
            Print(result, json); return 0;
        }
        catch (OperationCanceledException) { Print(new { error = "cancelled", message = "Client wait cancelled. Submitted jobs remain queryable by ID." }, json); return 130; }
        catch (Exception e)
        {
            var error = new { error = (e as WinixException)?.Code ?? "error", message = e.Message };
            if (json) Console.WriteLine(Wire.Encode(error)); else Console.Error.WriteLine($"{error.error}: {error.message}");
            return e is WinixException { Code: "usage" or "invalid_id" or "invalid_task" or "config_missing" } ? 2
                : e is WinixException { Code: "contact_lost" } ? 4 : 3;
        }
    }
    private static void Print(object value, bool json) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions(Wire.Json) { WriteIndented = !json }));
    /// <summary>A job record with its VM and local-time stamps (guest clocks are UTC).</summary>
    private static void PrintJob(object record, WinixService service, bool json)
    {
        var view = Wire.Typed("job", record);
        var id = view.TryGetValue("id", out var idValue) && idValue is JsonElement e ? e.GetString() : null;
        if (id is not null && service.Store.Exists("jobs", id)) view["vm"] = service.HostJob(id).Vm;
        foreach (var key in new[] { "createdAt", "finishedAt" })
            if (view.TryGetValue(key, out var value) && value is JsonElement { ValueKind: JsonValueKind.String } stamp && DateTimeOffset.TryParse(stamp.GetString(), out var when))
                view[key + "Local"] = when.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
        Print(view, json);
    }
    private static async Task Fetch(WinixService service, JobRecord record, string? destination, bool json, CancellationToken ct)
    {
        if (destination is null) return;
        var path = await service.Artifacts(record.Id, destination, ct);
        if (json) Print(Wire.Typed("artifacts", new { id = record.Id, path }), true); else Console.Error.WriteLine($"Artifacts: {path}");
    }
    private static async Task<int> Logs(WinixService service, string id, long offset, bool follow, bool all, bool json, CancellationToken ct)
    {
        if (!follow && !all) { Print(await service.Job(id, "logs", offset, ct), json); return 0; }
        var wroteText = false;
        while (true)
        {
            var page = Wire.Decode<LogPage>((await service.Job(id, "logs", offset, ct)).GetRawText());
            if (page.Text.Length > 0)
            {
                if (json) Print(Wire.Typed("logs", page), true); else { Console.Write(page.Text); wroteText = true; }
                offset = page.NextOffset; continue;
            }
            if (page.End || !follow) break; // --all stops once caught up; --follow waits for the job to end
            await Task.Delay(1000, ct);
        }
        if (wroteText) Console.Out.Flush();
        return 0;
    }
    private static RunParameters? Parameters(Arguments args)
    {
        var env = new Dictionary<string, string>();
        foreach (var (key, value) in Pairs(args.Multi("env"))) env[TaskSpec.ValidateEnvName(key)] = value;
        var list = args.Multi("arg");
        return env.Count == 0 && list.Count == 0 ? null : new(env, list);
    }
    private static Dictionary<string, string> Pairs(string? csv) => Pairs(csv is null ? [] : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
    private static Dictionary<string, string> Pairs(List<string> items)
    {
        var result = new Dictionary<string, string>();
        foreach (var item in items)
        {
            var split = item.IndexOf('=');
            if (split <= 0) throw new WinixException("usage", $"Expected KEY=VALUE but got '{item}'.");
            result[item[..split]] = item[(split + 1)..];
        }
        return result;
    }
    /// <summary>UI arguments from flags; <c>--args JSON</c> supplies a base object (or the step list for batch).</summary>
    private static JsonElement UiArguments(Arguments args, string action)
    {
        var values = new Dictionary<string, object?>();
        if (args.Option("args") is { } supplied)
        {
            var element = Wire.Decode<JsonElement>(supplied);
            if (element.ValueKind == JsonValueKind.Array && action == "batch") values["steps"] = element;
            else if (element.ValueKind == JsonValueKind.Object) foreach (var p in element.EnumerateObject()) values[p.Name] = p.Value;
            else throw new WinixException("usage", "--args must be a JSON object (or an array of steps for batch).");
        }
        if (args.Option("file") is { } file) values["steps"] = Wire.Decode<JsonElement>(File.ReadAllText(file));
        void Set(string key, string option) { if (args.Option(option) is { } v) values[key] = v; }
        void SetInt(string key, string option) { if (args.Option(option) is { } v) values[key] = int.Parse(v); }
        void SetFlag(string key, string option) { if (args.Flag(option)) values[key] = true; }
        Set("exe", "exe"); Set("appId", "app-id"); Set("automationId", "automation-id"); Set("name", "name"); Set("text", "text"); Set("keys", "keys");
        Set("window", "window"); Set("cwd", "cwd"); Set("button", "button"); Set("path", "path"); Set("point", "point"); Set("map", "map"); Set("backend", "backend");
        SetInt("pid", "pid"); SetInt("monitor", "monitor"); SetInt("steps", "steps"); SetInt("delayMs", "delay"); SetInt("depth", "depth"); SetInt("limit", "limit");
        if (args.Option("timeout") is { } timeout) values["timeoutMs"] = (int)Paths.Duration(timeout).TotalMilliseconds;
        SetFlag("hold", "hold"); SetFlag("release", "release"); SetFlag("noConsole", "no-console"); SetFlag("freshProfile", "fresh-profile");
        SetFlag("includeHidden", "include-hidden"); SetFlag("continueOnError", "continue-on-error"); SetFlag("full", "full"); SetFlag("single", "single");
        if (args.Multi("arg").Count > 0) values["arguments"] = args.Multi("arg");
        if (args.Multi("env").Count > 0) values["env"] = Pairs(args.Multi("env"));
        if (action == "drag" && args.Words.Count >= 4) { values["from"] = args.Word(2); values["to"] = args.Word(3); }
        if (action == "pointer" && args.Words.Count >= 3 && !values.ContainsKey("path")) values["path"] = string.Join(';', args.Words.Skip(2));
        return Wire.Element(values);
    }
    private const string Help = """
Winix — Windows build and desktop testing from Linux

  init                            Create winix.yaml
  doctor                          Check host prerequisites, stray MCP hosts (exit 3 if missing)
  config                          Validate and print project configuration
  media download                  Download/checksum Windows 11 from Microsoft
  image build [--iso PATH]         Provision Windows + full Visual Studio Community
             [--guest DIR] [--edition Community|Professional|Enterprise]
             [--nvda-url URL --nvda-sha256 HASH]   Optional NVDA portable recipe
  image seal --vm NAME --name ID   Shut down and capture a verified baseline
  image list                      List immutable images
  image status --vm NAME          Read provisioning stage through QEMU guest agent
  vm up|down|delete|status NAME    Manage a development VM (--force for hard stop; status shows the lease)
  vm list|ready|health|trust [NAME] Inventory, desktop readiness, worker health, QGA host-key pinning
  vm devices                      Guest view: adapters, monitors, audio, PnP status
  vm device add DRIVER|remove ID  Hot-plug (usb-audio, usb-kbd, usb-mouse, usb-tablet, usb-hub, usb-host)
     [--id ID] [--props k=v,..] [--job ID --marker TEXT] [--delay 5s]   Trigger on a job-log marker
  vm device list                  QEMU peripherals and Winix-tracked hot-plugs
  lease acquire --owner NAME      Hold the VM (--ttl 2h, --note TEXT); honoured by sync, run, ui, vm down
  lease release [--force] | status
  console --vm NAME               Open the virtual display
  sync [DIRECTORY] [--full]       Copy working-tree changes into Windows (incremental; one-line summary)
  push HOST_PATH GUEST_PATH       Copy a file or directory into the guest, outside the synced tree
  pull GUEST_PATH HOST_PATH       Copy a guest file or directory back (hashes verified)
  run TASK [--wait]               Start a durable build/test task
     [--env K=V]... [--arg V]... [--label NAME] [--queue [--timeout 1h]] [--artifacts DIR]
  job status|logs|wait|cancel ID   Inspect/cancel work (logs: --offset BYTES, --follow, --all)
  job artifacts ID [--output DIR] Retrieve logs, reports and binaries as ZIP (--path GUEST_PATH for one file)
  job wait ID [--artifacts DIR]   Wait, then download the ZIP
  job dump --pid N [--full]       Minidump + thread stacks (cdb) of a guest process
  ui ACTION [--args JSON]         windows, launch, close, focus, state, tree, find, click, rightclick,
                                 doubleclick, type, text, keys, wait, assert, screenshot, monitors,
                                 kill, dump, batch, pointer, drag, calibrate, deploy, uninstall
  guest driver add GUEST_INF      Install a driver through the provisioned elevated task
  guest update [--guest DIR]      Replace the worker on a running VM with a published guest (via QEMU guest agent)
  ci                              Clean overlay → sync → pipeline → export → cleanup
  mcp                             Strict MCP 2026-07-28 stdio server

Global: --json --home DIR --vm NAME --config PATH --owner NAME (or WINIX_OWNER)
UI: --exe PATH --app-id ID --pid N --window TITLE|INDEX|hwnd:N --automation-id ID --name NAME
    --text TEXT --keys 'CTRL+S DOWN*3 ENTER' --timeout 10s --monitor N --output PATH
    launch: --arg V... --env K=V... --cwd DIR --no-console --fresh-profile
    pointer: --path 'x1,y1;x2,y2' --button left --hold --release --steps 20 --delay 15 --map virtual|primary|monitor:N
    drag FROM TO (x,y pairs); calibrate --point x,y; batch --file steps.json [--continue-on-error]
Image default: windows-dev. VM default: dev. Config default: ./winix.yaml.
Exit: 0 success, 1 task/test failure, 2 invalid usage, 3 infrastructure, 4 contact lost (job still running), 130 interrupted.
""";
    private const string DefaultConfig = """
version: 1
image: windows-dev
workspace: app
resources:
  cpus: 6
  memoryGiB: 12
  diskGiB: 180
# vm:              # virtual hardware; applied the next time the VM starts from shut off
#   audio: none    # off | none (silent HDA endpoint for WASAPI tests) | spice
#   displays: 1    # monitors; more than one uses QXL
#   usb: []        # usb-audio, usb-kbd, usb-mouse, usb-tablet, usb-hub or VVVV:PPPP host devices
exclude: [.git, .winix, .env, '*.pfx', '*.key']
pipeline: [build, test]
tasks:
  build:
    script: |
      cmake -S . -B build -A x64
      if ($LASTEXITCODE) { exit $LASTEXITCODE }
      cmake --build build --config Debug
    timeoutSeconds: 1800
    artifacts: [build]
  test:
    executable: ctest
    arguments: [--test-dir, build, -C, Debug, --output-on-failure, --output-junit, results.xml]
    timeoutSeconds: 600
    artifacts: [build/results.xml]
# Other task keys: scriptFile (PowerShell file in the workspace), env/environment, workingDirectory,
# detach (started apps outlive the task), elevated (runs through the SYSTEM task; no desktop).
""";
}

internal sealed class Arguments
{
    public List<string> Words { get; } = [];
    private readonly Dictionary<string, string?> options = [];
    private readonly Dictionary<string, List<string>> multi = [];
    private static readonly HashSet<string> Flags = ["json", "help", "wait", "force", "follow", "all", "queue", "hold", "release", "full", "no-console", "fresh-profile", "include-hidden", "continue-on-error", "single"];
    private static readonly HashSet<string> Values = ["home", "vm", "config", "iso", "guest", "edition", "name", "output", "offset", "args", "exe", "app-id", "pid", "automation-id", "text", "keys",
        "window", "monitor", "cwd", "button", "path", "point", "map", "steps", "delay", "timeout", "depth", "limit", "backend", "label", "owner", "ttl", "note", "id", "props", "job", "marker", "file", "artifacts", "nvda-url", "nvda-sha256"];
    private static readonly HashSet<string> Repeatable = ["env", "arg"];
    public Arguments(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) { Words.Add(args[i]); continue; }
            var key = args[i][2..];
            if (Repeatable.Contains(key))
            {
                if (++i == args.Length) throw new WinixException("usage", $"Missing value for --{key}.");
                (multi.TryGetValue(key, out var list) ? list : multi[key] = []).Add(args[i]); continue;
            }
            if (!Flags.Contains(key) && !Values.Contains(key)) throw new WinixException("usage", $"Unknown option --{key}.");
            if (options.ContainsKey(key)) throw new WinixException("usage", $"Duplicate option --{key}.");
            if (Flags.Contains(key)) options[key] = null;
            else { if (++i == args.Length) throw new WinixException("usage", $"Missing value for --{key}."); options[key] = args[i]; }
        }
    }
    public string Word(int index) => index < Words.Count ? Words[index] : throw new WinixException("usage", "Missing command argument; see --help.");
    public bool Flag(string name) => options.ContainsKey(name);
    public string? Option(string name) => options.GetValueOrDefault(name);
    public List<string> Multi(string name) => multi.GetValueOrDefault(name) ?? [];
}
