using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VideoToMaterial
{
    public enum SplitMode
    {
        SmartScene,
        FixedDuration
    }

    public enum SceneDetectionEngine
    {
        ContentDetector,
        AdaptiveDetector,
        HighPrecision
    }

    public class ProcessingOptions
    {
        public SplitMode SplitMode { get; set; } = SplitMode.SmartScene;
        public SceneDetectionEngine SceneDetectionEngine { get; set; } = SceneDetectionEngine.ContentDetector;
        public string HighPrecisionModelPath { get; set; }
        public double FixedDurationSeconds { get; set; } = 10.0;
        public FixedClipSelectionMode FixedClipSelectionMode { get; set; } = FixedClipSelectionMode.Automatic;
        public double MinimumSceneDurationSeconds { get; set; } = 10.0;
        public double Sensitivity { get; set; } = 0.75;
        public bool Mute { get; set; }
        public SubtitleRemovalOptions SubtitleRemoval { get; set; } = new SubtitleRemovalOptions();
        public int MaxThreads { get; set; } = 1;
        public string OutputFileNamePrefix { get; set; } = "";
        public EncoderProfile Profile { get; set; }
    }

    public class VideoProcessorService
    {
        private readonly string _ffmpegPath;
        private readonly string _ffprobePath;
        
        public event Action<string> OnLog;
        public event Action<int, int> OnProgress;

        public VideoProcessorService(string ffmpegPath)
        {
            _ffmpegPath = ffmpegPath;
            _ffprobePath = Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
        }

        public async Task ProcessSingleVideoAsync(
            string input,
            string outDir,
            ProcessingOptions options,
            CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.Profile == null) throw new InvalidOperationException("编码器配置未初始化。");
            cancellationToken.ThrowIfCancellationRequested();

            string processingInput = input;
            string preparedInput = input;
            string cfrTempDirectory = null;
            string subtitleTempDirectory = null;
            bool useSinglePassSubtitleExport = false;
            try
            {
                if (options.SubtitleRemoval?.Enabled == true
                    && await IsVariableFrameRateAsync(input, cancellationToken))
                {
                    cfrTempDirectory = Path.Combine(
                        Path.GetTempPath(),
                        "MaterialHarvester",
                        "ConstantFrameRate",
                        Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(cfrTempDirectory);
                    processingInput = Path.Combine(cfrTempDirectory, "source-cfr.mkv");
                    EnsureTemporaryWorkspaceCapacity(input, cfrTempDirectory);
                    string frameRate = await GetNormalizationFrameRateAsync(input, cancellationToken);
                    Log($"> 检测到可变帧率视频，正在转换为 {frameRate} FPS 的无损恒定帧率工作源...");
                    await CreateConstantFrameRateSourceAsync(
                        input,
                        processingInput,
                        frameRate,
                        cancellationToken);
                    Log("> 恒定帧率工作源已就绪，后续场景规划、AI 去字幕和切片将使用同一时间基准。");
                }

                cancellationToken.ThrowIfCancellationRequested();
                List<SceneTask> tasks = options.SplitMode == SplitMode.FixedDuration
                    ? await PlanFixedDurationVideoAsync(processingInput, outDir, options, cancellationToken)
                    : await PlanSmartSceneVideoAsync(processingInput, outDir, options, cancellationToken);
                preparedInput = processingInput;

                if (options.SubtitleRemoval?.Enabled == true)
                {
                    SubtitleRemovalInstallation installation = options.SubtitleRemoval.Installation
                        ?? await SubtitleRemovalService.FindBestInstallationAsync(AppContext.BaseDirectory);
                    if (installation == null)
                    {
                        throw new InvalidOperationException(LocalizationManager.Text("Log.SubtitleBackendMissing"));
                    }
                    string aiAccelerator = await SubtitleRemovalService.ProbeAcceleratorAsync(installation);
                    Log(LocalizationManager.Format("Log.SubtitleAccelerator", aiAccelerator));
                    if (aiAccelerator == "CPU")
                    {
                        Log(LocalizationManager.Text("Log.SubtitleCpuWarning"));
                    }

                    (int width, int height) = await GetVideoDimensionsAsync(processingInput, cancellationToken);
                    double plannedDuration = await GetVideoDurationAsync(processingInput, cancellationToken);
                    options.SubtitleRemoval.ProcessingRanges = SubtitleRemovalService.BuildProcessingRanges(
                        tasks.Select(task => (task.Time, task.Duration)),
                        contextSeconds: 0.5,
                        totalDurationSeconds: plannedDuration);
                    options.SubtitleRemoval.OutputSegments = tasks
                        .Select(task => new SubtitleTimeRange(task.Time, task.Time + task.Duration))
                        .ToArray();
                    options.SubtitleRemoval.OutputVideoCodec = options.Profile.VideoCodec;
                    options.SubtitleRemoval.OutputVideoArguments = (options.Profile.ExtraArgs ?? "")
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    useSinglePassSubtitleExport = installation.SupportsSinglePassExport
                        && options.SubtitleRemoval.OutputSegments.Count > 0;
                    if (useSinglePassSubtitleExport)
                    {
                        Log("> 性能优化：AI 清理阶段使用最终编码器并写入切点关键帧，后续切片不再重复编码视频。");
                    }
                    if (installation.SupportsProcessingRanges && options.SubtitleRemoval.ProcessingRanges.Count > 0)
                    {
                        double aiSeconds = options.SubtitleRemoval.ProcessingRanges.Sum(
                            range => range.EndSeconds - range.StartSeconds);
                        Log(LocalizationManager.Format(
                            "Log.SubtitleRangeOptimization",
                            options.SubtitleRemoval.ProcessingRanges.Count,
                            aiSeconds));
                    }
                    subtitleTempDirectory = Path.Combine(
                        Path.GetTempPath(),
                        "MaterialHarvester",
                        "SubtitleRemoval",
                        Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(subtitleTempDirectory);
                    preparedInput = Path.Combine(subtitleTempDirectory, "cleaned.mp4");

                    Log(LocalizationManager.Format("Log.SubtitleRemovalStarted", options.SubtitleRemoval.InpaintMode));
                    await SubtitleRemovalService.RemoveAsync(
                        installation,
                        processingInput,
                        preparedInput,
                        options.SubtitleRemoval,
                        width,
                        height,
                        Log,
                        (current, total) => OnProgress?.Invoke(current, total),
                        cancellationToken);
                    Log(LocalizationManager.Text("Log.SubtitleRemovalFinished"));
                }

                cancellationToken.ThrowIfCancellationRequested();
                await ExportTasksAsync(
                    preparedInput,
                    tasks,
                    options,
                    useSinglePassSubtitleExport,
                    cancellationToken);
            }
            finally
            {
                if (subtitleTempDirectory != null)
                {
                    try
                    {
                        if (File.Exists(preparedInput)) File.Delete(preparedInput);
                        if (Directory.Exists(subtitleTempDirectory)) Directory.Delete(subtitleTempDirectory, false);
                    }
                    catch
                    {
                        // 临时文件清理失败不应覆盖实际处理结果。
                    }
                }
                if (cfrTempDirectory != null)
                {
                    try
                    {
                        if (Directory.Exists(cfrTempDirectory)) Directory.Delete(cfrTempDirectory, true);
                    }
                    catch
                    {
                        // 取消或失败后的临时工作源清理不能覆盖原始异常。
                    }
                }
            }
        }

        private async Task<bool> IsVariableFrameRateAsync(
            string input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(_ffprobePath)
                || (OperatingSystem.IsWindows() && !HasPortableExecutableHeader(_ffprobePath)))
            {
                Log("[提示] 无法使用 ffprobe 检查帧时间戳，将先生成安全的恒定帧率工作源。");
                return true;
            }

            try
            {
                (bool ratesSucceeded, string ratesOutput) = await TryRunProcessCaptureAsync(
                    _ffprobePath,
                    new[]
                    {
                        "-v", "error",
                        "-select_streams", "v:0",
                        "-show_entries", "stream=r_frame_rate,avg_frame_rate",
                        "-of", "default=noprint_wrappers=1",
                        input
                    },
                    TimeSpan.FromSeconds(15),
                    cancellationToken);
                if (!ratesSucceeded)
                {
                    Log("[提示] 无法可靠读取视频帧率，将先生成安全的恒定帧率工作源。");
                    return true;
                }

                double nominalRate = ReadProbeRate(ratesOutput, "r_frame_rate");
                double averageRate = ReadProbeRate(ratesOutput, "avg_frame_rate");
                if (nominalRate <= 0 || averageRate <= 0)
                {
                    Log("[提示] 视频帧率元数据不完整，将先生成安全的恒定帧率工作源。");
                    return true;
                }

                double rateTolerance = Math.Max(0.01, nominalRate * 0.005);
                if (Math.Abs(nominalRate - averageRate) > rateTolerance)
                {
                    return true;
                }

                return await HasVariablePacketCadenceAsync(input, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VFR detection failed: {ex.Message}");
                Log("[提示] 视频时间戳检查失败，将先生成安全的恒定帧率工作源。");
                return true;
            }
        }

        private async Task<bool> HasVariablePacketCadenceAsync(
            string input,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _ffprobePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            foreach (string argument in new[]
            {
                "-v", "error",
                "-select_streams", "v:0",
                "-show_packets",
                "-show_entries", "packet=pts_time,duration_time",
                "-of", "csv=p=0",
                input
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("ffprobe 帧时间戳检查进程启动失败。");
            var presentationTimes = new List<double>();
            var packetDurations = new List<double>();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                while (true)
                {
                    string line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                    if (line == null) break;
                    string[] values = line.Split(',');
                    if (values.Length > 0 && TryReadProbeNumber(values[0], out double pts))
                    {
                        presentationTimes.Add(pts);
                    }
                    if (values.Length > 1 && TryReadProbeNumber(values[1], out double duration)
                        && duration > 0)
                    {
                        packetDurations.Add(duration);
                    }
                }
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process);
                await WaitForTerminatedProcessAsync(process);
                try { await stderrTask; } catch { }
                throw;
            }

            string stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                Debug.WriteLine($"ffprobe packet cadence failed: {stderr}");
                return true;
            }

            if (HasMeaningfulCadenceVariation(packetDurations)) return true;
            if (presentationTimes.Count < 3) return false;

            presentationTimes.Sort();
            var intervals = new List<double>(presentationTimes.Count - 1);
            for (int i = 1; i < presentationTimes.Count; i++)
            {
                double interval = presentationTimes[i] - presentationTimes[i - 1];
                if (interval <= 0) return true;
                intervals.Add(interval);
            }
            return HasMeaningfulCadenceVariation(intervals);
        }

        private async Task<string> GetNormalizationFrameRateAsync(
            string input,
            CancellationToken cancellationToken)
        {
            if (File.Exists(_ffprobePath))
            {
                (bool succeeded, string output) = await TryRunProcessCaptureAsync(
                    _ffprobePath,
                    new[]
                    {
                        "-v", "error",
                        "-select_streams", "v:0",
                        "-show_entries", "stream=avg_frame_rate,r_frame_rate",
                        "-of", "default=noprint_wrappers=1",
                        input
                    },
                    TimeSpan.FromSeconds(15),
                    cancellationToken);
                if (succeeded)
                {
                    double rate = ReadProbeRate(output, "avg_frame_rate");
                    if (rate <= 0) rate = ReadProbeRate(output, "r_frame_rate");
                    if (rate > 0)
                    {
                        return NormalizeFrameRate(rate);
                    }
                }
            }

            Log("[提示] 无法确定源帧率，恒定帧率工作源将使用兼容的 30 FPS。");
            return "30";
        }

        private async Task CreateConstantFrameRateSourceAsync(
            string input,
            string output,
            string frameRate,
            CancellationToken cancellationToken)
        {
            var arguments = new[]
            {
                "-hide_banner",
                "-loglevel", "error",
                "-y",
                "-i", input,
                "-map", "0:v:0",
                "-map", "0:a?",
                "-c:v", "ffv1",
                "-level", "3",
                "-g", "1",
                "-r", frameRate,
                "-fps_mode", "cfr",
                "-c:a", "copy",
                "-map_metadata", "0",
                output
            };

            try
            {
                await RunProcessCaptureAsync(
                    _ffmpegPath,
                    arguments,
                    cancellationToken);
                if (!File.Exists(output) || new FileInfo(output).Length == 0)
                {
                    throw new InvalidOperationException("FFmpeg 没有生成有效的恒定帧率工作源。");
                }
                bool canVerifyFrameCadence = File.Exists(_ffprobePath)
                    && (!OperatingSystem.IsWindows() || HasPortableExecutableHeader(_ffprobePath));
                if (canVerifyFrameCadence
                    && await IsVariableFrameRateAsync(output, cancellationToken))
                {
                    throw new InvalidOperationException("恒定帧率工作源时间戳验证失败。");
                }
                if (!canVerifyFrameCadence)
                {
                    Log("[提示] ffprobe 不可用；已依据 FFmpeg 恒定帧率转换成功结果继续处理。");
                }
            }
            catch
            {
                TryDeleteFile(output);
                throw;
            }
        }

        private static bool HasMeaningfulCadenceVariation(List<double> values)
        {
            if (values.Count < 2) return false;
            double[] sorted = values.Where(value => value > 0).OrderBy(value => value).ToArray();
            if (sorted.Length < 2) return false;
            double median = sorted[sorted.Length / 2];
            // Matroska commonly stores timestamps at 1 ms resolution, so a true
            // 30/60/120 FPS stream naturally alternates by about 1 ms. Treat that
            // container quantization as CFR while still detecting dropped/duplicated frames.
            double tolerance = Math.Max(0.0015, median * 0.08);
            return sorted.Any(value => Math.Abs(value - median) > tolerance);
        }

        private static double ReadProbeRate(string output, string name)
        {
            Match match = Regex.Match(
                output ?? "",
                $@"(?m)^{Regex.Escape(name)}=(?<rate>[^\r\n]+)\r?$");
            return match.Success && TryReadRate(match.Groups["rate"].Value, out double rate)
                ? rate
                : 0;
        }

        private static bool TryReadRate(string value, out double rate)
        {
            rate = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string[] parts = value.Trim().Split('/');
            if (parts.Length == 2
                && TryReadProbeNumber(parts[0], out double numerator)
                && TryReadProbeNumber(parts[1], out double denominator)
                && denominator != 0)
            {
                rate = numerator / denominator;
                return double.IsFinite(rate) && rate > 0;
            }
            return TryReadProbeNumber(value, out rate) && rate > 0;
        }

        private static bool TryReadProbeNumber(string value, out double number)
        {
            return double.TryParse(
                value?.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out number)
                && double.IsFinite(number);
        }

        private static string NormalizeFrameRate(double sourceRate)
        {
            var commonRates = new (double Value, string Text)[]
            {
                (24000d / 1001d, "24000/1001"),
                (24, "24"),
                (25, "25"),
                (30000d / 1001d, "30000/1001"),
                (30, "30"),
                (50, "50"),
                (60000d / 1001d, "60000/1001"),
                (60, "60"),
                (120, "120")
            };
            (double Value, string Text) nearest = commonRates
                .OrderBy(candidate => Math.Abs(candidate.Value - sourceRate))
                .First();
            if (Math.Abs(nearest.Value - sourceRate) / sourceRate <= 0.02)
            {
                return nearest.Text;
            }
            return Math.Clamp(sourceRate, 1, 240)
                .ToString("0.######", CultureInfo.InvariantCulture);
        }

        private static void TryKillProcessTree(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能已经自行结束。
            }
        }

        private static async Task WaitForTerminatedProcessAsync(Process process)
        {
            try { await process.WaitForExitAsync(); } catch { }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 取消清理采用尽力而为，不能覆盖原始异常。
            }
        }

        private static void EnsureTemporaryWorkspaceCapacity(string input, string temporaryDirectory)
        {
            long inputBytes = new FileInfo(input).Length;
            string root = Path.GetPathRoot(Path.GetFullPath(temporaryDirectory));
            if (string.IsNullOrWhiteSpace(root)) return;

            long requiredBytes;
            try
            {
                checked
                {
                    requiredBytes = inputBytes * 8L + 2L * 1024 * 1024 * 1024;
                }
            }
            catch (OverflowException)
            {
                requiredBytes = long.MaxValue;
            }

            long availableBytes = new DriveInfo(root).AvailableFreeSpace;
            if (availableBytes < requiredBytes)
            {
                throw new IOException(
                    $"临时磁盘空间不足，VFR 安全处理至少需要约 {FormatByteSize(requiredBytes)}，" +
                    $"当前仅剩 {FormatByteSize(availableBytes)}。请释放 {root} 空间后重试。");
            }
        }

        private static string FormatByteSize(long bytes)
        {
            const double gib = 1024d * 1024d * 1024d;
            return bytes == long.MaxValue
                ? "超大容量"
                : $"{bytes / gib:F1} GB";
        }

        private async Task<(int width, int height)> GetVideoDimensionsAsync(
            string input,
            CancellationToken cancellationToken = default)
        {
            if (File.Exists(_ffprobePath))
            {
                (bool succeeded, string output) = await TryRunProcessCaptureAsync(
                    _ffprobePath,
                    new[]
                    {
                        "-v", "error",
                        "-select_streams", "v:0",
                        "-show_entries", "stream=width,height",
                        "-of", "csv=s=x:p=0",
                        input
                    },
                    TimeSpan.FromSeconds(8),
                    cancellationToken);
                if (succeeded)
                {
                    Match match = Regex.Match(output.Trim(), @"^(\d+)x(\d+)$");
                    if (match.Success)
                    {
                        return (
                            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
                    }
                }
                Log("[提示] ffprobe 尺寸探测失败，改用 FFmpeg 抽帧读取尺寸。");
            }

            using Bitmap frame = await GetFrameAt(input, 0, cancellationToken);
            if (frame == null) throw new InvalidOperationException("无法读取视频尺寸，AI 去字幕不能开始。");
            return (frame.Width, frame.Height);
        }

        private async Task<List<SceneTask>> PlanFixedDurationVideoAsync(
            string input,
            string outDir,
            ProcessingOptions options,
            CancellationToken cancellationToken)
        {
            double fixedDuration = Math.Max(1.0, Math.Min(600.0, options.FixedDurationSeconds));
            Log($"> 固定时长切片模式：每段 {fixedDuration:F1}s");

            double videoDuration = await GetVideoDurationAsync(input, cancellationToken);
            List<SceneTask> tasks = BuildFixedDurationTasks(
                videoDuration,
                fixedDuration,
                options.FixedClipSelectionMode,
                outDir,
                options.OutputFileNamePrefix);

            Log($"> 时长分析完成：视频 {videoDuration:F2}s → {tasks.Count} 个片段");
            return tasks;
        }

        private async Task<List<SceneTask>> PlanSmartSceneVideoAsync(
            string input,
            string outDir,
            ProcessingOptions options,
            CancellationToken cancellationToken)
        {
            double sens = options.Sensitivity;
            double minSceneDuration = Math.Max(1.0, Math.Min(600.0, options.MinimumSceneDurationSeconds));
            Log("> 正在智能分析场景...");
            Log($"  [切片约束] 最小场景时长：{minSceneDuration:F1}s");
            Log($"  [检测引擎] {GetSceneDetectionEngineName(options.SceneDetectionEngine)}");
            List<double> stamps = await DetectScenes(
                input,
                sens,
                minSceneDuration,
                options.SceneDetectionEngine,
                options.HighPrecisionModelPath,
                cancellationToken: cancellationToken);
            
            // 确保总是包含0.0起点
            if (stamps.Count == 0 || stamps[0] > 0.1) stamps.Insert(0, 0.0);

            // 【针对首个切片过长问题的修复】
            // 无论首个切点在哪里，我们都强制对视频前20秒进行"高灵敏度二次扫描"
            // 原因是片头通常包含Logo、淡入淡出、快速剪辑等容易被漏检的场景
            // 且之前的逻辑依靠 firstGap > 5.0 容易被 0.1s 的噪点切片误导，导致跳过修复
            double scanDuration = 20.0;
            Log($"  [智能优化]正在执行前 {scanDuration}s 高灵敏度精细扫描...");

            var extraStamps = await DetectScenes(
                input,
                0.95,
                Math.Min(minSceneDuration, scanDuration),
                SceneDetectionEngine.ContentDetector,
                null,
                0,
                scanDuration,
                cancellationToken);

            int added = 0;
            foreach (var t in extraStamps)
            {
                // 简单的距离去重，防止太近的重复 (0.3s内视为同一场景点)
                if (!stamps.Any(existing => Math.Abs(existing - t) < 0.3))
                {
                    stamps.Add(t);
                    added++;
                }
            }

            if (added > 0)
            {
                stamps.Sort();
                stamps = EnforceMinimumSceneGap(stamps, minSceneDuration);
                Log($"  [智能优化] 在片头补充了 {added} 个细微场景切点");
            }

            double videoDuration = await GetVideoDurationAsync(input, cancellationToken);
            
            // 最终导出任务列表
            List<SceneTask> tasks = new List<SceneTask>();
            
            // 状态追踪
            SceneTask currentTask = null;
            ulong lastHashP = 0, lastHashC = 0;
            // 存储已接受场景的哈希用于全局去重
            List<(ulong p, ulong c)> acceptedHashes = new List<(ulong, ulong)>();

            List<SceneCandidate> candidates = new List<SceneCandidate>();
            for (int i = 0; i < stamps.Count; i++)
            {
                double tStart = stamps[i];
                double tNext = (i < stamps.Count - 1) ? stamps[i + 1] : videoDuration;
                double duration = tNext - tStart;

                if (duration < 0.5)
                {
                    continue;
                }

                candidates.Add(new SceneCandidate { StampIndex = i, Time = tStart, Duration = duration });
            }

            Log($"  [性能] 批量抽取 {candidates.Count} 个切点帧用于去重分析...");
            Dictionary<int, Bitmap> frameCache = await GetFramesAtAsync(
                input,
                candidates.Select(c => c.Time).ToList(),
                cancellationToken);
            int totalStamps = Math.Max(1, candidates.Count);
            
            for (int i = 0; i < candidates.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SceneCandidate candidate = candidates[i];
                double tStart = candidate.Time;
                double duration = candidate.Duration;

                Bitmap frame = null;
                try
                {
                    if (!frameCache.TryGetValue(i, out frame) || frame == null)
                    {
                        frame = await GetFrameAt(input, tStart, cancellationToken);
                    }

                    using (Bitmap bmp = frame)
                    {
                        frame = null;
                    if (bmp == null) continue;
                    
                    var (hP, hC) = CalculatePerceptualHash(bmp);
                    
                    bool isMerge = false;
                    bool isDrop = false;

                    // 如果当前有正在进行的任务，先判断是否可以合并（相邻去重）
                    if (currentTask != null)
                    {
                        int distP = HammingDistance(hP, lastHashP);
                        int distC = HammingDistance(hC, lastHashC);
                        
                        // 阈值：非常相似 (主Hash<=3, 颜色<=2) -> 视为同一场景的延续
                        if (distP <= 3 && distC <= 2)
                        {
                            isMerge = true;
                        }
                        else
                        {
                            // 如果不合并，则检查是否是全局重复（即之前出现过的场景又出现了）
                            // 阈值：主Hash<=5, 颜色<=3 -> 视为重复内容
                            foreach (var past in acceptedHashes)
                            {
                                int pDist = HammingDistance(hP, past.p);
                                int cDist = HammingDistance(hC, past.c);
                                
                                if (pDist <= 5 && cDist <= 3)
                                {
                                    isDrop = true;
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        // 当前没有任务（刚开始或刚断开），只检查全局去重
                        foreach (var past in acceptedHashes)
                        {
                            int pDist = HammingDistance(hP, past.p);
                            int cDist = HammingDistance(hC, past.c);
                            
                            if (pDist <= 5 && cDist <= 3)
                            {
                                isDrop = true;
                                break;
                            }
                        }
                    }

                    // 执行决策
                    if (isMerge)
                    {
                        // 【合并】：延长当前任务的时长
                        currentTask.Duration += duration;
                        Log($"  [合并] 场景延续 (T:{tStart:F2}s +{duration:F1}s)");
                        // 注意：不更新 lastHash，保持以场景起始帧为准，防止渐变漂移
                    }
                    else if (isDrop)
                    {
                        // 【丢弃】：断开当前连接，且不创建新任务
                        // 这会在输出视频中形成一个“时间缺口”，从而物理上删除了这段重复内容
                        if (currentTask != null)
                        {
                            // 结束上一段
                            currentTask = null;
                        }
                        Log($"  [丢弃] 重复冗余 (T:{tStart:F2}s)");
                    }
                    else
                    {
                        // 【新增】：截断上一段（如果存在），开始新场景
                        string name = Path.Combine(outDir, $"{options.OutputFileNamePrefix}Scene_{tasks.Count:D3}_{tStart:F2}s.mp4");
                        currentTask = new SceneTask { Time = tStart, Duration = duration, OutputName = name };
                        tasks.Add(currentTask);
                        
                        // 记录哈希
                        acceptedHashes.Add((hP, hC));
                        lastHashP = hP; 
                        lastHashC = hC;
                        
                        Log($"  [新增] 独立场景 (T:{tStart:F2}s)");
                    }
                    }
                }
                finally
                {
                    frame?.Dispose();
                }
                
                OnProgress?.Invoke(i + 1, totalStamps);
            }
            
            Log($"> 智能分析完成：{stamps.Count} 个切点 → {tasks.Count} 个有效素材 (过滤率:{(1 - tasks.Count / (float)Math.Max(1, stamps.Count)) * 100:F1}%)");
            
            Log($"> 智能去重完成：{stamps.Count} 个场景 → {tasks.Count} 个精选片段 (去重率:{(1 - tasks.Count / (float)Math.Max(1, stamps.Count)) * 100:F1}%)");

            return tasks;
        }

        private List<double> EnforceMinimumSceneGap(List<double> stamps, double minSceneDuration)
        {
            List<double> normalized = new List<double>();
            foreach (double stamp in stamps.OrderBy(t => t))
            {
                if (normalized.Count == 0 || stamp - normalized[normalized.Count - 1] >= minSceneDuration)
                {
                    normalized.Add(stamp);
                }
            }

            if (normalized.Count == 0 || normalized[0] > 0.1)
            {
                normalized.Insert(0, 0.0);
            }

            return normalized;
        }

        private List<SceneTask> BuildFixedDurationTasks(
            double videoDuration,
            double fixedDuration,
            FixedClipSelectionMode selectionMode,
            string outDir,
            string outputFileNamePrefix)
        {
            if (videoDuration <= 0) throw new InvalidOperationException("无法获取有效的视频时长。");
            if (fixedDuration <= 0) throw new ArgumentOutOfRangeException(nameof(fixedDuration));

            List<FixedClipRange> ranges = FixedDurationSelectionPlanner
                .Create(videoDuration, fixedDuration, selectionMode)
                .ToList();
            List<SceneTask> tasks = new List<SceneTask>(ranges.Count);

            for (int index = 0; index < ranges.Count; index++)
            {
                FixedClipRange range = ranges[index];
                string name = Path.Combine(
                    outDir,
                    $"{outputFileNamePrefix}Scene_{index:D3}_{range.StartSeconds:F2}s.mp4");
                tasks.Add(new SceneTask
                {
                    Time = range.StartSeconds,
                    Duration = range.DurationSeconds,
                    OutputName = name
                });
            }

            return tasks;
        }

        private async Task ExportTasksAsync(
            string input,
            List<SceneTask> tasks,
            ProcessingOptions options,
            bool copyPreparedVideo = false,
            CancellationToken cancellationToken = default)
        {
            OnProgress?.Invoke(0, tasks.Count);

            using (SemaphoreSlim sem = new SemaphoreSlim(Math.Max(1, options.MaxThreads)))
            {
                List<Task> running = new List<Task>();
                List<(SceneTask Task, Exception Error)> failedTasks = new List<(SceneTask, Exception)>();
                object failedTasksLock = new object();
                int done = 0;
                try
                {
                    foreach (var t in tasks)
                    {
                        await sem.WaitAsync(cancellationToken);
                        running.Add(Task.Run(async () =>
                        {
                            try
                            {
                                await CutVideo(
                                    input,
                                    t.OutputName,
                                    t.Time,
                                    t.Duration,
                                    options.Mute,
                                    options.Profile,
                                    copyPreparedVideo,
                                    cancellationToken);
                                Log($"  [OK] {Path.GetFileName(t.OutputName)} ({t.Duration:F1}s)");
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                lock (failedTasksLock)
                                {
                                    failedTasks.Add((t, ex));
                                }
                            }
                            finally
                            {
                                sem.Release();
                                int d = Interlocked.Increment(ref done);
                                OnProgress?.Invoke(d, tasks.Count);
                            }
                        }));
                    }
                    await Task.WhenAll(running);

                    if (failedTasks.Count > 0)
                    {
                        Log($"> 检测到 {failedTasks.Count} 个切片发生瞬时失败，正在降低负载并逐个串行重试...");
                        foreach ((SceneTask failedTask, Exception _) in failedTasks)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            await CutVideo(
                                input,
                                failedTask.OutputName,
                                failedTask.Time,
                                failedTask.Duration,
                                options.Mute,
                                options.Profile,
                                copyPreparedVideo,
                                cancellationToken);
                            Log($"  [OK-串行重试] {Path.GetFileName(failedTask.OutputName)} ({failedTask.Duration:F1}s)");
                        }
                    }
                }
                catch
                {
                    try { await Task.WhenAll(running); } catch { }
                    throw;
                }
            }
        }

        private async Task<double> GetVideoDurationAsync(
            string input,
            CancellationToken cancellationToken = default)
        {
            if (File.Exists(_ffprobePath))
            {
                (bool streamSucceeded, string streamOutput) = await TryRunProcessCaptureAsync(
                    _ffprobePath,
                    new[]
                    {
                        "-v", "error",
                        "-select_streams", "v:0",
                        "-show_entries", "stream=duration",
                        "-of", "default=noprint_wrappers=1:nokey=1",
                        input
                    },
                    TimeSpan.FromSeconds(8),
                    cancellationToken);
                if (streamSucceeded && double.TryParse(
                    streamOutput.Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double duration) && duration > 0)
                {
                    return duration;
                }

                (bool formatSucceeded, string formatOutput) = await TryRunProcessCaptureAsync(
                    _ffprobePath,
                    new[]
                    {
                        "-v", "error",
                        "-show_entries", "format=duration",
                        "-of", "default=noprint_wrappers=1:nokey=1",
                        input
                    },
                    TimeSpan.FromSeconds(8),
                    cancellationToken);
                if (formatSucceeded && double.TryParse(
                    formatOutput.Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out duration) && duration > 0)
                {
                    return duration;
                }
                Log("[提示] ffprobe 读取时长失败，改用 FFmpeg 回退解析。");
            }

            Log("[提示] ffprobe 不可用或解析失败，尝试使用 ffmpeg 读取视频时长。");
            (bool _, string ffmpegOutput) = await TryRunProcessCaptureAsync(
                _ffmpegPath,
                new[] { "-hide_banner", "-i", input },
                TimeSpan.FromSeconds(15),
                cancellationToken);
            Match match = Regex.Match(ffmpegOutput, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (match.Success)
            {
                double hours = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                double minutes = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                double seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                return hours * 3600 + minutes * 60 + seconds;
            }

            throw new InvalidOperationException("无法获取视频总时长，请确认 ffprobe.exe 与 ffmpeg.exe 可用。");
        }

        private async Task<List<double>> DetectScenes(
            string path,
            double sensitivity,
            double minDuration,
            SceneDetectionEngine engine = SceneDetectionEngine.ContentDetector,
            string highPrecisionModelPath = null,
            double startOffset = 0,
            double? maxDuration = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (engine == SceneDetectionEngine.HighPrecision)
            {
                throw new NotSupportedException("高精度检测引擎尚未配置模型。请接入 TransNetV2/AutoShot ONNX 模型后再启用。");
            }

            double threshold = Math.Max(0, Math.Min(1, 1.0 - sensitivity));
            if (startOffset == 0 && maxDuration == null)
                Log($"> 场景检测配置：灵敏度={sensitivity:F2} -> FFmpeg阈值={threshold:F2}");

            List<double> stamps = await DetectScenesWithThresholdAsync(
                path,
                threshold,
                minDuration,
                startOffset,
                maxDuration,
                cancellationToken);

            if (engine == SceneDetectionEngine.AdaptiveDetector)
            {
                double adaptiveThreshold = Math.Max(0.02, threshold * 0.65);
                List<double> sensitive = await DetectScenesWithThresholdAsync(
                    path,
                    adaptiveThreshold,
                    Math.Max(0.5, minDuration * 0.5),
                    startOffset,
                    maxDuration,
                    cancellationToken);
                int before = stamps.Count;
                stamps = MergeAdaptiveSceneCandidates(stamps, sensitive, minDuration);
                if (startOffset == 0 && maxDuration == null)
                {
                    Log($"  [AdaptiveDetector] 弱切点补充：{before} → {stamps.Count}");
                }
            }

            return stamps;
        }

        private async Task<List<double>> DetectScenesWithThresholdAsync(
            string path,
            double threshold,
            double minDuration,
            double startOffset,
            double? maxDuration,
            CancellationToken cancellationToken)
        {
            List<double> t = new List<double>();
            if (startOffset == 0) t.Add(0);

            string thresholdText = threshold.ToString("0.###", CultureInfo.InvariantCulture);
            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            if (startOffset > 0)
            {
                startInfo.ArgumentList.Add("-ss");
                startInfo.ArgumentList.Add(startOffset.ToString("0.###", CultureInfo.InvariantCulture));
            }
            if (maxDuration.HasValue)
            {
                startInfo.ArgumentList.Add("-t");
                startInfo.ArgumentList.Add(maxDuration.Value.ToString("0.###", CultureInfo.InvariantCulture));
            }
            foreach (string argument in new[]
            {
                "-i", path,
                "-vf", $"select='gt(scene,{thresholdText})',showinfo",
                "-f", "null", "-"
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("FFmpeg 场景检测进程启动失败。");
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            var errors = new List<string>();
            Regex timestampPattern = new Regex(@"pts_time:([0-9.]+)");
            try
            {
                while (true)
                {
                    string line = await process.StandardError.ReadLineAsync(cancellationToken);
                    if (line == null) break;
                    if (errors.Count < 200) errors.Add(line);

                    if (line.Contains("showinfo"))
                    {
                        Match match = timestampPattern.Match(line);
                        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rawVal))
                        {
                            double val = rawVal + startOffset;
                            if (t.Count == 0 || val - t[t.Count - 1] > minDuration)
                            {
                                t.Add(val);
                            }
                        }
                    }
                }
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process);
                await WaitForTerminatedProcessAsync(process);
                try { await stdoutTask; } catch { }
                throw;
            }
            await stdoutTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"ffmpeg 场景检测失败 (代码 {process.ExitCode}): {string.Join("\n", errors.Take(20))}");
            }

            return t;
        }

        private List<double> MergeAdaptiveSceneCandidates(List<double> primary, List<double> sensitive, double minDuration)
        {
            List<double> merged = primary.OrderBy(t => t).ToList();
            foreach (double candidate in sensitive.OrderBy(t => t))
            {
                if (merged.Any(t => Math.Abs(t - candidate) < minDuration))
                {
                    continue;
                }

                double previous = merged.Where(t => t < candidate).DefaultIfEmpty(0).Max();
                double next = merged.Where(t => t > candidate).DefaultIfEmpty(double.MaxValue).Min();
                bool inLongGap = candidate - previous >= minDuration && (next == double.MaxValue || next - candidate >= minDuration);
                if (inLongGap)
                {
                    merged.Add(candidate);
                    merged.Sort();
                }
            }

            return EnforceMinimumSceneGap(merged, minDuration);
        }

        private string GetSceneDetectionEngineName(SceneDetectionEngine engine)
        {
            switch (engine)
            {
                case SceneDetectionEngine.AdaptiveDetector:
                    return "AdaptiveDetector 自适应智能";
                case SceneDetectionEngine.HighPrecision:
                    return "HighPrecision 高精度模型";
                default:
                    return "ContentDetector 内容变化";
            }
        }

        private async Task<Bitmap> GetFrameAt(
            string path,
            double time,
            CancellationToken cancellationToken = default)
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"tmp_{Guid.NewGuid()}.jpg");
            try
            {
                await RunProcessCaptureAsync(
                    _ffmpegPath,
                    new[]
                    {
                        "-hide_banner",
                        "-loglevel", "error",
                        "-ss", time.ToString("0.###", CultureInfo.InvariantCulture),
                        "-i", path,
                        "-vframes", "1",
                        "-q:v", "2",
                        "-y",
                        tmp
                    },
                    cancellationToken);
                if (File.Exists(tmp))
                {
                    using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read))
                    {
                        using (var tempBmp = new Bitmap(fs))
                        {
                            return new Bitmap(tempBmp);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetFrameAt Error: {ex.Message}");
            }
            finally
            {
                TryDeleteFile(tmp);
            }
            return null;
        }

        private async Task<Dictionary<int, Bitmap>> GetFramesAtAsync(
            string path,
            IReadOnlyList<double> times,
            CancellationToken cancellationToken)
        {
            Dictionary<int, Bitmap> frames = new Dictionary<int, Bitmap>();
            if (times.Count == 0) return frames;

            const int batchSize = 24;
            string root = Path.Combine(Path.GetTempPath(), "VideoToMaterial", "BatchFrames", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                for (int offset = 0; offset < times.Count; offset += batchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(batchSize, times.Count - offset);
                    string batchDir = Path.Combine(root, offset.ToString(CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(batchDir);

                    try
                    {
                        await ExtractFrameBatchAsync(
                            path,
                            times,
                            offset,
                            count,
                            batchDir,
                            cancellationToken);
                        for (int i = 0; i < count; i++)
                        {
                            string framePath = Path.Combine(batchDir, $"frame_{i + 1:0000}.jpg");
                            if (File.Exists(framePath))
                            {
                                frames[offset + i] = LoadBitmap(framePath);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log($"  [提示] 批量抽帧失败，回退为逐帧抽取：{ex.Message}");
                    }
                }
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }

            return frames;
        }

        private async Task ExtractFrameBatchAsync(
            string path,
            IReadOnlyList<double> times,
            int offset,
            int count,
            string batchDir,
            CancellationToken cancellationToken)
        {
            List<string> branches = new List<string>();
            List<string> labels = new List<string>();

            for (int i = 0; i < count; i++)
            {
                string label = $"v{i}";
                string start = Math.Max(0, times[offset + i]).ToString("0.###", CultureInfo.InvariantCulture);
                branches.Add($"[0:v]trim=start={start}:duration=0.2,setpts=PTS-STARTPTS,select='eq(n\\,0)'[{label}]");
                labels.Add($"[{label}]");
            }

            string filter = string.Join(";", branches) + ";" +
                string.Concat(labels) +
                $"concat=n={count}:v=1:a=0,scale=320:-2[outv]";

            string outputPattern = Path.Combine(batchDir, "frame_%04d.jpg");
            await RunProcessCaptureAsync(
                _ffmpegPath,
                new[]
                {
                    "-hide_banner",
                    "-loglevel", "error",
                    "-i", path,
                    "-filter_complex", filter,
                    "-map", "[outv]",
                    "-vsync", "0",
                    "-q:v", "2",
                    "-y",
                    outputPattern
                },
                cancellationToken);
        }

        private static Bitmap LoadBitmap(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (Bitmap temp = new Bitmap(stream))
            {
                return new Bitmap(temp);
            }
        }

        private Task CutVideo(
            string path,
            string outPath,
            double time,
            double duration,
            bool mute,
            EncoderProfile profile,
            bool copyPreparedVideo = false,
            CancellationToken cancellationToken = default)
        {
            return CutVideoCoreAsync();

            async Task CutVideoCoreAsync()
            {
                var attempts = new List<(bool CopyVideo, string Codec, string[] ExtraArguments, bool HardwareDecode)>();
                if (copyPreparedVideo)
                {
                    attempts.Add((true, null, Array.Empty<string>(), false));
                }
                attempts.Add((
                    false,
                    profile.VideoCodec,
                    (profile.ExtraArgs ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    profile.IsHardware));
                if (!string.Equals(profile.VideoCodec, "libx264", StringComparison.OrdinalIgnoreCase))
                {
                    attempts.Add((
                        false,
                        "libx264",
                        new[] { "-preset", "veryfast", "-crf", "20" },
                        false));
                }

                Exception lastError = null;
                for (int attemptIndex = 0; attemptIndex < attempts.Count; attemptIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    (bool copyVideo, string codec, string[] extraArguments, bool hardwareDecode) = attempts[attemptIndex];
                    List<string> arguments = BuildCutArguments(
                        path,
                        outPath,
                        time,
                        duration,
                        mute,
                        copyVideo,
                        codec,
                        extraArguments,
                        hardwareDecode);
                    try
                    {
                        await RunProcessCaptureAsync(
                            _ffmpegPath,
                            arguments,
                            cancellationToken);
                        (bool validOutput, string validationError) = await VideoComposerService.ValidateClipAsync(
                            _ffmpegPath,
                            outPath,
                            cancellationToken);
                        if (!validOutput)
                        {
                            throw new InvalidOperationException(
                                $"切片产物无法完整解码：{validationError}");
                        }
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        TryDeleteFile(outPath);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        TryDeleteFile(outPath);
                        if (attemptIndex + 1 < attempts.Count)
                        {
                            string failedMode = copyVideo ? "无损切片" : codec;
                            string nextMode = attempts[attemptIndex + 1].CopyVideo
                                ? "无损切片"
                                : attempts[attemptIndex + 1].Codec;
                            Log($"  [提示] {Path.GetFileName(outPath)} 使用 {failedMode} 失败，自动改用 {nextMode} 重试。");
                        }
                    }
                }

                string finalReason = lastError?.Message ?? "未返回具体错误。";
                throw new InvalidOperationException(
                    $"切片 {Path.GetFileName(outPath)} 的无损复制、首选编码器和 CPU 回退均失败。" +
                    $" 最后一次失败原因：{finalReason}",
                    lastError);
            }
        }

        private static List<string> BuildCutArguments(
            string path,
            string outPath,
            double time,
            double duration,
            bool mute,
            bool copyVideo,
            string videoCodec,
            IReadOnlyList<string> extraVideoArguments,
            bool hardwareDecode)
        {
            var arguments = new List<string>
            {
                "-hide_banner",
                "-loglevel", "error"
            };
            if (!copyVideo && hardwareDecode)
            {
                arguments.Add("-hwaccel");
                arguments.Add("auto");
            }
            arguments.Add("-ss");
            arguments.Add(time.ToString("0.######", CultureInfo.InvariantCulture));
            arguments.Add("-i");
            arguments.Add(path);
            arguments.Add("-t");
            arguments.Add(duration.ToString("0.######", CultureInfo.InvariantCulture));
            arguments.Add("-map");
            arguments.Add("0:v:0");
            arguments.Add("-c:v");
            arguments.Add(copyVideo ? "copy" : videoCodec);
            if (!copyVideo && extraVideoArguments != null)
            {
                arguments.AddRange(extraVideoArguments);
            }
            if (mute)
            {
                arguments.Add("-an");
            }
            else
            {
                arguments.Add("-map");
                arguments.Add("0:a?");
                arguments.Add("-c:a");
                arguments.Add("aac");
            }
            arguments.Add("-avoid_negative_ts");
            arguments.Add(copyVideo ? "disabled" : "make_zero");
            arguments.Add("-y");
            arguments.Add(outPath);
            return arguments;
        }

        private void RunFFmpeg(string args)
        {
            RunProcessCapture(_ffmpegPath, args);
        }

        private async Task<(bool Succeeded, string Output)> TryRunProcessCaptureAsync(
            string fileName,
            IEnumerable<string> arguments,
            TimeSpan timeout)
        {
            return await TryRunProcessCaptureAsync(
                fileName,
                arguments,
                timeout,
                CancellationToken.None);
        }

        private async Task<(bool Succeeded, string Output)> TryRunProcessCaptureAsync(
            string fileName,
            IEnumerable<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (OperatingSystem.IsWindows() && !HasPortableExecutableHeader(fileName))
            {
                return (false, "not a valid Windows executable");
            }
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };
                foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

                using Process process = Process.Start(startInfo);
                if (process == null) return (false, "");
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                using var timeoutCancellation = new CancellationTokenSource(timeout);
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutCancellation.Token);
                try
                {
                    await process.WaitForExitAsync(linkedCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    TryKillProcessTree(process);
                    await WaitForTerminatedProcessAsync(process);
                    try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }
                    if (cancellationToken.IsCancellationRequested) throw;
                    return (false, "process timed out");
                }

                string stdout = await stdoutTask;
                string stderr = await stderrTask;
                return (process.ExitCode == 0, stdout + stderr);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Optional process probe failed: {ex.Message}");
                return (false, ex.Message);
            }
        }

        private async Task<string> RunProcessCaptureAsync(
            string fileName,
            IEnumerable<string> arguments,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"{Path.GetFileName(fileName)} 进程启动失败。");
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process);
                await WaitForTerminatedProcessAsync(process);
                try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }
                throw;
            }

            string output = await stdoutTask + await stderrTask;
            if (process.ExitCode != 0)
            {
                string message = string.IsNullOrWhiteSpace(output) ? "无错误输出" : output.Trim();
                throw new InvalidOperationException(
                    $"{Path.GetFileName(fileName)} 执行失败 (代码 {process.ExitCode}): {message}");
            }
            return output;
        }

        private static bool HasPortableExecutableHeader(string path)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
            }
            catch
            {
                return false;
            }
        }

        private string RunProcessCapture(string fileName, string args, bool allowNonZeroExit = false, bool logOnError = true)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                using (Process p = Process.Start(psi))
                {
                    Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = p.StandardError.ReadToEndAsync();
                    p.WaitForExit();
                    Task.WaitAll(stdoutTask, stderrTask);

                    string output = stdoutTask.Result + stderrTask.Result;
                    if (p.ExitCode != 0 && !allowNonZeroExit)
                    {
                        string message = string.IsNullOrWhiteSpace(output) ? "无错误输出" : output.Trim();
                        throw new InvalidOperationException($"{Path.GetFileName(fileName)} 执行失败 (代码 {p.ExitCode}): {message}");
                    }

                    return output;
                }
            }
            catch (Exception ex)
            {
                if (logOnError)
                {
                    Log($"[Process Error] {Path.GetFileName(fileName)}: {ex.Message}");
                }
                throw;
            }
        }

        private string RunProcessCapture(string fileName, IEnumerable<string> arguments, bool allowNonZeroExit = false, bool logOnError = true)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                foreach (string argument in arguments)
                {
                    psi.ArgumentList.Add(argument);
                }

                using (Process p = Process.Start(psi))
                {
                    Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = p.StandardError.ReadToEndAsync();
                    p.WaitForExit();
                    Task.WaitAll(stdoutTask, stderrTask);

                    string output = stdoutTask.Result + stderrTask.Result;
                    if (p.ExitCode != 0 && !allowNonZeroExit)
                    {
                        string message = string.IsNullOrWhiteSpace(output) ? "无错误输出" : output.Trim();
                        throw new InvalidOperationException($"{Path.GetFileName(fileName)} 执行失败 (代码 {p.ExitCode}): {message}");
                    }

                    return output;
                }
            }
            catch (Exception ex)
            {
                if (logOnError)
                {
                    Log($"[Process Error] {Path.GetFileName(fileName)}: {ex.Message}");
                }
                throw;
            }
        }

        // ===== 升级版哈希算法：多尺度感知哈希 (pHash) =====
        private (ulong primary, ulong secondary) CalculatePerceptualHash(Bitmap image)
        {
            ulong primary = VideoAnalysisAlgorithms.CalculateLowFrequencyDctHash(image);
            ulong secondary = CalculateColorHistogramHash(image);
            return (primary, secondary);
        }

        private ulong CalculateColorHistogramHash(Bitmap img)
        {
            float[] hist = VideoAnalysisAlgorithms.CalculateColorHistogram(img);
            ulong hash = 0;

            for (int channel = 0; channel < 3; channel++)
            {
                int offset = channel * 8;
                float avg = 0;
                for (int i = 0; i < 8; i++)
                {
                    avg += hist[offset + i];
                }
                avg /= 8;

                for (int i = 0; i < 8; i++)
                {
                    if (hist[offset + i] > avg)
                    {
                        hash |= 1UL << (offset + i);
                    }
                }
            }

            return hash;
        }

        private int HammingDistance(ulong h1, ulong h2)
        {
            ulong xor = h1 ^ h2;
            int d = 0;
            while (xor > 0)
            {
                d += (int)(xor & 1);
                xor >>= 1;
            }
            return d;
        }

        private void Log(string msg)
        {
            OnLog?.Invoke(msg);
        }

        private class SceneCandidate { public int StampIndex; public double Time; public double Duration; }
        private class SceneTask { public double Time; public double Duration; public string OutputName; }
    }
}
