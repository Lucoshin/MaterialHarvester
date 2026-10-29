using System.Text;
using System.Text.Json;

namespace MaterialHarvester.Cli;

public interface IJobEventSink
{
    void Write(JobEvent jobEvent);
}

public sealed class JsonLineEventWriter : IJobEventSink
{
    private readonly string _path;
    private readonly object _writeLock = new();

    public JsonLineEventWriter(string path)
    {
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "", new UTF8Encoding(false));
    }

    public void Write(JobEvent jobEvent)
    {
        ArgumentNullException.ThrowIfNull(jobEvent);
        string line = JsonSerializer.Serialize(jobEvent, JsonDefaults.CompactOptions) + Environment.NewLine;
        lock (_writeLock)
        {
            File.AppendAllText(_path, line, new UTF8Encoding(false));
        }
    }
}
