using System.Buffers;
using System.Text;

namespace Winix;

public static class Utf8Logs
{
    public static LogPage Read(string path, long offset, bool terminal, int pageSize = 65536)
    {
        if (offset < 0) throw new WinixException("invalid_offset", "Offset must be nonnegative.");
        if (!File.Exists(path)) return new("", 0, terminal);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset > file.Length) throw new WinixException("invalid_offset", "Offset exceeds log length.");
        file.Position = offset; var buffer = new byte[Math.Max(4, pageSize)]; var length = file.Read(buffer);
        var count = 0;
        while (count < length)
        {
            var status = Rune.DecodeFromUtf8(buffer.AsSpan(count, length - count), out _, out var used);
            if (status == OperationStatus.NeedMoreData) break;
            count += Math.Max(1, used);
        }
        return new(Encoding.UTF8.GetString(buffer, 0, count), offset + count, terminal && offset + count == file.Length);
    }
}
