using VideoToMaterial;

namespace MaterialHarvester.Cli;

public sealed class VideoJobExecutor : IJobExecutor
{
    private readonly string? _configuredFfmpegPath;

    public VideoJobExecutor() { }

    public VideoJobExecutor(string ffmpegPath)
    {
        _configuredFfmpegPath = Path.GetFullPath(ffmpegPath);
    }

    public async Task<JobResult> ExecuteAsync(
        JobRequest request,
        IJobEventSink events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);
        string ffmpegPath = _configuredFfmpegPath ?? ExecutableLocator.ResolveFfmpeg();
        string ffprobePath = ExecutableLocator.ResolveFfprobe(ffmpegPath);

        events.Write(new JobEvent
        {
            Type = "stage",
            Stage = "encoder-detection",
            Message = "正在检测可用编码器"
        });
        EncoderProfile profile = await new FFmpegHelper(ffmpegPath).DetectBestEncoderAsync();
        cancellationToken.ThrowIfCancellationRequested();
        events.Write(new JobEvent
        {
            Type = "log",
            Stage = "encoder-ready",
            Message = $"使用编码器：{profile.Name}"
        });

        return request.Operation == "split"
            ? await ExecuteSplitAsync(request, events, profile, ffmpegPath, cancellationToken)
            : await ExecuteCompositionAsync(
                request, events, profile, ffmpegPath, ffprobePath, cancellationToken);
    }

    private static async Task<JobResult> ExecuteSplitAsync(
        JobRequest request,
        IJobEventSink events,
        EncoderProfile profile,
        string ffmpegPath,
        CancellationToken cancellationToken)
    {
        var result = new JobResult { JobId = request.JobId };
        int completed = 0;

        foreach (string input in request.Inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = new JobItemResult { Input = input };
            result.Items.Add(item);
            try
            {
                if (!File.Exists(input))
                    throw new FileNotFoundException("输入视频不存在。", input);

                string outputDirectory = CreateUniqueSplitDirectory(request.OutputDirectory, input);
                var processor = new VideoProcessorService(ffmpegPath);
                processor.OnLog += message => events.Write(new JobEvent
                {
                    Type = "log",
                    Stage = "split",
                    Input = input,
                    Message = message
                });
                processor.OnProgress += (current, total) => events.Write(new JobEvent
                {
                    Type = "progress",
                    Stage = "split",
                    Input = input,
                    Current = current,
                    Total = total,
                    Percent = total > 0 ? current * 100d / total : 0
                });

                await processor.ProcessSingleVideoAsync(
                    input,
                    outputDirectory,
                    BuildProcessingOptions(request, profile),
                    cancellationToken);

                item.Outputs = Directory.EnumerateFiles(outputDirectory, "*.mp4", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (item.Outputs.Count == 0)
                    throw new InvalidOperationException("视频引擎没有生成任何可用片段。");
                item.Status = "succeeded";
            }
            catch (OperationCanceledException)
            {
                item.Status = "cancelled";
                throw;
            }
            catch (Exception error)
            {
                item.Status = "failed";
                item.Error = error.Message;
            }

            completed++;
            events.Write(new JobEvent
            {
                Type = "item-completed",
                Stage = "split",
                Input = input,
                Current = completed,
                Total = request.Inputs.Count
            });
        }

        result.Status = AggregateStatus(result.Items);
        if (result.Status == "failed")
            result.Error = "所有输入视频均处理失败。";
        return result;
    }

    private static ProcessingOptions BuildProcessingOptions(JobRequest request, EncoderProfile profile)
    {
        SplitSettings split = request.Split!;
        EncodingSettings encoding = request.Encoding!;
        return new ProcessingOptions
        {
            SplitMode = split.Mode == "fixedDuration" ? SplitMode.FixedDuration : SplitMode.SmartScene,
            SceneDetectionEngine = split.SceneEngine == "adaptive"
                ? SceneDetectionEngine.AdaptiveDetector
                : SceneDetectionEngine.ContentDetector,
            FixedDurationSeconds = split.DurationSeconds ?? 10,
            FixedClipSelectionMode = split.Selection switch
            {
                "startOnly" => FixedClipSelectionMode.StartOnly,
                "startAndEnd" => FixedClipSelectionMode.StartAndEnd,
                "startMiddleAndEnd" => FixedClipSelectionMode.StartMiddleAndEnd,
                _ => FixedClipSelectionMode.Automatic
            },
            MinimumSceneDurationSeconds = split.MinimumSceneDurationSeconds ?? 10,
            Sensitivity = split.Sensitivity ?? 0.75,
            Mute = encoding.Mute,
            MaxThreads = encoding.MaxThreads,
            Profile = profile,
            SubtitleRemoval = new SubtitleRemovalOptions { Enabled = false }
        };
    }

    private static async Task<JobResult> ExecuteCompositionAsync(
        JobRequest request,
        IJobEventSink events,
        EncoderProfile profile,
        string ffmpegPath,
        string ffprobePath,
        CancellationToken cancellationToken)
    {
        string[] missingClips = request.Clips.Where(path => !File.Exists(path)).ToArray();
        if (missingClips.Length > 0)
            throw new FileNotFoundException("混剪素材不存在：" + string.Join("；", missingClips));

        Directory.CreateDirectory(request.OutputDirectory);
        CompositionSettings settings = request.Composition!;
        var options = new CompositionOptions
        {
            OutputCount = settings.OutputCount,
            ClipsPerOutput = settings.ClipsPerOutput,
            MaxParallelism = settings.MaxParallelism,
            MuteAudio = settings.MuteAudio,
            OutputDirectory = request.OutputDirectory,
            FileNamePrefix = "Mix_" + SafeFileSegment(request.JobId),
            EncoderProfile = profile
        };
        CompositionPlan plan = new RandomCompositionPlanner().CreatePlan(request.Clips, options);
        var composer = new VideoComposerService(ffmpegPath, ffprobePath);
        composer.OnLog += message => events.Write(new JobEvent
        {
            Type = "log",
            Stage = "compose",
            Message = message
        });
        composer.OnProgress += (current, total) => events.Write(new JobEvent
        {
            Type = "progress",
            Stage = "compose",
            Current = current,
            Total = total,
            Percent = total > 0 ? current * 100d / total : 0
        });
        composer.OnStageProgress += (message, percent) => events.Write(new JobEvent
        {
            Type = "stage-progress",
            Stage = "compose",
            Message = message,
            Percent = percent
        });

        events.Write(new JobEvent
        {
            Type = "stage",
            Stage = "validation",
            Message = "正在完整解码预检混剪素材"
        });
        ClipValidationResult validation = await composer.ValidateClipsAsync(
            request.Clips,
            Math.Min(settings.MaxParallelism, 2),
            validateAudio: !settings.MuteAudio,
            cancellationToken);
        if (validation.InvalidClips.Count > 0)
        {
            throw new InvalidDataException(
                "以下混剪素材无法完整解码：" + string.Join("；", validation.InvalidClips));
        }

        await composer.ComposeAsync(plan, options, cancellationToken);
        var result = new JobResult
        {
            JobId = request.JobId,
            Items = plan.Jobs.Select(job => new JobItemResult
            {
                Input = $"composition:{job.Index}",
                Status = job.Status switch
                {
                    "Completed" => "succeeded",
                    "Cancelled" => "cancelled",
                    _ => "failed"
                },
                Outputs = job.Status == "Completed" && File.Exists(job.OutputPath)
                    ? [job.OutputPath]
                    : [],
                Error = string.IsNullOrWhiteSpace(job.Error) ? null : job.Error
            }).ToList()
        };
        result.Status = AggregateStatus(result.Items);
        if (result.Status == "failed") result.Error = "所有混剪任务均失败。";
        return result;
    }

    private static string CreateUniqueSplitDirectory(string outputRoot, string input)
    {
        Directory.CreateDirectory(outputRoot);
        string name = SafeFileSegment(Path.GetFileNameWithoutExtension(input));
        string basePath = Path.Combine(outputRoot, name + "_Scenes");
        string candidate = basePath;
        for (int suffix = 2; Directory.Exists(candidate); suffix++)
        {
            candidate = basePath + "_" + suffix;
        }
        Directory.CreateDirectory(candidate);
        return candidate;
    }

    private static string SafeFileSegment(string value)
    {
        string safe = string.IsNullOrWhiteSpace(value) ? "Video" : value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars()) safe = safe.Replace(invalid, '_');
        if (safe.Length > 80) safe = safe[..80];
        return safe.Length == 0 ? "Video" : safe;
    }

    private static string AggregateStatus(IReadOnlyCollection<JobItemResult> items)
    {
        int succeeded = items.Count(item => item.Status == "succeeded");
        if (succeeded == items.Count) return "succeeded";
        return succeeded > 0 ? "partial" : "failed";
    }
}
