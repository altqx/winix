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
            var configFile = Path.GetFullPath(args.Option("config") ?? "winix.yaml");
            var root = Path.GetDirectoryName(configFile)!;
            using var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
            object result;
            switch (args.Word(0))
            {
                case "init":
                    if (File.Exists(configFile)) throw new WinixException("config_exists", "Configuration already exists.");
                    File.WriteAllText(configFile, DefaultConfig); result = new { config = configFile }; break;
                case "doctor":
                    var checks = await service.Doctor(cancel.Token); Print(checks, json); return checks.All(c => c.Ok) ? 0 : 3;
                case "config": result = WinixConfig.Load(configFile); break;
                case "media":
                    var path = Path.GetFullPath(args.Option("output") ?? Path.Combine(store.Root, "media", "windows11.iso"));
                    result = new { path = await Media.Download(runner, Media.WindowsUrl, path, Media.WindowsSha256, cancel.Token) }; break;
                case "image":
                    var builder = new ImageBuilder(store, runner);
                    result = args.Word(1) switch
                    {
                        "build" => await builder.Build(args.Option("name") ?? "windows-dev", args.Option("iso"),
                            Path.GetFullPath(args.Option("guest") ?? (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "guest")) ? Path.Combine(AppContext.BaseDirectory, "guest") : "artifacts/guest")), args.Option("edition") ?? "Community"),
                        "seal" => await builder.Seal(args.Option("vm") ?? "build-windows-dev", args.Option("name") ?? "windows-dev"),
                        "list" => store.List<ImageManifest>("images"),
                        "status" => await service.Vms.ProvisionStatus(args.Option("vm") ?? "build-windows-dev"),
                        _ => throw new WinixException("usage", "image build|seal|list")
                    }; break;
                case "vm":
                    var name = args.Words.Count > 2 ? args.Word(2) : vm;
                    switch (args.Word(1))
                    {
                        case "up":
                            var config = WinixConfig.Load(configFile);
                            result = await service.Vms.Up(name, config.Image, config.Resources);
                            if (args.Flag("wait")) await service.Ready(name, ct: cancel.Token);
                            break;
                        case "down": await service.Vms.Down(name, args.Flag("force")); result = new { vm = name, status = "shut off" }; break;
                        case "delete": await service.Vms.Delete(name); result = new { deleted = name }; break;
                        case "status": result = new { vm = name, status = await service.Vms.Status(name) }; break;
                        case "list": result = store.List<VmRecord>("vms"); break;
                        case "ready": result = await service.Ready(name, ct: cancel.Token); break;
                        case "trust": await service.Vms.PinHostKey(store.Read<VmRecord>("vms", name)); result = new { vm = name, trusted = true }; break;
                        default: throw new WinixException("usage", "vm up|down|delete|status|list|ready|trust [name]");
                    }
                    break;
                case "console":
                    await runner.Checked("virt-viewer", ["--connect", VmManager.Connection, "winix-" + Paths.Id(vm)], ct: cancel.Token);
                    result = new { vm }; break;
                case "sync": result = await service.Sync(vm, args.Words.Count > 1 ? Path.GetFullPath(args.Word(1)) : root, configFile); break;
                case "run":
                    var started = await service.Run(vm, configFile, args.Word(1));
                    if (args.Flag("wait")) Console.Error.WriteLine($"Waiting for {started.Id}; interrupting this client leaves the Windows job running.");
                    var record = args.Flag("wait") ? await service.Wait(started.Id, cancel.Token) : started;
                    Print(record, json); return record.Terminal && record.State != JobState.Succeeded ? 1 : 0;
                case "job":
                    var id = args.Word(2);
                    result = args.Word(1) switch
                    {
                        "wait" => await service.Wait(id, cancel.Token),
                        "status" or "cancel" or "logs" => await service.Job(id, args.Word(1), long.Parse(args.Option("offset") ?? "0")),
                        "artifacts" => new { path = await service.Artifacts(id, args.Option("output") ?? "artifacts/runs") },
                        _ => throw new WinixException("usage", "job status|logs|wait|cancel|artifacts <id>")
                    };
                    if (result is JobRecord completed) { Print(completed, json); return completed.State == JobState.Succeeded ? 0 : 1; }
                    break;
                case "ui":
                    var action = args.Word(1);
                    var uiArgs = args.Option("args") is { } supplied ? Wire.Decode<JsonElement>(supplied) : Wire.Element(new Dictionary<string, object?>
                    {
                        ["exe"] = args.Option("exe"), ["appId"] = args.Option("app-id"), ["pid"] = int.Parse(args.Option("pid") ?? "0"),
                        ["automationId"] = args.Option("automation-id"), ["name"] = args.Option("name"), ["text"] = args.Option("text"), ["keys"] = args.Option("keys")
                    }.Where(p => p.Value is not null).ToDictionary());
                    var response = await service.Ui(vm, action, uiArgs);
                    if (action == "screenshot")
                    {
                        var output = Path.GetFullPath(args.Option("output") ?? "winix-screenshot.png");
                        await File.WriteAllBytesAsync(output, Convert.FromBase64String(response.GetProperty("data").GetString()!), cancel.Token);
                        result = new { path = output };
                    }
                    else result = response;
                    break;
                case "ci":
                    result = await service.Ci(configFile, root, args.Option("output") ?? "artifacts/runs", cancel.Token);
                    Print(result, json); return Wire.Element(result).GetProperty("succeeded").GetBoolean() ? 0 : 1;
                case "mcp": await McpHost.Run(service, cancel.Token); return 0;
                default: throw new WinixException("usage", "Unknown command. Run winix --help.");
            }
            Print(result, json); return 0;
        }
        catch (OperationCanceledException) { Print(new { error = "cancelled", message = "Client wait cancelled. Submitted jobs remain queryable by ID." }, json); return 130; }
        catch (Exception e)
        {
            var error = new { error = (e as WinixException)?.Code ?? "error", message = e.Message };
            if (json) Console.WriteLine(Wire.Encode(error)); else Console.Error.WriteLine($"{error.error}: {error.message}");
            return e is WinixException { Code: "usage" or "invalid_id" or "invalid_task" or "config_missing" } ? 2 : 3;
        }
    }
    private static void Print(object value, bool json) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions(Wire.Json) { WriteIndented = !json }));
    private const string Help = """
Winix — Windows build and desktop testing from Linux

  init                            Create winix.yaml
  doctor                          Check host prerequisites (exit 3 if missing)
  config                          Validate and print project configuration
  media download                  Download/checksum Windows 11 from Microsoft
  image build [--iso PATH]         Provision Windows + full Visual Studio Community
             [--guest DIR] [--edition Community|Professional|Enterprise]
  image seal --vm NAME --name ID   Shut down and capture a verified baseline
  image list                      List immutable images
  image status --vm NAME          Read provisioning stage through QEMU guest agent
  vm up|down|delete|status NAME    Manage a development VM (--force for hard stop)
  vm list|ready|trust [NAME]       Inventory, desktop readiness, QGA host-key pinning
  console --vm NAME               Open the virtual display
  sync [DIRECTORY]                Copy working-tree changes into Windows
  run TASK [--wait]               Start a durable build/test task
  job status|logs|wait|cancel ID   Inspect/cancel work (--offset BYTES for logs)
  job artifacts ID [--output DIR] Retrieve logs, reports and binaries as ZIP
  ui ACTION [--args JSON]         windows, launch, close, tree, find, click, type,
                                 keys, wait, assert, screenshot, deploy, uninstall
  ci                              Clean overlay → sync → pipeline → export → cleanup
  mcp                             Strict MCP 2026-07-28 stdio server

Global: --json --home DIR --vm NAME --config PATH
UI: --exe PATH --app-id ID --pid N --automation-id ID --name NAME --text TEXT
    --keys CTRL+S --output PATH
Image default: windows-dev. VM default: dev. Config default: ./winix.yaml.
Exit: 0 success, 1 task/test failure, 2 invalid usage, 3 infrastructure, 130 interrupted.
""";
    private const string DefaultConfig = """
version: 1
image: windows-dev
workspace: app
resources:
  cpus: 6
  memoryGiB: 12
  diskGiB: 180
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
""";
}

internal sealed class Arguments
{
    public List<string> Words { get; } = [];
    private readonly Dictionary<string, string?> options = [];
    private static readonly HashSet<string> Flags = ["json", "help", "wait", "force"];
    private static readonly HashSet<string> Values = ["home", "vm", "config", "iso", "guest", "edition", "name", "output", "offset", "args", "exe", "app-id", "pid", "automation-id", "text", "keys"];
    public Arguments(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) { Words.Add(args[i]); continue; }
            var key = args[i][2..];
            if (!Flags.Contains(key) && !Values.Contains(key)) throw new WinixException("usage", $"Unknown option --{key}.");
            if (options.ContainsKey(key)) throw new WinixException("usage", $"Duplicate option --{key}.");
            if (Flags.Contains(key)) options[key] = null;
            else { if (++i == args.Length) throw new WinixException("usage", $"Missing value for --{key}."); options[key] = args[i]; }
        }
    }
    public string Word(int index) => index < Words.Count ? Words[index] : throw new WinixException("usage", "Missing command argument; see --help.");
    public bool Flag(string name) => options.ContainsKey(name);
    public string? Option(string name) => options.GetValueOrDefault(name);
}
