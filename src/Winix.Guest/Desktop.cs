using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
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
    private static string? String(JsonElement args, string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? p.ToString() : null;
    private static int Number(JsonElement args, string name, int fallback = 0) => String(args, name) is { } s && int.TryParse(s, out var v) ? v : fallback;
    private static bool Flag(JsonElement args, string name) => String(args, name) is "true" or "True" or "1";
    public static bool IsUnlocked()
    {
        if (!System.Environment.UserInteractive) return false;
        var desktop = OpenInputDesktop(0, false, 0x0100);
        if (desktop == IntPtr.Zero) return false;
        try { return SwitchDesktop(desktop); } finally { CloseDesktop(desktop); }
    }

    /// <summary>One UI Automation connection shared by every step of a call (or batch).</summary>
    private sealed class Session(JsonElement args) : IDisposable
    {
        private AutomationBase? automation;
        public AutomationBase Automation
        {
            get
            {
                if (automation is not null) return automation;
                automation = String(args, "backend") == "uia2" ? new UIA2Automation() : new UIA3Automation();
                if (automation is UIA3Automation uia3) { uia3.ConnectionTimeout = TimeSpan.FromSeconds(5); uia3.TransactionTimeout = TimeSpan.FromSeconds(5); }
                return automation;
            }
        }
        public AutomationElement DesktopRoot => Automation.GetDesktop();
        public void Dispose() => automation?.Dispose();
    }

    private static object Execute(string action, JsonElement args)
    {
        if (action == "monitors") return Monitors(); // Needed by the host pointer mapping even when the desktop is locked.
        if (!IsUnlocked()) throw new WinixException("desktop_locked", "The desktop is locked, unavailable, or displaying a secure prompt.");
        using var session = new Session(args);
        if (action != "batch") return Run(action, args, session);
        if (!args.TryGetProperty("steps", out var stepsElement) || stepsElement.ValueKind != JsonValueKind.Array) throw new WinixException("invalid_args", "batch needs steps: [{action, ...}].");
        var steps = stepsElement.EnumerateArray().ToList(); var continueOnError = Flag(args, "continueOnError");
        var results = new List<object>(); var watch = Stopwatch.StartNew(); var failed = 0;
        foreach (var step in steps)
        {
            var name = String(step, "action") ?? throw new WinixException("invalid_args", "Each batch step needs an action.");
            var stepArgs = step.TryGetProperty("args", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : step;
            if (name is "batch" or "monitors") throw new WinixException("invalid_args", $"{name} cannot be nested in a batch.");
            try { results.Add(new { action = name, ok = true, result = Run(name, stepArgs, session) }); }
            catch (Exception e)
            {
                results.Add(new { action = name, ok = false, code = (e as WinixException)?.Code ?? "guest_error", error = e.Message }); failed++;
                if (!continueOnError) break;
            }
        }
        return new { steps = results, completed = results.Count, total = steps.Count, failed, elapsedMs = watch.ElapsedMilliseconds };
    }

    private static object Run(string action, JsonElement args, Session session)
    {
        switch (action)
        {
            case "screenshot": return Screenshot(args);
            case "launch": return Launch(args);
            case "deploy" or "uninstall": return Package(action, args);
            case "keys": SendKeys(String(args, "keys") ?? "", Number(args, "delayMs", 30)); return new { ok = true };
            case "text": SendText(String(args, "text") ?? ""); return new { ok = true };
            case "state": return State(session);
            case "kill":
                using (var victim = Process.GetProcessById(Number(args, "pid"))) victim.Kill(!Flag(args, "single"));
                return new { ok = true, pid = Number(args, "pid") };
            case "click" when args.TryGetProperty("x", out var x):
                Mouse.Click(new Point(x.GetInt32(), Number(args, "y"))); return new { ok = true };
            case "windows" when Number(args, "pid") <= 0:
                return session.DesktopRoot.FindAllChildren().Select(Summary).ToArray();
        }
        var processId = Number(args, "pid");
        if (processId <= 0) throw new WinixException("invalid_args", "A process pid is required for scoped UI operations.");
        var includeHidden = Flag(args, "includeHidden");
        if (action == "windows") return TopLevelWindows(processId, includeHidden).Select(WindowInfo).ToArray();
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Clamp(Number(args, "timeoutMs", 10000), 1, 60000));
        if (action == "focus")
        {
            var windows = TopLevelWindows(processId, includeHidden);
            var target = SelectWindow(windows, args) ?? windows.FirstOrDefault() ?? throw new WinixException("window_not_found", $"No window for process {processId}.");
            var ok = Foreground(target.Handle);
            if (!ok) throw new WinixException("focus_failed", $"Windows refused to bring {target.Title} to the foreground.");
            return new { ok = true, window = WindowInfo(target) };
        }
        var roots = Roots(session, processId, args, action == "wait" ? deadline : DateTime.MinValue, out var allWindows);
        if (roots.Count == 0) throw new WinixException("window_not_found", $"No window for process {processId}.");
        if (action == "wait" && String(args, "automationId") is null && String(args, "name") is null) return Summary(roots[0]);
        if (action == "close")
        {
            // Packaged apps can expose their content under an ApplicationFrameHost
            // window owned by another process. Close the containing frame, not its host.
            var walker = session.Automation.TreeWalkerFactory.GetControlViewWalker();
            AutomationElement? window = roots[0];
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
            var walker = session.Automation.TreeWalkerFactory.GetControlViewWalker();
            var rootHandle = Handle(roots[0]);
            var others = allWindows.Select(w => (long)w.Handle).Where(h => h != rootHandle).ToHashSet();
            var tree = (Dictionary<string, object?>)Tree(roots[0], walker, others, 0, Math.Clamp(Number(args, "depth", 4), 0, 12), ref remaining);
            tree["windows"] = allWindows.Select(WindowInfo).ToArray();
            return tree;
        }
        AutomationElement Find() => FindOne(roots, args);
        AutomationElement element;
        if (action == "wait")
        {
            while (true)
            {
                try { element = Find(); break; }
                catch (WinixException e) when (e.Code == "element_not_found" && DateTime.UtcNow < deadline)
                { Thread.Sleep(100); roots = Roots(session, processId, args, DateTime.MinValue, out allWindows); }
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
            case "rightclick": element.RightClick(); return new { ok = true };
            case "doubleclick": element.DoubleClick(); return new { ok = true };
            case "setfocus": element.Focus(); return Summary(element);
            case "type": element.AsTextBox().Text = String(args, "text") ?? ""; return new { ok = true };
            case "assert":
                var actual = Value(element);
                var expected = String(args, "text") ?? "";
                while (actual != expected && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(100); element = Find();
                    actual = Value(element);
                }
                if (actual != expected) throw new WinixException("assertion_failed", $"Expected '{expected}', got '{actual}'.");
                return new { ok = true, actual };
            default: throw new WinixException("unknown_action", action);
        }
    }
    private static string? Value(AutomationElement element) => element.Patterns.Value.IsSupported ? element.Patterns.Value.Pattern.Value.Value : element.Name;

    // ---- Windows of a process: every visible top-level window, owned dialogs and floating panels included. ----
    internal sealed record Win(IntPtr Handle, string Title, string ClassName, IntPtr Owner, bool Visible, int Pid, uint Thread);
    private static List<Win> TopLevelWindows(int pid, bool includeHidden)
    {
        List<Win> list = [];
        EnumWindows((handle, _) =>
        {
            var thread = GetWindowThreadProcessId(handle, out var owner);
            if ((int)owner != pid) return true;
            var visible = IsWindowVisible(handle);
            if (visible || includeHidden) list.Add(new(handle, WindowText(handle), ClassName(handle), GetWindow(handle, 4), visible, pid, thread));
            return true;
        }, IntPtr.Zero);
        return list; // z-order, topmost first
    }
    private static Win? SelectWindow(List<Win> windows, JsonElement args)
    {
        if (!args.TryGetProperty("window", out var selector) || selector.ValueKind == JsonValueKind.Null) return null;
        if (selector.ValueKind == JsonValueKind.Number && selector.TryGetInt32(out var index)) return ByIndex(index);
        var text = selector.GetString() ?? "";
        if (int.TryParse(text, out index)) return ByIndex(index);
        if (text.StartsWith("hwnd:", StringComparison.OrdinalIgnoreCase) && long.TryParse(text[5..], out var hwnd))
            return windows.FirstOrDefault(w => (long)w.Handle == hwnd) ?? throw new WinixException("window_not_found", $"No window with handle {hwnd} in this process.");
        var matches = windows.Where(w => w.Title.Contains(text, StringComparison.OrdinalIgnoreCase) || w.ClassName.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1) { var exact = matches.Where(w => w.Title.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList(); if (exact.Count == 1) return exact[0]; }
        return matches.Count switch
        {
            0 => throw new WinixException("window_not_found", $"No window matching '{text}'. Windows: {string.Join(" | ", windows.Select(w => $"[{windows.IndexOf(w)}] {w.Title} ({w.ClassName})"))}"),
            1 => matches[0],
            _ => throw new WinixException("ambiguous_window", $"'{text}' matches {matches.Count} windows; use an index or hwnd: {string.Join(" | ", matches.Select(w => $"[{windows.IndexOf(w)}] {w.Title} hwnd:{(long)w.Handle}"))}")
        };
        Win ByIndex(int i) => i >= 0 && i < windows.Count ? windows[i] : throw new WinixException("window_not_found", $"Window index {i} out of range; the process has {windows.Count} windows.");
    }
    /// <summary>UI Automation roots for a process: the selected window, or every top-level window. Falls back to UIA lookup for packaged apps hosted by another process.</summary>
    private static List<AutomationElement> Roots(Session session, int pid, JsonElement args, DateTime waitUntil, out List<Win> windows)
    {
        while (true)
        {
            windows = TopLevelWindows(pid, Flag(args, "includeHidden"));
            List<AutomationElement> roots = [];
            WinixException? pending = null;
            try
            {
                var selected = SelectWindow(windows, args);
                if (selected is not null) roots.Add(session.Automation.FromHandle(selected.Handle));
                else foreach (var window in windows) { try { roots.Add(session.Automation.FromHandle(window.Handle)); } catch (COMException) { } }
            }
            catch (WinixException e) { pending = e; }
            if (roots.Count == 0 && pending is null)
            {
                var fallback = session.DesktopRoot.FindAllChildren(cf => cf.ByProcessId(pid)).FirstOrDefault() ?? session.DesktopRoot.FindFirstDescendant(cf => cf.ByProcessId(pid));
                if (fallback is not null) roots.Add(fallback);
            }
            if (roots.Count > 0 || DateTime.UtcNow >= waitUntil) { if (roots.Count == 0 && pending is not null) throw pending; return roots; }
            Thread.Sleep(100);
        }
    }
    private static AutomationElement FindOne(List<AutomationElement> roots, JsonElement args)
    {
        var aid = String(args, "automationId"); var name = String(args, "name");
        if (aid is null && name is null) throw new WinixException("invalid_selector", "Provide automationId or name.");
        var seen = new HashSet<string>(); List<AutomationElement> matches = [];
        void Add(AutomationElement e) { var id = string.Join('.', e.Properties.RuntimeId.ValueOrDefault ?? []); if (id.Length == 0 || seen.Add(id)) matches.Add(e); }
        foreach (var root in roots)
        {
            if ((aid is null || root.Properties.AutomationId.ValueOrDefault == aid) && (name is null || root.Properties.Name.ValueOrDefault == name)) Add(root);
            foreach (var match in root.FindAllDescendants(cf => aid is not null && name is not null ? cf.ByAutomationId(aid).And(cf.ByName(name)) : aid is not null ? cf.ByAutomationId(aid) : cf.ByName(name!))) Add(match);
        }
        return matches.Count switch { 0 => throw new WinixException("element_not_found", aid ?? name!), 1 => matches[0], _ => throw new WinixException("ambiguous_selector", $"Selector matched {matches.Count} controls.") };
    }

    // ---- Focus and state ----
    private static bool Foreground(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
        var current = GetCurrentThreadId();
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        var target = GetWindowThreadProcessId(hwnd, out _);
        var attachedForeground = foregroundThread != 0 && foregroundThread != current && AttachThreadInput(current, foregroundThread, true);
        var attachedTarget = target != current && AttachThreadInput(current, target, true);
        try { BringWindowToTop(hwnd); SetForegroundWindow(hwnd); }
        finally
        {
            if (attachedForeground) AttachThreadInput(current, foregroundThread, false);
            if (attachedTarget) AttachThreadInput(current, target, false);
        }
        for (var i = 0; i < 10 && GetForegroundWindow() != hwnd; i++) Thread.Sleep(50);
        if (GetForegroundWindow() == hwnd) return true;
        // A key tap grants this process the foreground lock Windows otherwise withholds from background callers.
        Send([Key(VirtualKeyShort.ALT, false), Key(VirtualKeyShort.ALT, true)]);
        SetForegroundWindow(hwnd);
        for (var i = 0; i < 10 && GetForegroundWindow() != hwnd; i++) Thread.Sleep(50);
        return GetForegroundWindow() == hwnd;
    }
    private static object State(Session session)
    {
        var foreground = GetForegroundWindow();
        object? focused = null; List<object> ancestors = [];
        try
        {
            var element = session.Automation.FocusedElement();
            focused = Summary(element);
            var walker = session.Automation.TreeWalkerFactory.GetControlViewWalker();
            for (var parent = walker.GetParent(element); parent is not null && ancestors.Count < 25; parent = walker.GetParent(parent)) ancestors.Add(Summary(parent));
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or NullReferenceException) { }
        GetCursorPos(out var cursor);
        return new { foreground = foreground == IntPtr.Zero ? null : WindowInfo(Describe(foreground)), focused, ancestors, cursor = new { x = cursor.X, y = cursor.Y }, locked = false, dpiAware = Program.DpiAware };
    }
    private static Win Describe(IntPtr handle)
    {
        var thread = GetWindowThreadProcessId(handle, out var pid);
        return new(handle, WindowText(handle), ClassName(handle), GetWindow(handle, 4), IsWindowVisible(handle), (int)pid, thread);
    }
    private static object WindowInfo(Win w)
    {
        GetWindowRect(w.Handle, out var rect);
        return new { hwnd = (long)w.Handle, title = w.Title, className = w.ClassName, pid = w.Pid, owner = w.Owner == IntPtr.Zero ? (long?)null : (long)w.Owner, owned = w.Owner != IntPtr.Zero,
            visible = w.Visible, minimized = IsIconic(w.Handle), foreground = GetForegroundWindow() == w.Handle,
            bounds = new { x = rect.Left, y = rect.Top, width = rect.Right - rect.Left, height = rect.Bottom - rect.Top } };
    }

    // ---- Launch ----
    private static object Launch(JsonElement args)
    {
        if (String(args, "appId") is { } appId)
        {
            var manager = (IApplicationActivationManager)new ApplicationActivationManager();
            var hr = manager.ActivateApplication(appId, String(args, "arguments") ?? "", 0, out var pid);
            Marshal.ThrowExceptionForHR(hr); return new { pid };
        }
        var executable = String(args, "exe") ?? throw new WinixException("invalid_args", "Provide exe or appId.");
        var environment = new Dictionary<string, string>();
        if (args.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object) foreach (var p in env.EnumerateObject()) environment[TaskSpec.ValidateEnvName(p.Name)] = p.Value.ToString();
        var noConsole = Flag(args, "noConsole"); var freshProfile = Flag(args, "freshProfile");
        if (environment.Count == 0 && !noConsole && !freshProfile)
        {
            // Shell launch keeps the GUI from inheriting the short-lived UI client's
            // redirected pipes, which would keep PowerShell waiting for the app to exit.
            var shell = new ProcessStartInfo(executable) { UseShellExecute = true };
            if (args.TryGetProperty("arguments", out var arguments)) foreach (var argument in arguments.EnumerateArray()) shell.ArgumentList.Add(argument.GetString()!);
            if (String(args, "cwd") is { } directory) shell.WorkingDirectory = directory;
            using var simple = Process.Start(shell) ?? throw new WinixException("launch_failed", executable);
            return new { pid = simple.Id };
        }
        // Environment, hidden console and profile redirection need CreateProcess; standard handles are
        // redirected to a log so the app never inherits a caller's pipes.
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = noConsole, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (args.TryGetProperty("arguments", out var list)) foreach (var argument in list.EnumerateArray()) start.ArgumentList.Add(argument.GetString()!);
        if (String(args, "cwd") is { } cwd) start.WorkingDirectory = cwd;
        foreach (var (key, value) in environment) start.Environment[key] = value;
        string? profile = null;
        if (freshProfile)
        {
            profile = Path.Combine(Program.Dir("profiles"), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(Path.Combine(profile, "Roaming")); Directory.CreateDirectory(Path.Combine(profile, "Local"));
            start.Environment["APPDATA"] = Path.Combine(profile, "Roaming"); start.Environment["LOCALAPPDATA"] = Path.Combine(profile, "Local");
        }
        var process = Process.Start(start) ?? throw new WinixException("launch_failed", executable);
        process.StandardInput.Close();
        var log = Path.Combine(Program.Dir("launch"), process.Id + ".log");
        _ = Task.Run(async () =>
        {
            await using var writer = new StreamWriter(log, false, new UTF8Encoding(false)) { AutoFlush = true };
            async Task Pump(StreamReader reader, string tag) { while (await reader.ReadLineAsync() is { } line) { lock (writer) writer.WriteLine($"[{tag}] {line}"); } }
            try { await Task.WhenAll(Pump(process.StandardOutput, "out"), Pump(process.StandardError, "err")); } catch (IOException) { } finally { process.Dispose(); }
        });
        return new { pid = process.Id, profile, log };
    }
    private static object Package(string action, JsonElement args)
    {
        var value = String(args, action == "deploy" ? "package" : "packageFullName") ?? throw new WinixException("invalid_args", "Package is required.");
        var quoted = "'" + value.Replace("'", "''") + "'";
        var script = action == "deploy" ? $"$ErrorActionPreference='Stop'; Add-AppxPackage -Path {quoted}" : $"$ErrorActionPreference='Stop'; Remove-AppxPackage -Package {quoted}";
        if (action == "deploy" && args.TryGetProperty("dependencies", out var dependencies) && dependencies.GetArrayLength() > 0)
            script += " -DependencyPath " + string.Join(',', dependencies.EnumerateArray().Select(d => "'" + d.GetString()!.Replace("'", "''") + "'"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        new CommandRunner().Checked("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], ct: timeout.Token).GetAwaiter().GetResult();
        return new { ok = true };
    }

    // ---- Screens ----
    private sealed record Monitor(int Index, string Device, bool Primary, Rectangle Bounds, Rectangle WorkingArea, int Dpi);
    private static List<Monitor> EnumerateMonitors()
    {
        List<Monitor> monitors = [];
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(handle, ref info)) return true;
            var dpi = GetDpiForMonitor(handle, 0, out var dpiX, out _) == 0 ? (int)dpiX : 96;
            monitors.Add(new(monitors.Count, info.szDevice, (info.dwFlags & 1) != 0, Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom),
                Rectangle.FromLTRB(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom), dpi));
            return true;
        }, IntPtr.Zero);
        return monitors;
    }
    private static Rectangle VirtualScreen() => new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
    private static object Rect(Rectangle r) => new { x = r.X, y = r.Y, width = r.Width, height = r.Height };
    public static object Monitors() => new
    {
        virtualScreen = Rect(VirtualScreen()), dpiAware = Program.DpiAware,
        monitors = EnumerateMonitors().Select(m => new { index = m.Index, deviceName = m.Device, primary = m.Primary, bounds = Rect(m.Bounds), workingArea = Rect(m.WorkingArea), dpi = m.Dpi, scale = m.Dpi / 96.0 }).ToArray()
    };
    private static object Screenshot(JsonElement args)
    {
        var area = VirtualScreen(); int? monitor = null;
        if (String(args, "monitor") is { } m && int.TryParse(m, out var index))
        {
            var monitors = EnumerateMonitors();
            if (index < 0 || index >= monitors.Count) throw new WinixException("invalid_args", $"Monitor {index} does not exist; {monitors.Count} attached.");
            area = monitors[index].Bounds; monitor = index;
        }
        using var bitmap = new Bitmap(area.Width, area.Height);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png);
        return new { mimeType = "image/png", data = Convert.ToBase64String(stream.ToArray()), width = area.Width, height = area.Height, x = area.X, y = area.Y, monitor, dpiAware = Program.DpiAware };
    }

    // ---- Keyboard: scan codes through SendInput, extended flag for navigation keys so screen readers see them as such. ----
    private static readonly HashSet<VirtualKeyShort> ExtendedKeys = [VirtualKeyShort.UP, VirtualKeyShort.DOWN, VirtualKeyShort.LEFT, VirtualKeyShort.RIGHT, VirtualKeyShort.HOME, VirtualKeyShort.END,
        VirtualKeyShort.PRIOR, VirtualKeyShort.NEXT, VirtualKeyShort.INSERT, VirtualKeyShort.DELETE, VirtualKeyShort.RCONTROL, VirtualKeyShort.RMENU, VirtualKeyShort.NUMLOCK, VirtualKeyShort.DIVIDE,
        VirtualKeyShort.SNAPSHOT, VirtualKeyShort.LWIN, VirtualKeyShort.RWIN, VirtualKeyShort.APPS];
    internal static VirtualKeyShort KeyFor(string k) => k.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => VirtualKeyShort.CONTROL, "ALT" or "MENU" => VirtualKeyShort.ALT, "SHIFT" => VirtualKeyShort.SHIFT,
        "WIN" or "LWIN" or "META" or "SUPER" => VirtualKeyShort.LWIN, "RWIN" => VirtualKeyShort.RWIN, "RCTRL" or "RCONTROL" => VirtualKeyShort.RCONTROL, "RALT" or "ALTGR" or "RMENU" => VirtualKeyShort.RMENU,
        "ENTER" or "RETURN" => VirtualKeyShort.RETURN, "ESC" or "ESCAPE" => VirtualKeyShort.ESCAPE, "TAB" => VirtualKeyShort.TAB, "SPACE" => VirtualKeyShort.SPACE,
        "BACKSPACE" or "BKSP" or "BACK" => VirtualKeyShort.BACK, "DEL" or "DELETE" => VirtualKeyShort.DELETE, "INS" or "INSERT" => VirtualKeyShort.INSERT,
        "PGUP" or "PAGEUP" or "PRIOR" => VirtualKeyShort.PRIOR, "PGDN" or "PAGEDOWN" or "NEXT" => VirtualKeyShort.NEXT,
        "UP" => VirtualKeyShort.UP, "DOWN" => VirtualKeyShort.DOWN, "LEFT" => VirtualKeyShort.LEFT, "RIGHT" => VirtualKeyShort.RIGHT, "HOME" => VirtualKeyShort.HOME, "END" => VirtualKeyShort.END,
        "APPS" or "CONTEXT" or "MENUKEY" => VirtualKeyShort.APPS, "CAPSLOCK" => VirtualKeyShort.CAPITAL, "NUMLOCK" => VirtualKeyShort.NUMLOCK, "SCROLLLOCK" => VirtualKeyShort.SCROLL,
        "PRINTSCREEN" or "PRTSC" => VirtualKeyShort.SNAPSHOT, "PAUSE" => VirtualKeyShort.PAUSE,
        var key => Enum.TryParse<VirtualKeyShort>(key.Length == 1 && char.IsLetterOrDigit(key[0]) ? "KEY_" + key : key, out var parsed) ? parsed : throw new WinixException("invalid_key", k)
    };
    /// <summary>Parses <c>CTRL+S</c>, sequences separated by spaces or commas (<c>CTRL+O DOWN*3 ENTER</c>), and <c>*N</c> repeats.</summary>
    internal static List<VirtualKeyShort[]> ParseKeys(string spec)
    {
        List<VirtualKeyShort[]> chords = [];
        foreach (var token in spec.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var text = token; var repeat = 1; var star = text.LastIndexOf('*');
            if (star > 0 && int.TryParse(text[(star + 1)..], out var n)) { repeat = Math.Clamp(n, 1, 100); text = text[..star]; }
            var keys = text.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(KeyFor).ToArray();
            if (keys.Length == 0) throw new WinixException("invalid_key", token);
            for (var i = 0; i < repeat; i++) chords.Add(keys);
        }
        return chords;
    }
    private static void SendKeys(string spec, int delayMs)
    {
        var chords = ParseKeys(spec);
        if (chords.Count == 0) throw new WinixException("invalid_args", "Provide keys, e.g. CTRL+S or DOWN*3 ENTER.");
        foreach (var chord in chords)
        {
            List<INPUT> inputs = [];
            foreach (var key in chord) inputs.Add(Key(key, false));
            foreach (var key in chord.Reverse()) inputs.Add(Key(key, true));
            Send(inputs);
            Thread.Sleep(Math.Clamp(delayMs, 0, 1000));
        }
    }
    private static void SendText(string text)
    {
        if (text.Length == 0) return;
        List<INPUT> inputs = [];
        foreach (var c in text)
        {
            inputs.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = 0x4 } } });
            inputs.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = 0x4 | 0x2 } } });
        }
        Send(inputs);
    }
    private static INPUT Key(VirtualKeyShort key, bool up)
    {
        var vk = (ushort)key; var scan = (ushort)MapVirtualKey(vk, 0);
        uint flags = scan == 0 ? 0u : 0x8; // KEYEVENTF_SCANCODE when the key has one
        if (ExtendedKeys.Contains(key)) flags |= 0x1;
        if (up) flags |= 0x2;
        return new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } };
    }
    private static void Send(List<INPUT> inputs)
    {
        var array = inputs.ToArray();
        var sent = SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());
        if (sent != array.Length) throw new WinixException("input_blocked", $"SendInput delivered {sent}/{array.Length} events (error {Marshal.GetLastWin32Error()}). An elevated or secure window may own the foreground.");
    }

    // ---- Summaries and trees ----
    private static long Handle(AutomationElement e) { try { return e.Properties.NativeWindowHandle.ValueOrDefault.ToInt64(); } catch (Exception ex) when (ex is COMException or InvalidOperationException) { return 0; } }
    private static object Summary(AutomationElement e)
    {
        var hwnd = Handle(e);
        string? value = null; try { if (e.Patterns.Value.IsSupported) value = e.Patterns.Value.Pattern.Value.ValueOrDefault; } catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        return new { name = e.Properties.Name.ValueOrDefault, automationId = e.Properties.AutomationId.ValueOrDefault,
            controlType = e.Properties.ControlType.ValueOrDefault.ToString(), className = e.Properties.ClassName.ValueOrDefault, pid = e.Properties.ProcessId.ValueOrDefault,
            hwnd = hwnd == 0 ? (long?)null : hwnd, enabled = e.Properties.IsEnabled.ValueOrDefault, value,
            bounds = new { x = e.BoundingRectangle.X, y = e.BoundingRectangle.Y, width = e.BoundingRectangle.Width, height = e.BoundingRectangle.Height } };
    }
    /// <summary>Walks with the control-view tree walker (no cached subtree request, which fails once owned windows exist) and stops at other top-level windows of the process.</summary>
    private static object Tree(AutomationElement e, ITreeWalker walker, HashSet<long> otherWindows, int depth, int maxDepth, ref int remaining)
    {
        remaining--; List<object> children = [];
        var handle = Handle(e);
        var separateWindow = depth > 0 && handle != 0 && otherWindows.Contains(handle);
        if (!separateWindow && depth < maxDepth && remaining > 0)
        {
            AutomationElement? child;
            try { child = walker.GetFirstChild(e); } catch (COMException) { child = null; }
            while (child is not null && remaining > 0)
            {
                children.Add(Tree(child, walker, otherWindows, depth + 1, maxDepth, ref remaining));
                try { child = walker.GetNextSibling(child); } catch (COMException) { child = null; }
            }
        }
        return new Dictionary<string, object?> { ["element"] = Summary(e), ["children"] = children, ["truncated"] = remaining <= 0, ["separateWindow"] = separateWindow ? true : null };
    }
    private static string WindowText(IntPtr handle) { var buffer = new StringBuilder(512); GetWindowText(handle, buffer, buffer.Capacity); return buffer.ToString(); }
    private static string ClassName(IntPtr handle) { var buffer = new StringBuilder(256); GetClassName(handle, buffer, buffer.Capacity); return buffer.ToString(); }
    public void Dispose() { queue.CompleteAdding(); thread.Join(1000); }

    // ---- Win32 ----
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MONITORINFOEX { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public HARDWAREINPUT hi; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool SwitchDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool flag);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] private class ApplicationActivationManager { }
    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
        [PreserveSig] int ActivateForFile(IntPtr a, IntPtr b, IntPtr c, out uint processId);
        [PreserveSig] int ActivateForProtocol(IntPtr a, IntPtr b, out uint processId);
    }
}
