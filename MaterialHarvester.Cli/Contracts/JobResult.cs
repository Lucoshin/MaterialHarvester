namespace MaterialHarvester.Cli;

public sealed class JobResult
{
    public int SchemaVersion { get; set; } = 1;
    public string JobId { get; set; } = "";
    public string Status { get; set; } = "";
    public List<JobItemResult> Items { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class JobItemResult
{
    public string Input { get; set; } = "";
    public string Status { get; set; } = "";
    public List<string> Outputs { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class JobEvent
{
    public string Type { get; set; } = "";
    public string? Stage { get; set; }
    public string? Message { get; set; }
    public string? Input { get; set; }
    public int? Current { get; set; }
    public int? Total { get; set; }
    public double? Percent { get; set; }
}
