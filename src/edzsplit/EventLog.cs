using System.Text;
using System.Text.Json;

namespace EdzSplit;

/// <summary>JSONL event log — one JSON object per line, written atomically enough for resume.</summary>
internal sealed class EventLog
{
    private readonly string? _path;
    private readonly StreamWriter? _writer;

    public EventLog(string? path)
    {
        _path = path;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
    }

    public void Write(string type, object data)
    {
        var record = new Dictionary<string, object?>
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
            ["type"] = type,
            ["data"] = data
        };
        var line = JsonSerializer.Serialize(record);
        Console.WriteLine(line);
        _writer?.WriteLine(line);
    }
}
