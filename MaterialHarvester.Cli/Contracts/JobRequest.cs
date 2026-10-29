using System.Text.Json;

namespace MaterialHarvester.Cli;

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static JsonSerializerOptions CompactOptions { get; } = new(Options)
    {
        WriteIndented = false
    };
}

public sealed class JobRequest
{
    public int SchemaVersion { get; set; }
    public string Operation { get; set; } = "";
    public string JobId { get; set; } = "";
    public List<string> Inputs { get; set; } = [];
    public List<string> Clips { get; set; } = [];
    public string OutputDirectory { get; set; } = "";
    public string OutputRule { get; set; } = "";
    public SplitSettings? Split { get; set; }
    public EncodingSettings? Encoding { get; set; }
    public CompositionSettings? Composition { get; set; }
    public SubtitleRemovalSettings SubtitleRemoval { get; set; } = new();
}

public sealed class SplitSettings
{
    public string Mode { get; set; } = "";
    public string SceneEngine { get; set; } = "";
    public double? Sensitivity { get; set; }
    public double? MinimumSceneDurationSeconds { get; set; }
    public double? DurationSeconds { get; set; }
    public string Selection { get; set; } = "";
}

public sealed class EncodingSettings
{
    public string Mode { get; set; } = "";
    public bool Mute { get; set; }
    public int MaxThreads { get; set; }
}

public sealed class CompositionSettings
{
    public int OutputCount { get; set; }
    public int ClipsPerOutput { get; set; }
    public int MaxParallelism { get; set; }
    public bool MuteAudio { get; set; }
}

public sealed class SubtitleRemovalSettings
{
    public bool Enabled { get; set; }
}
