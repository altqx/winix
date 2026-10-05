using System.Text.Json;

namespace Winix;

/// <summary>
/// Pointer input injected from the host through QEMU's USB tablet (QMP <c>input-send-event</c>).
/// It reaches Windows as hardware input: it works over the lock screen and UAC and cannot be blocked by a hung app.
/// Pixel coordinates are mapped to the tablet's 0..32767 range over a chosen screen rectangle.
/// </summary>
public sealed class HostInput(StateStore store, ICommandRunner runner, Func<string, GuestConnection> guest)
{
    public record Rect(int X, int Y, int Width, int Height);
    public record Point(int X, int Y)
    {
        public static Point Parse(string text)
        {
            var parts = text.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)) throw new WinixException("usage", $"Expected x,y but got '{text}'.");
            return new(x, y);
        }
        public static List<Point> ParsePath(string text) => text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Parse).ToList();
    }
    private static readonly string[] Buttons = ["left", "middle", "right", "none"];

    /// <summary>The rectangle pixels are mapped over: <c>virtual</c> (whole desktop), <c>primary</c>, <c>monitor:N</c>, or an explicit <c>WxH[+X+Y]</c>.</summary>
    public async Task<Rect> Geometry(string vm, string map, CancellationToken ct)
    {
        var explicitMatch = System.Text.RegularExpressions.Regex.Match(map, "^(\\d+)x(\\d+)(?:\\+(-?\\d+)\\+(-?\\d+))?$");
        if (explicitMatch.Success)
            return new(explicitMatch.Groups[3].Success ? int.Parse(explicitMatch.Groups[3].Value) : 0, explicitMatch.Groups[4].Success ? int.Parse(explicitMatch.Groups[4].Value) : 0,
                int.Parse(explicitMatch.Groups[1].Value), int.Parse(explicitMatch.Groups[2].Value));
        var monitors = await Monitors(vm, ct);
        if (monitors is { } m)
        {
            if (map == "virtual") return Read(m.GetProperty("virtualScreen"));
            var list = m.GetProperty("monitors").EnumerateArray().ToList();
            if (map == "primary") return Read(list.First(x => x.GetProperty("primary").GetBoolean()).GetProperty("bounds"));
            if (map.StartsWith("monitor:") && int.TryParse(map[8..], out var index) && index >= 0 && index < list.Count) return Read(list[index].GetProperty("bounds"));
            throw new WinixException("usage", $"Unknown mapping '{map}'. Use virtual, primary, monitor:N or WxH+X+Y.");
        }
        if (map != "virtual") throw new WinixException("geometry_unavailable", "The guest worker is unreachable; only --map virtual (host screenshot size) or an explicit WxH is available.");
        return await ScreenshotSize(vm, ct);
    }
    private static Rect Read(JsonElement bounds) => new(bounds.GetProperty("x").GetInt32(), bounds.GetProperty("y").GetInt32(), bounds.GetProperty("width").GetInt32(), bounds.GetProperty("height").GetInt32());
    private async Task<JsonElement?> Monitors(string vm, CancellationToken ct)
    {
        var cache = Path.Combine(store.Dir("vms", vm), "screen.json");
        try
        {
            var monitors = await guest(vm).Call("ui.monitors", new { }, ct);
            StateStore.Atomic(cache, monitors.GetRawText());
            return monitors;
        }
        catch (WinixException) when (File.Exists(cache)) { return Wire.Decode<JsonElement>(File.ReadAllText(cache)); }
        catch (WinixException) { return null; }
    }
    /// <summary>Falls back to the size of a libvirt screenshot (PPM header) when the guest worker cannot answer.</summary>
    private async Task<Rect> ScreenshotSize(string vm, CancellationToken ct)
    {
        var file = Path.Combine(store.Root, "screen-" + Guid.NewGuid().ToString("N") + ".ppm");
        try
        {
            await runner.Checked("virsh", ["-c", VmManager.Connection, "screenshot", "winix-" + Paths.Id(vm), file], ct: ct);
            using var stream = File.OpenRead(file); var header = new byte[64]; var length = stream.Read(header);
            var fields = System.Text.Encoding.ASCII.GetString(header, 0, length).Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || fields[0] != "P6") throw new WinixException("geometry_unavailable", "Unexpected screenshot format.");
            return new(0, 0, int.Parse(fields[1]), int.Parse(fields[2]));
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
    public async Task<object> Pointer(string vm, List<Point> path, string button, bool hold, bool release, int steps, int delayMs, string map, CancellationToken ct)
    {
        if (!Buttons.Contains(button)) throw new WinixException("usage", "Button must be left, middle, right or none.");
        steps = Math.Clamp(steps, 1, 500); delayMs = Math.Clamp(delayMs, 0, 1000);
        if (path.Count == 0)
        {
            if (!release) throw new WinixException("usage", "Provide a path (x1,y1;x2,y2) or --release.");
            await Button(vm, button, false, ct); return new { vm, released = button };
        }
        var rect = await Geometry(vm, map, ct);
        var pressed = false; var completed = false;
        try
        {
            if (release) await Button(vm, button, false, ct);
            await Move(vm, rect, path[0], ct);
            if (button != "none") { await Button(vm, button, true, ct); pressed = true; }
            for (var i = 1; i < path.Count; i++)
                for (var step = 1; step <= steps; step++)
                {
                    ct.ThrowIfCancellationRequested();
                    var x = path[i - 1].X + (path[i].X - path[i - 1].X) * step / steps;
                    var y = path[i - 1].Y + (path[i].Y - path[i - 1].Y) * step / steps;
                    await Move(vm, rect, new(x, y), ct);
                    if (delayMs > 0) await Task.Delay(delayMs, CancellationToken.None);
                }
            completed = true;
        }
        finally
        {
            // A failed step must never leave a button held; only an explicit, completed --hold keeps it down.
            if (pressed && !(hold && completed)) await Button(vm, button, false, CancellationToken.None);
        }
        return new { vm, points = path.Count, button, held = hold && button != "none", map = rect, end = path[^1] };
    }
    public Task<object> Drag(string vm, Point from, Point to, string button, int steps, int delayMs, string map, CancellationToken ct)
        => Pointer(vm, [from, to], button, false, false, steps, delayMs, map, ct);
    /// <summary>Moves the tablet to a point and reports where the guest cursor landed, so a mapping can be checked per monitor.</summary>
    public async Task<object> Calibrate(string vm, Point target, string map, CancellationToken ct)
    {
        var rect = await Geometry(vm, map, ct);
        await Move(vm, rect, target, ct);
        await Task.Delay(150, ct);
        var state = await guest(vm).Call("ui.state", new { }, ct);
        var cursor = state.GetProperty("cursor");
        var actual = new Point(cursor.GetProperty("x").GetInt32(), cursor.GetProperty("y").GetInt32());
        return new { vm, map = rect, expected = target, actual, offset = new Point(actual.X - target.X, actual.Y - target.Y) };
    }
    private async Task Move(string vm, Rect rect, Point point, CancellationToken ct)
    {
        static int Scale(int value, int origin, int extent) => Math.Clamp((int)Math.Round((value - origin) * 32767.0 / Math.Max(1, extent - 1)), 0, 32767);
        await Qmp(vm, new { execute = "input-send-event", arguments = new { events = new object[] {
            new { type = "abs", data = new { axis = "x", value = Scale(point.X, rect.X, rect.Width) } },
            new { type = "abs", data = new { axis = "y", value = Scale(point.Y, rect.Y, rect.Height) } } } } }, ct);
    }
    private Task Button(string vm, string button, bool down, CancellationToken ct) => button == "none" ? Task.CompletedTask
        : Qmp(vm, new { execute = "input-send-event", arguments = new { events = new object[] { new { type = "btn", data = new { down, button } } } } }, ct);
    public async Task<JsonElement> Qmp(string vm, object command, CancellationToken ct = default)
    {
        var result = await runner.Run("virsh", ["-c", VmManager.Connection, "qemu-monitor-command", "winix-" + Paths.Id(vm), Wire.Encode(command)], cancellationToken: ct);
        if (result.ExitCode != 0) throw new WinixException("qmp_error", result.Stderr.Trim());
        var reply = Wire.Decode<JsonElement>(result.Stdout);
        if (reply.TryGetProperty("error", out var error)) throw new WinixException("qmp_error", error.TryGetProperty("desc", out var desc) ? desc.GetString()! : error.GetRawText());
        return reply.TryGetProperty("return", out var value) ? value.Clone() : default;
    }
}
