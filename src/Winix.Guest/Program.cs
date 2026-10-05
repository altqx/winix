using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Winix;

namespace Winix.Guest;

internal static class Program
{
    public const string Root = @"C:\Winix";
    public const string Pipe = "winix-desktop-v1";
    /// <summary>Per-monitor-V2 DPI awareness, so screenshots and coordinates are physical pixels at any scaling.</summary>
    public static bool DpiAware { get; private set; }
    /// <summary>A Winix data directory: the provisioned <c>C:\Winix\&lt;name&gt;</c>, or a fallback inside the writable work tree on older images.</summary>
    public static string Dir(string name)
    {
        var provisioned = Path.Combine(Root, Paths.Id(name));
        if (Directory.Exists(provisioned)) return provisioned;
        var fallback = Path.Combine(Root, "work", ".winix", name);
        Directory.CreateDirectory(fallback);
        return fallback;
    }
    public static async Task<int> Main(string[] args)
    {
        try { DpiAware = SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { DpiAware = false; }
        try
        {
            if (args is ["ui"])
            {
                // Used by a test process already launched on the interactive desktop.
                var request = Wire.Decode<GuestRequest>(await Console.In.ReadLineAsync() ?? "");
                using var desktop = new Desktop();
                Console.WriteLine(Wire.Encode(await desktop.Call(request.Method, request.Arguments)));
                return 0;
            }
            if (args is ["rpc"])
            {
                var request = await Console.In.ReadLineAsync() ?? throw new WinixException("invalid_request", "Expected one JSON request.");
                using var pipe = new NamedPipeClientStream(".", Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.ConnectAsync(10000); }
                catch (TimeoutException) { throw new WinixException("desktop_unavailable", "The Winix desktop worker is not running. Log into the test account."); }
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await writer.WriteLineAsync(request);
                Console.WriteLine(await reader.ReadLineAsync());
                return 0;
            }
            if (args is ["elevated"]) return await Elevation.RunPending();
            if (args is not ["worker"]) { Console.Error.WriteLine("winix-guest worker | rpc | ui | elevated"); return 2; }
            using var instance = new Mutex(true, "Local\\WinixDesktopWorker", out var created);
            if (!created) throw new WinixException("worker_running", "A desktop worker is already running.");
            // Started by Task Scheduler in the interactive session, a console app gets a terminal window that can take
            // the foreground away from the app under test. Hide it; the worker logs nothing to the console anyway.
            var console = GetConsoleWindow();
            if (console != IntPtr.Zero) ShowWindow(console, 0);
            foreach (var dir in new[] { "work", "jobs", "inbox", "exports" }) Directory.CreateDirectory(Path.Combine(Root, dir));
            using var dispatcher = new Dispatcher();
            while (true)
            {
                var server = new NamedPipeServerStream(Pipe, PipeDirection.InOut, 16, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync();
                _ = Handle(server, dispatcher);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(Wire.Encode(new GuestResponse(false, Wire.Element(new { }), (e as WinixException)?.Code ?? "guest_error", e.Message)));
            return args is ["rpc"] ? 0 : 1;
        }
    }
    private static async Task Handle(NamedPipeServerStream pipe, Dispatcher dispatcher)
    {
        await using (pipe)
        {
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            GuestResponse response;
            try
            {
                var line = await reader.ReadLineAsync() ?? throw new WinixException("invalid_request", "Empty request.");
                var result = await dispatcher.Call(Wire.Decode<GuestRequest>(line));
                response = new(true, Wire.Element(result));
            }
            catch (Exception e) { response = new(false, Wire.Element(new { }), (e as WinixException)?.Code ?? "guest_error", e.Message); }
            try { await writer.WriteLineAsync(Wire.Encode(response)); } catch (IOException) { /* Job state is durable even if SSH disconnected. */ }
        }
    }
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
}
