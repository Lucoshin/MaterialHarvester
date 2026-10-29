namespace MaterialHarvester.Cli;

public sealed class ContractException : Exception
{
    public ContractException(string message) : base(message) { }
}

public static class RequestValidator
{
    private static readonly HashSet<string> SceneEngines = new(StringComparer.Ordinal)
    {
        "content", "adaptive"
    };

    private static readonly HashSet<string> FixedSelections = new(StringComparer.Ordinal)
    {
        "automatic", "startOnly", "startAndEnd", "startMiddleAndEnd"
    };

    public static void Validate(JobRequest request, string command)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(command is "split" or "compose", "命令只支持 split 或 compose。");
        Require(request.SchemaVersion == 1, "schemaVersion 只支持 1。");
        Require(request.Operation == command, "命令与请求 operation 不一致。");
        RequireText(request.JobId, "jobId");
        RequireText(request.OutputDirectory, "outputDirectory");
        Require(request.SubtitleRemoval is not null && !request.SubtitleRemoval.Enabled,
            "当前版本未启用去字幕，请将 subtitleRemoval.enabled 设为 false。");

        if (command == "split") ValidateSplit(request);
        else ValidateComposition(request);
    }

    private static void ValidateSplit(JobRequest request)
    {
        RequireFileList(request.Inputs, "inputs");
        Require(request.OutputRule == "perVideoSubfolder",
            "当前 split 只支持 perVideoSubfolder 输出规则。");
        Require(request.Split is not null, "split 配置不能为空。");
        Require(request.Encoding is not null, "encoding 配置不能为空。");

        SplitSettings split = request.Split!;
        if (split.Mode == "smartScene")
        {
            Require(SceneEngines.Contains(split.SceneEngine), "sceneEngine 只支持 content 或 adaptive。");
            RequireRange(split.Sensitivity, 0.01, 1, "sensitivity");
            RequireRange(split.MinimumSceneDurationSeconds, 0.1, 600, "minimumSceneDurationSeconds");
        }
        else if (split.Mode == "fixedDuration")
        {
            RequireRange(split.DurationSeconds, 1, 600, "durationSeconds");
            Require(FixedSelections.Contains(split.Selection), "固定时长取片模式无效。");
        }
        else
        {
            throw new ContractException("split.mode 只支持 smartScene 或 fixedDuration。");
        }

        EncodingSettings encoding = request.Encoding!;
        Require(encoding.Mode == "auto", "encoding.mode 只支持 auto。");
        RequireIntegerRange(encoding.MaxThreads, 1, 16, "maxThreads");
    }

    private static void ValidateComposition(JobRequest request)
    {
        RequireFileList(request.Clips, "clips");
        Require(request.Composition is not null, "composition 配置不能为空。");
        CompositionSettings composition = request.Composition!;
        RequireIntegerRange(composition.OutputCount, 1, 100, "outputCount");
        RequireIntegerRange(composition.ClipsPerOutput, 1, 100, "clipsPerOutput");
        RequireIntegerRange(composition.MaxParallelism, 1, 8, "maxParallelism");
        int distinctClips = request.Clips.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Require(distinctClips >= composition.ClipsPerOutput,
            "去重后的 clips 数量不能少于 clipsPerOutput。");
    }

    private static void RequireFileList(IEnumerable<string>? values, string field)
    {
        Require(values is not null && values.Any(), $"{field} 不能为空。");
        foreach (string value in values!) RequireText(value, field);
    }

    private static void RequireText(string? value, string field) =>
        Require(!string.IsNullOrWhiteSpace(value), $"{field} 不能为空。");

    private static void RequireRange(double? value, double minimum, double maximum, string field) =>
        Require(value.HasValue && double.IsFinite(value.Value)
            && value.Value >= minimum && value.Value <= maximum,
            $"{field} 必须在 {minimum} 到 {maximum} 之间。");

    private static void RequireIntegerRange(int value, int minimum, int maximum, string field) =>
        Require(value >= minimum && value <= maximum,
            $"{field} 必须在 {minimum} 到 {maximum} 之间。");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ContractException(message);
    }
}
