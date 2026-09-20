using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA2;
using FlaUI.UIA3;
using Winix;

namespace Winix.Guest;

internal sealed class Desktop : IDisposable
{
    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread thread;
    public Desktop()
    {
        thread = new Thread(() => { foreach (var action in queue.GetConsumingEnumerable()) action(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    public Task<object> Call(string action, JsonElement args)
    {
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Add(() => { try { completion.SetResult(Execute(action, args)); } catch (Exception e) { completion.SetException(e); } });
        return completion.Task;
    }
    private static string? String(JsonElement args, string name) => args.TryGetProperty(name, out var p) ? p.GetString() : null;
    private static int Number(JsonElement args, string name, int fallback = 0) => args.TryGetProperty(name, out var p) ? p.GetInt32() : fallback;
    public static bool IsUnlocked()
    {
        if (!System.Environment.UserInteractive) return false;
        var desktop = OpenInputDesktop(0, false, 0x0100);
        if (desktop == IntPtr.Zero) return false;
        try { return SwitchDesktop(desktop); } finally { CloseDesktop(desktop); }
    }
    private static object Execute(string action, JsonElement args)
    {
        if (!IsUnlocked()) throw new WinixException("desktop_locked", "The desktop is locked, unavailable, or displaying a secure prompt.");
        if (action == "screenshot")
        {
            var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
            using var bitmap = new Bitmap(screen.Width, screen.Height);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(screen.Location, Point.Empty, screen.Size);
            using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png);
            return new { mimeType = "image/png", data = Convert.ToBase64String(stream.ToArray()), width = screen.Width, height = screen.Height };
        }
        if (action == "launch")
        {
            if (String(args, "appId") is { } appId)
            {
                var manager = (IApplicationActivationManager)new ApplicationActivationManager();
                var hr = manager.ActivateApplication(appId, String(args, "arguments") ?? "", 0, out var pid);
                Marshal.ThrowExceptionForHR(hr); return new { pid };
            }
            var executable = String(args, "exe") ?? throw new WinixException("invalid_args", "Provide exe or appId.");
            // Shell launch keeps the GUI from inheriting the short-lived UI client's
            // redirected pipes, which would keep PowerShell waiting for the app to exit.
            var start = new ProcessStartInfo(executable) { UseShellExecute = true };
            if (args.TryGetProperty("arguments", out var arguments)) foreach (var argument in arguments.EnumerateArray()) start.ArgumentList.Add(argument.GetString()!);
            if (String(args, "cwd") is { } cwd) start.WorkingDirectory = cwd;
            using var process = Process.Start(start) ?? throw new WinixException("launch_failed", executable);
            return new { pid = process.Id };
        }
        if (action is "deploy" or "uninstall")
        {
            var value = String(args, action == "deploy" ? "package" : "packageFullName") ?? throw new WinixException("invalid_args", "Package is required.");
            var quoted = "'" + value.Replace("'", "''") + "'";
            var script = action == "deploy" ? $"$ErrorActionPreference='Stop'; Add-AppxPackage -Path {quoted}" : $"$ErrorActionPreference='Stop'; Remove-AppxPackage -Package {quoted}";
            if (action == "deploy" && args.TryGetProperty("dependencies", out var dependencies) && dependencies.GetArrayLength() > 0)
                script += " -DependencyPath " + string.Join(',', dependencies.EnumerateArray().Select(d => "'" + d.GetString()!.Replace("'", "''") + "'"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            new CommandRunner().Checked("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))], ct: timeout.Token).GetAwaiter().GetResult();
            return new { ok = true };
        }
        if (action == "click" && args.TryGetProperty("x", out var x))
        {
            Mouse.Click(new Point(x.GetInt32(), Number(args, "y"))); return new { ok = true };
        }
        if (action == "keys")
        {
            var keys = (String(args, "keys") ?? "").Split('+').Select(k => k.ToUpperInvariant() switch
            {
                "CTRL" => VirtualKeyShort.CONTROL, "ALT" => VirtualKeyShort.ALT, "SHIFT" => VirtualKeyShort.SHIFT,
                "ENTER" => VirtualKeyShort.RETURN, "ESC" => VirtualKeyShort.ESCAPE,
                var key => Enum.TryParse<VirtualKeyShort>(key.Length == 1 && char.IsLetterOrDigit(key[0]) ? "KEY_" + key : key, out var parsed) ? parsed : throw new WinixException("invalid_key", k)
            }).ToArray();
            Keyboard.TypeSimultaneously(keys); return new { ok = true };
        }
        using AutomationBase automation = String(args, "backend") == "uia2" ? new UIA2Automation() : new UIA3Automation();
        if (automation is UIA3Automation uia3) { uia3.ConnectionTimeout = TimeSpan.FromSeconds(5); uia3.TransactionTimeout = TimeSpan.FromSeconds(5); }
        var desktopRoot = automation.GetDesktop();
        if (action == "windows") return desktopRoot.FindAllChildren().Select(Summary).ToArray();
        var processId = Number(args, "pid");
        if (processId <= 0) throw new WinixException("invalid_args", "A process pid is required for scoped UI operations.");
        AutomationElement? LocateRoot() => desktopRoot.FindAllChildren(cf => cf.ByProcessId(processId)).FirstOrDefault()
            ?? desktopRoot.FindFirstDescendant(cf => cf.ByProcessId(processId));
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Clamp(Number(args, "timeoutMs", 10000), 1, 60000));
        var root = LocateRoot();
        while (root is null && action == "wait" && DateTime.UtcNow < deadline) { Thread.Sleep(100); root = LocateRoot(); }
        if (root is null) throw new WinixException("window_not_found", $"No window for process {processId}.");
        if (action == "wait" && String(args, "automationId") is null && String(args, "name") is null) return Summary(root);
        if (action == "close")
        {
            // Packaged apps can expose their content under an ApplicationFrameHost
            // window owned by another process. Close the containing frame, not its host.
            var walker = automation.TreeWalkerFactory.GetControlViewWalker();
            AutomationElement? window = root;
            for (var depth = 0; window is not null && depth < 8; depth++, window = walker.GetParent(window))
                if (window.ControlType == FlaUI.Core.Definitions.ControlType.Window && window.Patterns.Window.IsSupported)
                { window.AsWindow().Close(); return new { ok = true }; }
            using var process = Process.GetProcessById(processId);
            if (!process.CloseMainWindow()) throw new WinixException("close_unavailable", "This application does not expose a closeable main window.");
            return new { ok = true };
        }
        if (action == "tree")
        {
            var remaining = Math.Clamp(Number(args, "limit", 200), 1, 1000);
            return Tree(root, 0, Math.Clamp(Number(args, "depth", 4), 0, 12), ref remaining);
        }
        AutomationElement Find()
        {
            var aid = String(args, "automationId"); var name = String(args, "name");
            if (aid is null && name is null) throw new WinixException("invalid_selector", "Provide automationId or name.");
            var matches = root.FindAllDescendants(cf => aid is not null && name is not null ? cf.ByAutomationId(aid).And(cf.ByName(name)) : aid is not null ? cf.ByAutomationId(aid) : cf.ByName(name!));
            return matches.Length switch { 0 => throw new WinixException("element_not_found", aid ?? name!), 1 => matches[0], _ => throw new WinixException("ambiguous_selector", $"Selector matched {matches.Length} controls.") };
        }
        AutomationElement element;
        if (action == "wait")
        {
            while (true)
            {
                try { element = Find(); break; }
                catch (WinixException e) when (e.Code == "element_not_found" && DateTime.UtcNow < deadline) { Thread.Sleep(100); }
            }
        }
        else element = Find();
        switch (action)
        {
            case "find": case "wait": return Summary(element);
            case "click":
                if (element.Patterns.Invoke.IsSupported) element.Patterns.Invoke.Pattern.Invoke();
                else { element.Focus(); element.Click(); }
                return new { ok = true };
            case "type": element.AsTextBox().Text = String(args, "text") ?? ""; return new { ok = true };
            case "assert":
                var actual = element.Patterns.Value.IsSupported ? element.Patterns.Value.Pattern.Value.Value : element.Name;
                var expected = String(args, "text") ?? "";
                while (actual != expected && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(100); element = Find();
                    actual = element.Patterns.Value.IsSupported ? element.Patterns.Value.Pattern.Value.Value : element.Name;
                }
                if (actual != expected) throw new WinixException("assertion_failed", $"Expected '{expected}', got '{actual}'.");
                return new { ok = true, actual };
            default: throw new WinixException("unknown_action", action);
        }
    }
    private static object Summary(AutomationElement e) => new { name = e.Properties.Name.ValueOrDefault, automationId = e.Properties.AutomationId.ValueOrDefault,
        controlType = e.Properties.ControlType.ValueOrDefault.ToString(), pid = e.Properties.ProcessId.ValueOrDefault,
        enabled = e.Properties.IsEnabled.ValueOrDefault, bounds = new { x = e.BoundingRectangle.X, y = e.BoundingRectangle.Y, width = e.BoundingRectangle.Width, height = e.BoundingRectangle.Height } };
    private static object Tree(AutomationElement e, int depth, int maxDepth, ref int remaining)
    {
        remaining--; List<object> children = [];
        if (depth < maxDepth && remaining > 0)
            foreach (var child in e.FindAllChildren()) { if (remaining <= 0) break; children.Add(Tree(child, depth + 1, maxDepth, ref remaining)); }
        return new { element = Summary(e), children, truncated = remaining <= 0 };
    }
    public void Dispose() { queue.CompleteAdding(); thread.Join(1000); }
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool SwitchDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] private class ApplicationActivationManager { }
    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
        [PreserveSig] int ActivateForFile(IntPtr a, IntPtr b, IntPtr c, out uint processId);
        [PreserveSig] int ActivateForProtocol(IntPtr a, IntPtr b, out uint processId);
    }
}
