using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VideoToMaterial
{
    public sealed class CompositionRunResult
    {
        public int Succeeded { get; }
        public int Failed { get; }

        public CompositionRunResult(int succeeded, int failed)
        {
            Succeeded = succeeded;
            Failed = failed;
        }
    }

    public sealed class ClipValidationResult
    {
        public IReadOnlyList<string> ValidClips { get; }
        public IReadOnlyList<string> InvalidClips { get; }
        public IReadOnlyDictionary<string, string> Errors { get; }

        public ClipValidationResult(
            IEnumerable<string> validClips,
            IEnumerable<string> invalidClips,
            IDictionary<string, string> errors)
        {
            ValidClips = (validClips ?? Array.Empty<string>()).ToArray();
            InvalidClips = (invalidClips ?? Array.Empty<string>()).ToArray();
            Errors = new Dictionary<string, string>(
                errors ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public static class CompositionManifestService
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public static void Save(string path, CompositionPlan plan, CompositionOptions options)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Manifest path is required.", nameof(path));
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (options == null) throw new ArgumentNullException(nameof(options));

            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            string temporaryPath = path + ".tmp";
            var document = new
            {
                Version = 1,
                CreatedAtUtc = DateTime.UtcNow,
                plan.Seed,
                Options = new
                {
                    options.OutputCount,
                    options.ClipsPerOutput,
                    options.FileNamePrefix,
                    options.MuteAudio,
                    options.MaxParallelism
                },
                UsageCounts = plan.UsageCounts,
                Jobs = plan.Jobs.Select(job => new
                {
                    job.Index,
                    job.OutputPath,
                    job.Clips,
                    job.Status,
                    job.Error
                })
            };

            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, path, true);
        }
    }

    public sealed class VideoComposerService
    {
        private const int MaxInputsPerComposePass = 8;

        private sealed class CompositionClipMediaInfo
        {
            public string Path { get; }
            public double VideoDurationSeconds { get; }
            public bool HasAudio { get; }

            public CompositionClipMediaInfo(string path, double videoDurationSeconds, bool hasAudio)
            {
                Path = path;
                VideoDurationSeconds = videoDurationSeconds;
                HasAudio = hasAudio;
            }
        }

        private readonly string _ffmpegPath;
        private readonly string _ffprobePath;
        private readonly object _manifestLock = new object();
        private readonly object _mediaInfoLock = new object();
        private readonly object _activeProcessLock = new object();
        private readonly HashSet<Process> _activeProcesses = new HashSet<Process>();
        private readonly Dictionary<string, CompositionClipMediaInfo> _mediaInfoCache =
            new Dictionary<string, CompositionClipMediaInfo>(StringComparer.OrdinalIgnoreCase);

        public event Action<string> OnLog;
        public event Action<int, int> OnProgress;
        public event Action<string, double> OnStageProgress;

        public VideoComposerService(string ffmpegPath, string ffprobePath = null)
        {
            _ffmpegPath = ffmpegPath ?? throw new ArgumentNullException(nameof(ffmpegPath));
            _ffprobePath = string.IsNullOrWhiteSpace(ffprobePath)
                ? GetFfprobePath(_ffmpegPath)
                : ffprobePath;
        }

        public void StopActiveProcesses()
        {
            Process[] processes;
            lock (_activeProcessLock) processes = _activeProcesses.ToArray();
            foreach (Process process in processes)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch { }
            }
        }

        public Task<ClipValidationResult> ValidateClipsAsync(
            IEnumerable<string> clipPaths,
            int maxParallelism,
            CancellationToken cancellationToken = default) =>
            ValidateClipsAsync(
                clipPaths,
                maxParallelism,
                validateAudio: false,
                cancellationToken: cancellationToken);

        public async Task<ClipValidationResult> ValidateClipsAsync(
            IEnumerable<string> clipPaths,
            int maxParallelism,
            bool validateAudio,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(_ffmpegPath)) throw new FileNotFoundException("ffmpeg.exe was not found.", _ffmpegPath);
            string[] clips = (clipPaths ?? throw new ArgumentNullException(nameof(clipPaths)))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var results = new (bool Valid, string Error)[clips.Length];
            int completed = 0;
            using var gate = new SemaphoreSlim(Math.Max(1, Math.Min(maxParallelism, 2)));
            var tasks = clips.Select(async (clip, index) =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    results[index] = await ValidateClipAsync(
                            _ffmpegPath,
                            clip,
                            validateAudio,
                            cancellationToken,
                            RegisterActiveProcess,
                            UnregisterActiveProcess)
                        .ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                    ReportProgressSafely(Interlocked.Increment(ref completed), clips.Length);
                }
            }).ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);
            string[] valid = clips.Where((_, index) => results[index].Valid).ToArray();
            string[] invalid = clips.Where((_, index) => !results[index].Valid).ToArray();
            var errors = invalid.ToDictionary(
                path => path,
                path => results[Array.IndexOf(clips, path)].Error ?? "无法完整解码。",
                StringComparer.OrdinalIgnoreCase);
            return new ClipValidationResult(valid, invalid, errors);
        }

        internal static Task<(bool Valid, string Error)> ValidateClipAsync(
            string ffmpegPath,
            string clipPath,
            CancellationToken cancellationToken = default) =>
            ValidateClipAsync(
                ffmpegPath,
                clipPath,
                validateAudio: false,
                cancellationToken: cancellationToken,
                processStarted: null,
                processFinished: null);

        private static async Task<(bool Valid, string Error)> ValidateClipAsync(
            string ffmpegPath,
            string clipPath,
            bool validateAudio,
            CancellationToken cancellationToken,
            Action<Process> processStarted,
            Action<Process> processFinished)
        {
            if (string.IsNullOrWhiteSpace(clipPath) || !File.Exists(clipPath))
                return (false, "素材文件不存在。");
            if (new FileInfo(clipPath).Length == 0)
                return (false, "素材文件为空。");

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            AddArguments(startInfo,
                "-hide_banner", "-loglevel", "error", "-xerror",
                "-threads", "1", "-i", clipPath,
                "-map", "0:v:0");
            if (validateAudio)
                AddArguments(startInfo, "-map", "0:a:0?");
            else
                startInfo.ArgumentList.Add("-an");
            AddArguments(startInfo,
                "-sn", "-dn", "-nostats", "-progress", "pipe:1",
                "-f", "null", "-");

            try
            {
                using Process process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("FFmpeg validation process did not start.");
                processStarted?.Invoke(process);
                try
                {
                    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                    try
                    {
                        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                        try { await process.WaitForExitAsync().ConfigureAwait(false); } catch { }
                        try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); } catch { }
                        throw;
                    }

                    string stdout = await stdoutTask.ConfigureAwait(false);
                    string stderr = await stderrTask.ConfigureAwait(false);
                    int decodedFrames = Regex.Matches(stdout, @"(?m)^frame=(?<count>\d+)\r?$")
                        .Cast<Match>()
                        .Select(match => int.Parse(match.Groups["count"].Value))
                        .DefaultIfEmpty(0)
                        .Max();
                    if (process.ExitCode == 0 && decodedFrames > 0) return (true, "");
                    if (process.ExitCode == 0)
                        return (false, "素材未解码出任何视频帧。");
                    string output = stdout + stderr;
                    return (false, string.IsNullOrWhiteSpace(output) ? $"FFmpeg 退出代码 {process.ExitCode}。" : output.Trim());
                }
                finally
                {
                    try { processFinished?.Invoke(process); } catch { }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private async Task CacheMediaInfoAsync(
            IEnumerable<string> clipPaths,
            int maxParallelism,
            CancellationToken cancellationToken,
            bool required = false)
        {
            string ffprobePath = _ffprobePath;
            if (!File.Exists(ffprobePath))
            {
                if (required)
                    throw new FileNotFoundException("ffprobe.exe was not found; audio-safe composition cannot start.", ffprobePath);
                return;
            }

            string[] clips = (clipPaths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (clips.Length == 0) return;

            var results = new CompositionClipMediaInfo[clips.Length];
            using var gate = new SemaphoreSlim(Math.Max(1, Math.Min(maxParallelism, 2)));
            Task[] tasks = clips.Select(async (clip, index) =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    results[index] = await ProbeClipMediaInfoAsync(ffprobePath, clip, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch when (!required)
                {
                    results[index] = null;
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);

            lock (_mediaInfoLock)
            {
                foreach (CompositionClipMediaInfo info in results.Where(info => info != null))
                    _mediaInfoCache[info.Path] = info;
            }
        }

        private async Task<CompositionClipMediaInfo> ProbeClipMediaInfoAsync(
            string ffprobePath,
            string clipPath,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            AddArguments(startInfo,
                "-v", "error",
                "-show_entries", "stream=codec_type,duration:format=duration",
                "-of", "json",
                clipPath);

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("ffprobe media inspection process did not start.");
            RegisterActiveProcess(process);
            try
            {
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                try
                {
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await StopProcessAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
                    throw;
                }

                string stdout = await stdoutTask.ConfigureAwait(false);
                string stderr = await stderrTask.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"无法读取素材媒体信息：{clipPath}。" +
                        (string.IsNullOrWhiteSpace(stderr) ? $"ffprobe 退出代码 {process.ExitCode}。" : stderr.Trim()));
                }

                try
                {
                    using JsonDocument document = JsonDocument.Parse(stdout);
                    bool hasVideo = false;
                    bool hasAudio = false;
                    double videoDuration = 0;
                    if (document.RootElement.TryGetProperty("streams", out JsonElement streams)
                        && streams.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement stream in streams.EnumerateArray())
                        {
                            string codecType = stream.TryGetProperty("codec_type", out JsonElement typeElement)
                                ? typeElement.GetString()
                                : "";
                            if (codecType == "video")
                            {
                                hasVideo = true;
                                if (videoDuration <= 0 && stream.TryGetProperty("duration", out JsonElement durationElement))
                                    TryReadPositiveDouble(durationElement, out videoDuration);
                            }
                            else if (codecType == "audio")
                            {
                                hasAudio = true;
                            }
                        }
                    }

                    if (videoDuration <= 0
                        && document.RootElement.TryGetProperty("format", out JsonElement format)
                        && format.TryGetProperty("duration", out JsonElement formatDuration))
                    {
                        TryReadPositiveDouble(formatDuration, out videoDuration);
                    }

                    if (!hasVideo || videoDuration <= 0)
                        throw new InvalidDataException("未找到可用的视频流时长。");
                    return new CompositionClipMediaInfo(clipPath, videoDuration, hasAudio);
                }
                catch (Exception ex) when (ex is JsonException || ex is InvalidDataException)
                {
                    throw new InvalidDataException($"素材媒体信息无效：{clipPath}。{ex.Message}", ex);
                }
            }
            finally
            {
                UnregisterActiveProcess(process);
            }
        }

        private static bool TryReadPositiveDouble(JsonElement element, out double value)
        {
            value = 0;
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double numeric))
                value = numeric;
            else if (element.ValueKind == JsonValueKind.String)
                double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            return value > 0 && !double.IsInfinity(value) && !double.IsNaN(value);
        }

        private static string GetFfprobePath(string ffmpegPath)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(ffmpegPath)) ?? Environment.CurrentDirectory;
            string extension = Path.GetExtension(ffmpegPath);
            return Path.Combine(directory, "ffprobe" + extension);
        }

        public async Task<CompositionRunResult> ComposeAsync(
            CompositionPlan plan,
            CompositionOptions options,
            CancellationToken cancellationToken = default)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (!File.Exists(_ffmpegPath)) throw new FileNotFoundException("ffmpeg.exe was not found.", _ffmpegPath);
            if (string.IsNullOrWhiteSpace(options.OutputDirectory))
                throw new ArgumentException("Output directory is required.", nameof(options));

            Directory.CreateDirectory(options.OutputDirectory);
            string tempDirectory = Path.Combine(options.OutputDirectory, ".material-harvester");
            Directory.CreateDirectory(tempDirectory);
            string manifestPath = Path.Combine(options.OutputDirectory, "composition-manifest.json");
            CompositionManifestService.Save(manifestPath, plan, options);

            string[] selectedClips = plan.Jobs
                .SelectMany(job => job.Clips)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            LogSafely(options.MuteAudio
                ? $"> 正在核对 {selectedClips.Length} 个素材的可靠时长，防止成片丢片或变速..."
                : $"> 正在检查 {selectedClips.Length} 个素材的音轨与时长，确保有声和无声片段同步拼接...");
            await CacheMediaInfoAsync(
                    selectedClips,
                    Math.Min(2, options.MaxParallelism),
                    cancellationToken,
                    required: true)
                .ConfigureAwait(false);
            Dictionary<string, CompositionClipMediaInfo> mediaInfo;
            lock (_mediaInfoLock)
            {
                mediaInfo = selectedClips
                    .Where(path => _mediaInfoCache.ContainsKey(path))
                    .ToDictionary(path => path, path => _mediaInfoCache[path], StringComparer.OrdinalIgnoreCase);
            }

            int completed = 0;
            int succeeded = 0;
            int failed = 0;
            int effectiveParallelism = GetEffectiveParallelism(plan, options);
            if (effectiveParallelism < options.MaxParallelism)
            {
                LogSafely($"> 单条包含大量素材，成片任务并发已自动限制为 {effectiveParallelism}，避免解码内存过载。");
            }
            using var gate = new SemaphoreSlim(effectiveParallelism);
            Task[] tasks = plan.Jobs.Select(ComposeJobAsync).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
            return new CompositionRunResult(succeeded, failed);

            async Task ComposeJobAsync(CompositionJob job)
            {
                bool enteredGate = false;
                string listPath = Path.Combine(tempDirectory, $"job-{job.Index:D5}.ffconcat");
                string partialOutputPath = CreatePartialOutputPath(job.OutputPath);
                var stagedChunkPaths = new List<string>();
                try
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    enteredGate = true;
                    cancellationToken.ThrowIfCancellationRequested();
                    TryDeleteFile(partialOutputPath);
                    job.Status = "Running";
                    job.Error = "";

                    IReadOnlyList<string> composeClips = job.Clips;
                    IReadOnlyDictionary<string, CompositionClipMediaInfo> composeMediaInfo = mediaInfo;
                    bool concatenateStagedChunks = false;
                    if (job.Clips.Count > MaxInputsPerComposePass)
                    {
                        LogSafely(
                            $"> {Path.GetFileName(job.OutputPath)} 含 {job.Clips.Count} 个片段，" +
                            $"正在按每组最多 {MaxInputsPerComposePass} 路分段编码，避免多路解码器同时过载...");
                        var stagedMediaInfo = new Dictionary<string, CompositionClipMediaInfo>(StringComparer.OrdinalIgnoreCase);
                        int chunkIndex = 0;
                        string[][] chunks = job.Clips.Chunk(MaxInputsPerComposePass).ToArray();
                        foreach (string[] chunkClips in chunks)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            chunkIndex++;
                            string chunkPath = Path.Combine(
                                tempDirectory,
                                $"job-{job.Index:D5}-chunk-{chunkIndex:D3}.mp4");
                            TryDeleteFile(chunkPath);
                            stagedChunkPaths.Add(chunkPath);

                            ReportStageProgressSafely(
                                $"{Path.GetFileName(job.OutputPath)}：正在编码第 {chunkIndex}/{chunks.Length} 组",
                                (chunkIndex - 1) * 100.0 / chunks.Length);
                            await RunComposePassWithFallbackAsync(
                                    chunkClips,
                                    listPath,
                                    chunkPath,
                                    options.MuteAudio,
                                    mediaInfo,
                                    options.EncoderProfile,
                                    cancellationToken,
                                    groupPercent => ReportStageProgressSafely(
                                        $"{Path.GetFileName(job.OutputPath)}：第 {chunkIndex}/{chunks.Length} 组 {groupPercent:0}%",
                                        ((chunkIndex - 1) + groupPercent / 100.0) * 100.0 / chunks.Length))
                                .ConfigureAwait(false);
                            ReportStageProgressSafely(
                                $"{Path.GetFileName(job.OutputPath)}：第 {chunkIndex}/{chunks.Length} 组完成",
                                chunkIndex * 100.0 / chunks.Length);

                            (bool chunkValid, string chunkValidationError) = await ValidateClipAsync(
                                    _ffmpegPath,
                                    chunkPath,
                                    validateAudio: !options.MuteAudio,
                                    cancellationToken: cancellationToken,
                                    processStarted: RegisterActiveProcess,
                                    processFinished: UnregisterActiveProcess)
                                .ConfigureAwait(false);
                            if (!chunkValid)
                            {
                                throw new InvalidOperationException(
                                    $"第 {chunkIndex} 个合成分组验收失败：{chunkValidationError}");
                            }

                            CompositionClipMediaInfo chunkInfo = await ProbeClipMediaInfoAsync(
                                    _ffprobePath,
                                    chunkPath,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            double[] sourceDurations = chunkClips
                                .Select(path => mediaInfo[path].VideoDurationSeconds)
                                .ToArray();
                            if (!IsOutputDurationAcceptable(sourceDurations, chunkInfo.VideoDurationSeconds))
                            {
                                throw new InvalidOperationException(
                                    $"第 {chunkIndex} 个合成分组时长异常：" +
                                    $"预计约 {sourceDurations.Sum():0.###} 秒，实际 {chunkInfo.VideoDurationSeconds:0.###} 秒。");
                            }
                            stagedMediaInfo[chunkPath] = chunkInfo;
                        }

                        composeClips = stagedChunkPaths;
                        composeMediaInfo = stagedMediaInfo;
                        concatenateStagedChunks = true;
                    }

                    await File.WriteAllTextAsync(
                            listPath,
                            CreateConcatListContent(composeClips),
                            new UTF8Encoding(false),
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (concatenateStagedChunks)
                    {
                        ReportStageProgressSafely(
                            $"{Path.GetFileName(job.OutputPath)}：正在无损合并分组并验收",
                            100);
                        ProcessStartInfo startInfo = CreateConcatCopyStartInfo(
                            _ffmpegPath,
                            listPath,
                            partialOutputPath,
                            options.MuteAudio);
                        await RunFfmpegAsync(startInfo, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        ReportStageProgressSafely(
                            $"{Path.GetFileName(job.OutputPath)}：正在编码",
                            0);
                        await RunComposePassWithFallbackAsync(
                                composeClips,
                                listPath,
                                partialOutputPath,
                                options.MuteAudio,
                                composeMediaInfo,
                                options.EncoderProfile,
                                cancellationToken,
                                percent => ReportStageProgressSafely(
                                    $"{Path.GetFileName(job.OutputPath)}：正在编码 {percent:0}%",
                                    percent))
                            .ConfigureAwait(false);
                    }

                    (bool valid, string validationError) = await ValidateClipAsync(
                            _ffmpegPath,
                            partialOutputPath,
                            validateAudio: !options.MuteAudio,
                            cancellationToken: cancellationToken,
                            processStarted: RegisterActiveProcess,
                            processFinished: UnregisterActiveProcess)
                        .ConfigureAwait(false);
                    if (!valid)
                    {
                        throw new InvalidOperationException(
                            "合成产物验收失败：" + (string.IsNullOrWhiteSpace(validationError)
                                ? "无法完整解码成片。"
                                : validationError));
                    }

                    CompositionClipMediaInfo outputInfo = await ProbeClipMediaInfoAsync(
                            _ffprobePath,
                            partialOutputPath,
                            cancellationToken)
                        .ConfigureAwait(false);
                    double expectedDuration = job.Clips.Sum(path => mediaInfo[path].VideoDurationSeconds);
                    if (!IsOutputDurationAcceptable(
                            job.Clips.Select(path => mediaInfo[path].VideoDurationSeconds).ToArray(),
                            outputInfo.VideoDurationSeconds))
                    {
                        throw new InvalidOperationException(
                            "合成产物验收失败：" +
                            $"预计时长约 {expectedDuration:0.###} 秒，实际为 {outputInfo.VideoDurationSeconds:0.###} 秒，" +
                            "可能存在丢片、截短或播放速度异常。");
                    }

                    File.Move(partialOutputPath, job.OutputPath, true);
                    job.Status = "Completed";
                    Interlocked.Increment(ref succeeded);
                    LogSafely($"> 合成完成 [{job.Index}/{plan.Jobs.Count}]：{Path.GetFileName(job.OutputPath)}");
                }
                catch (OperationCanceledException)
                {
                    job.Status = "Cancelled";
                    job.Error = "";
                    throw;
                }
                catch (Exception ex)
                {
                    job.Status = "Failed";
                    job.Error = ex.Message;
                    Interlocked.Increment(ref failed);
                    if (!ex.Message.StartsWith("合成产物验收失败：", StringComparison.Ordinal))
                    {
                        IReadOnlyList<string> failedClips = FindFailedClipPaths(
                            ex.Message,
                            job.Clips,
                            options.MuteAudio);
                        if (failedClips.Count > 0)
                        {
                            LogSafely(
                                $"[合成失败] {Path.GetFileName(job.OutputPath)}：检测到无法解码的素材：" +
                                string.Join("；", failedClips) +
                                "。完整 FFmpeg 错误已写入 composition-manifest.json。");
                        }
                        else
                        {
                            string summary = string.Join(
                                " | ",
                                ex.Message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(4));
                            LogSafely(
                                $"[合成失败] {Path.GetFileName(job.OutputPath)}：{summary}" +
                                "（完整错误已写入 composition-manifest.json）");
                        }
                    }
                    else
                    {
                        LogSafely(
                            $"[成片验收失败] {Path.GetFileName(job.OutputPath)}：输出无法完整解码，已自动清理，未生成坏片。" +
                            "完整错误已写入 composition-manifest.json。");
                    }
                }
                finally
                {
                    TryDeleteFile(listPath);
                    foreach (string chunkPath in stagedChunkPaths)
                    {
                        if (!TryDeleteFile(chunkPath))
                            LogSafely($"[临时文件清理失败] 请手动删除：{chunkPath}");
                    }
                    if (!TryDeleteFile(partialOutputPath))
                        LogSafely($"[临时文件清理失败] 请手动删除：{partialOutputPath}");
                    int current = Interlocked.Increment(ref completed);
                    if (enteredGate) gate.Release();
                    try
                    {
                        lock (_manifestLock) CompositionManifestService.Save(manifestPath, plan, options);
                    }
                    catch (Exception ex)
                    {
                        LogSafely($"[清单写入失败] {ex.Message}");
                    }
                    ReportProgressSafely(current, plan.Jobs.Count);
                }
            }
        }

        private static string CreatePartialOutputPath(string outputPath)
        {
            string fullPath = Path.GetFullPath(outputPath);
            string directory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
            string fileName = Path.GetFileNameWithoutExtension(fullPath);
            string extension = Path.GetExtension(fullPath);
            return Path.Combine(directory, $".{fileName}.partial{extension}");
        }

        private void RegisterActiveProcess(Process process)
        {
            lock (_activeProcessLock) _activeProcesses.Add(process);
        }

        private void UnregisterActiveProcess(Process process)
        {
            lock (_activeProcessLock) _activeProcesses.Remove(process);
        }

        private void LogSafely(string message)
        {
            try { OnLog?.Invoke(message); } catch { }
        }

        private void ReportProgressSafely(int completed, int total)
        {
            try { OnProgress?.Invoke(completed, total); } catch { }
        }

        private void ReportStageProgressSafely(string message, double percent)
        {
            try { OnStageProgress?.Invoke(message, Math.Clamp(percent, 0, 100)); } catch { }
        }

        private async Task RunComposePassWithFallbackAsync(
            IEnumerable<string> clips,
            string concatListPath,
            string outputPath,
            bool muteAudio,
            IReadOnlyDictionary<string, CompositionClipMediaInfo> mediaInfo,
            EncoderProfile preferredProfile,
            CancellationToken cancellationToken,
            Action<double> progress = null)
        {
            double expectedDuration = clips
                .Where(path => mediaInfo != null && mediaInfo.ContainsKey(path))
                .Sum(path => mediaInfo[path].VideoDurationSeconds);
            ProcessStartInfo preferred = CreateStartInfo(
                _ffmpegPath,
                clips,
                concatListPath,
                outputPath,
                muteAudio,
                mediaInfo,
                preferredProfile);
            try
            {
                await RunFfmpegAsync(preferred, cancellationToken, expectedDuration, progress).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (preferredProfile?.IsHardware == true)
            {
                TryDeleteFile(outputPath);
                string summary = ex.Message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? "硬件编码器启动失败";
                LogSafely($"[提示] {preferredProfile.Name} 编码失败（{summary}），自动改用 CPU 快速模式重试。");
                ProcessStartInfo fallback = CreateStartInfo(
                    _ffmpegPath,
                    clips,
                    concatListPath,
                    outputPath,
                    muteAudio,
                    mediaInfo,
                    CreateCpuFallbackProfile());
                await RunFfmpegAsync(fallback, cancellationToken, expectedDuration, progress).ConfigureAwait(false);
            }
        }

        private static EncoderProfile CreateCpuFallbackProfile() => new EncoderProfile
        {
            Name = "CPU 快速模式",
            VideoCodec = "libx264",
            ExtraArgs = "-preset veryfast -crf 20",
            IsHardware = false
        };

        private async Task RunFfmpegAsync(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken,
            double expectedDurationSeconds = 0,
            Action<double> progress = null)
        {
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start FFmpeg.");
            RegisterActiveProcess(process);
            try
            {
                Task<string> stdoutTask = ReadFfmpegProgressAsync(
                    process.StandardOutput,
                    expectedDurationSeconds,
                    progress);
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                try
                {
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await StopProcessAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
                    throw;
                }

                string standardOutput = await stdoutTask.ConfigureAwait(false);
                string standardError = await stderrTask.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    string processOutput = standardOutput + standardError;
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(processOutput)
                        ? $"FFmpeg exited with code {process.ExitCode}."
                        : processOutput.Trim());
                }
            }
            finally
            {
                UnregisterActiveProcess(process);
            }
        }

        private static async Task<string> ReadFfmpegProgressAsync(
            StreamReader reader,
            double expectedDurationSeconds,
            Action<double> progress)
        {
            var output = new StringBuilder();
            double lastReported = -1;
            string line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                output.AppendLine(line);
                if (progress == null) continue;
                if (line.Equals("progress=end", StringComparison.Ordinal))
                {
                    progress(100);
                    continue;
                }
                if (expectedDurationSeconds <= 0 || !line.StartsWith("out_time_us=", StringComparison.Ordinal))
                    continue;
                if (!long.TryParse(line.Substring("out_time_us=".Length), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out long microseconds))
                    continue;
                double percent = Math.Clamp(microseconds / 1_000_000.0 / expectedDurationSeconds * 100.0, 0, 99.9);
                if (percent - lastReported < 1) continue;
                lastReported = percent;
                progress(percent);
            }
            return output.ToString();
        }

        private static async Task StopProcessAsync(
            Process process,
            Task<string> stdoutTask,
            Task<string> stderrTask)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
            try { await process.WaitForExitAsync().ConfigureAwait(false); } catch { }
            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); } catch { }
        }

        private static bool TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static int GetEffectiveParallelism(CompositionPlan plan, CompositionOptions options)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (options == null) throw new ArgumentNullException(nameof(options));
            int requested = Math.Max(1, Math.Min(options.MaxParallelism, 16));
            return plan.Jobs.Any(job => job.Clips.Count >= 20) ? 1 : requested;
        }

        internal static bool IsOutputDurationAcceptable(
            IReadOnlyList<double> clipDurations,
            double outputDuration)
        {
            if (clipDurations == null || clipDurations.Count == 0 || outputDuration <= 0
                || double.IsNaN(outputDuration) || double.IsInfinity(outputDuration))
                return false;
            double expectedDuration = clipDurations.Sum();
            double tolerance = Math.Max(0.35, clipDurations.Count / 25.0 + 0.15);
            return Math.Abs(outputDuration - expectedDuration) <= tolerance;
        }

        internal static IReadOnlyList<string> FindFailedClipPaths(
            string ffmpegError,
            IReadOnlyList<string> clips,
            bool muteAudio)
        {
            if (string.IsNullOrWhiteSpace(ffmpegError) || clips == null || clips.Count == 0)
                return Array.Empty<string>();

            return Regex.Matches(ffmpegError, @"\[vist#(?<index>\d+):")
                .Cast<Match>()
                .Select(match => int.Parse(match.Groups["index"].Value))
                .Where(index => index >= 0 && index < clips.Count)
                .Select(index => clips[index])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static ProcessStartInfo CreateConcatCopyStartInfo(
            string ffmpegPath,
            string concatListPath,
            string outputPath,
            bool muteAudio)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            AddArguments(startInfo,
                "-hide_banner", "-loglevel", "error",
                "-fflags", "+genpts",
                "-f", "concat", "-safe", "0", "-i", concatListPath,
                "-map", "0:v:0", "-c:v", "copy");
            if (muteAudio)
            {
                startInfo.ArgumentList.Add("-an");
            }
            else
            {
                AddArguments(startInfo, "-map", "0:a:0", "-c:a", "copy");
            }
            AddArguments(startInfo,
                "-avoid_negative_ts", "make_zero",
                "-movflags", "+faststart",
                "-progress", "pipe:1", "-nostats",
                "-y", outputPath);
            return startInfo;
        }

        public static ProcessStartInfo CreateStartInfo(
            string ffmpegPath,
            string concatListPath,
            string outputPath,
            bool muteAudio)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            AddArguments(startInfo,
                "-hide_banner", "-loglevel", "error",
                "-f", "concat", "-safe", "0", "-i", concatListPath,
                "-map", "0:v:0",
                "-vf", "scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "20");

            if (muteAudio)
            {
                startInfo.ArgumentList.Add("-an");
            }
            else
            {
                AddArguments(startInfo, "-map", "0:a?", "-c:a", "aac", "-b:a", "192k");
            }

            AddArguments(startInfo,
                "-movflags", "+faststart",
                "-progress", "pipe:1", "-nostats",
                "-y", outputPath);
            return startInfo;
        }

        public static ProcessStartInfo CreateStartInfo(
            string ffmpegPath,
            IEnumerable<string> clipPaths,
            string concatListPath,
            string outputPath,
            bool muteAudio)
        {
            return CreateStartInfo(
                ffmpegPath,
                clipPaths,
                concatListPath,
                outputPath,
                muteAudio,
                mediaInfo: null,
                encoderProfile: null);
        }

        public static ProcessStartInfo CreateStartInfo(
            string ffmpegPath,
            IEnumerable<string> clipPaths,
            string concatListPath,
            string outputPath,
            bool muteAudio,
            EncoderProfile encoderProfile)
        {
            return CreateStartInfo(
                ffmpegPath,
                clipPaths,
                concatListPath,
                outputPath,
                muteAudio,
                mediaInfo: null,
                encoderProfile: encoderProfile);
        }

        private static ProcessStartInfo CreateStartInfo(
            string ffmpegPath,
            IEnumerable<string> clipPaths,
            string concatListPath,
            string outputPath,
            bool muteAudio,
            IReadOnlyDictionary<string, CompositionClipMediaInfo> mediaInfo,
            EncoderProfile encoderProfile)
        {
            string[] clips = (clipPaths ?? throw new ArgumentNullException(nameof(clipPaths))).ToArray();
            if (clips.Length == 0) throw new ArgumentException("At least one clip is required.", nameof(clipPaths));
            if (!muteAudio && (mediaInfo == null || clips.Any(path => !mediaInfo.ContainsKey(path))))
                throw new ArgumentException("Every unmuted clip requires audio and duration metadata.", nameof(mediaInfo));

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            AddArguments(startInfo,
                "-hide_banner", "-loglevel", "error",
                "-filter_complex_threads", "1");
            foreach (string clip in clips) AddArguments(startInfo, "-threads", "1", "-i", clip);

            var filters = new List<string>(muteAudio ? clips.Length + 1 : clips.Length * 2 + 1);
            var videoLabels = new StringBuilder();
            for (int index = 0; index < clips.Length; index++)
            {
                string label = $"v{index}";
                filters.Add(
                    $"[{index}:v:0]" +
                    "scale=1920:1080:force_original_aspect_ratio=decrease," +
                    "pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30," +
                    $"setpts=PTS-STARTPTS[{label}]");
                videoLabels.Append('[').Append(label).Append(']');
            }

            if (muteAudio)
            {
                filters.Add($"{videoLabels}concat=n={clips.Length}:v=1:a=0[vout]");
            }
            else
            {
                var concatInputs = new StringBuilder();
                for (int index = 0; index < clips.Length; index++)
                {
                    CompositionClipMediaInfo info = mediaInfo[clips[index]];
                    string duration = info.VideoDurationSeconds.ToString("0.######", CultureInfo.InvariantCulture);
                    if (info.HasAudio)
                    {
                        filters.Add(
                            $"[{index}:a:0]aresample=48000:async=1:first_pts=0," +
                            "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo," +
                            $"asetpts=PTS-STARTPTS,apad,atrim=duration={duration}[a{index}]");
                    }
                    else
                    {
                        filters.Add(
                            $"anullsrc=r=48000:cl=stereo,atrim=duration={duration}," +
                            $"asetpts=PTS-STARTPTS[a{index}]");
                    }
                    concatInputs.Append("[v").Append(index).Append("][a").Append(index).Append(']');
                }
                filters.Add($"{concatInputs}concat=n={clips.Length}:v=1:a=1[vout][aout]");
            }

            EncoderProfile selectedProfile = encoderProfile ?? CreateCpuFallbackProfile();
            AddArguments(startInfo,
                "-filter_complex", string.Join(";", filters),
                "-map", "[vout]",
                "-c:v", selectedProfile.VideoCodec);
            foreach (string argument in (selectedProfile.ExtraArgs ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                startInfo.ArgumentList.Add(argument);
            }
            if (!selectedProfile.IsHardware)
                AddArguments(startInfo, "-threads:v", "4");

            if (muteAudio)
            {
                startInfo.ArgumentList.Add("-an");
            }
            else
            {
                AddArguments(startInfo,
                    "-map", "[aout]",
                    "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2");
            }

            AddArguments(startInfo,
                "-movflags", "+faststart",
                "-progress", "pipe:1", "-nostats",
                "-y", outputPath);
            return startInfo;
        }

        internal static string CreateConcatListContent(IEnumerable<string> clipPaths)
        {
            var builder = new StringBuilder("ffconcat version 1.0\n");
            foreach (string path in clipPaths)
            {
                string fullPath = Path.GetFullPath(path).Replace('\\', '/').Replace("'", "'\\''");
                builder.Append("file '").Append(fullPath).Append("'\n");
            }
            return builder.ToString();
        }

        private static void AddArguments(ProcessStartInfo startInfo, params string[] arguments)
        {
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        }
    }
}
