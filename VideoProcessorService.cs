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
        public double MinimumSceneDurationSeconds { get; set; } = 10.0;
        public double Sensitivity { get; set; } = 0.75;
        public bool Mute { get; set; }
        public int MaxThreads { get; set; } = 1;
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

        public async Task ProcessSingleVideoAsync(string input, string outDir, ProcessingOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.Profile == null) throw new InvalidOperationException("编码器配置未初始化。");

            if (options.SplitMode == SplitMode.FixedDuration)
            {
                await ProcessFixedDurationVideoAsync(input, outDir, options);
                return;
            }

            await ProcessSmartSceneVideoAsync(input, outDir, options);
        }

        private async Task ProcessFixedDurationVideoAsync(string input, string outDir, ProcessingOptions options)
        {
            double fixedDuration = Math.Max(1.0, Math.Min(600.0, options.FixedDurationSeconds));
            Log($"> 固定时长切片模式：每段 {fixedDuration:F1}s");

            double videoDuration = await GetVideoDurationAsync(input);
            List<SceneTask> tasks = BuildFixedDurationTasks(videoDuration, fixedDuration, outDir);

            Log($"> 时长分析完成：视频 {videoDuration:F2}s → {tasks.Count} 个片段");
            await ExportTasksAsync(input, tasks, options);
        }

        private async Task ProcessSmartSceneVideoAsync(string input, string outDir, ProcessingOptions options)
        {
            double sens = options.Sensitivity;
            double minSceneDuration = Math.Max(1.0, Math.Min(600.0, options.MinimumSceneDurationSeconds));
            Log("> 正在智能分析场景...");
            Log($"  [切片约束] 最小场景时长：{minSceneDuration:F1}s");
            Log($"  [检测引擎] {GetSceneDetectionEngineName(options.SceneDetectionEngine)}");
            List<double> stamps = await DetectScenes(input, sens, minSceneDuration, options.SceneDetectionEngine, options.HighPrecisionModelPath);
            
            // 确保总是包含0.0起点
            if (stamps.Count == 0 || stamps[0] > 0.1) stamps.Insert(0, 0.0);

            // 【针对首个切片过长问题的修复】
            // 无论首个切点在哪里，我们都强制对视频前20秒进行"高灵敏度二次扫描"
            // 原因是片头通常包含Logo、淡入淡出、快速剪辑等容易被漏检的场景
            // 且之前的逻辑依靠 firstGap > 5.0 容易被 0.1s 的噪点切片误导，导致跳过修复
            double scanDuration = 20.0;
            Log($"  [智能优化]正在执行前 {scanDuration}s 高灵敏度精细扫描...");

            var extraStamps = await DetectScenes(input, 0.95, Math.Min(minSceneDuration, scanDuration), SceneDetectionEngine.ContentDetector, null, 0, scanDuration);

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

            double videoDuration = await GetVideoDurationAsync(input);
            
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
            Dictionary<int, Bitmap> frameCache = await GetFramesAtAsync(input, candidates.Select(c => c.Time).ToList());
            int totalStamps = Math.Max(1, candidates.Count);
            
            for (int i = 0; i < candidates.Count; i++)
            {
                SceneCandidate candidate = candidates[i];
                double tStart = candidate.Time;
                double duration = candidate.Duration;

                Bitmap frame = null;
                try
                {
                    if (!frameCache.TryGetValue(i, out frame) || frame == null)
                    {
                        frame = await GetFrameAt(input, tStart);
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
                        string name = Path.Combine(outDir, $"Scene_{tasks.Count:D3}_{tStart:F2}s.mp4");
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

            await ExportTasksAsync(input, tasks, options);
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

        private List<SceneTask> BuildFixedDurationTasks(double videoDuration, double fixedDuration, string outDir)
        {
            if (videoDuration <= 0) throw new InvalidOperationException("无法获取有效的视频时长。");
            if (fixedDuration <= 0) throw new ArgumentOutOfRangeException(nameof(fixedDuration));

            List<SceneTask> tasks = new List<SceneTask>();
            int index = 0;

            for (double start = 0; start < videoDuration; start += fixedDuration)
            {
                double duration = Math.Min(fixedDuration, videoDuration - start);
                if (duration < 0.5) break;

                string name = Path.Combine(outDir, $"Scene_{index:D3}_{start:F2}s.mp4");
                tasks.Add(new SceneTask { Time = start, Duration = duration, OutputName = name });
                index++;
            }

            return tasks;
        }

        private async Task ExportTasksAsync(string input, List<SceneTask> tasks, ProcessingOptions options)
        {
            OnProgress?.Invoke(0, tasks.Count);

            using (SemaphoreSlim sem = new SemaphoreSlim(Math.Max(1, options.MaxThreads)))
            {
                List<Task> running = new List<Task>();
                int done = 0;
                
                foreach (var t in tasks)
                {
                    await sem.WaitAsync();
                    running.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await CutVideo(input, t.OutputName, t.Time, t.Duration, options.Mute, options.Profile);
                            Log($"  [OK] {Path.GetFileName(t.OutputName)} ({t.Duration:F1}s)");
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
            }
        }

        private Task<double> GetVideoDurationAsync(string input)
        {
            return Task.Run(() =>
            {
                if (File.Exists(_ffprobePath))
                {
                    try
                    {
                        string output = RunProcessCapture(_ffprobePath, $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{input}\"", logOnError: false);
                        if (double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double duration) && duration > 0)
                        {
                            return duration;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[提示] ffprobe 读取时长失败，改用 ffmpeg 回退解析：{ex.Message}");
                    }
                }

                Log("[提示] ffprobe 不可用或解析失败，尝试使用 ffmpeg 读取视频时长。");
                string ffmpegOutput = RunProcessCapture(_ffmpegPath, $"-i \"{input}\"", allowNonZeroExit: true);
                Match match = Regex.Match(ffmpegOutput, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                if (match.Success)
                {
                    double hours = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    double minutes = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                    double seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                    return hours * 3600 + minutes * 60 + seconds;
                }

                throw new InvalidOperationException("无法获取视频总时长，请确认 ffprobe.exe 与 ffmpeg.exe 可用。");
            });
        }

        private Task<List<double>> DetectScenes(string path, double sensitivity, double minDuration, SceneDetectionEngine engine = SceneDetectionEngine.ContentDetector, string highPrecisionModelPath = null, double startOffset = 0, double? maxDuration = null)
        {
            return Task.Run(() =>
            {
                if (engine == SceneDetectionEngine.HighPrecision)
                {
                    throw new NotSupportedException("高精度检测引擎尚未配置模型。请接入 TransNetV2/AutoShot ONNX 模型后再启用。");
                }

                // 🔧 修复：用户理解的"灵敏度"与FFmpeg的"阈值"是反向关系
                // 灵敏度 0.1 (低) -> 阈值 0.9 (只检测大变化)
                // 灵敏度 0.9 (高) -> 阈值 0.1 (检测微小变化)
                double threshold = Math.Max(0, Math.Min(1, 1.0 - sensitivity));
                
                // 仅在主扫描时打印日志
                if (startOffset == 0 && maxDuration == null)
                    Log($"> 场景检测配置：灵敏度={sensitivity:F2} -> FFmpeg阈值={threshold:F2}");

                List<double> stamps = DetectScenesWithThreshold(path, threshold, minDuration, startOffset, maxDuration);

                if (engine == SceneDetectionEngine.AdaptiveDetector)
                {
                    double adaptiveThreshold = Math.Max(0.02, threshold * 0.65);
                    List<double> sensitive = DetectScenesWithThreshold(path, adaptiveThreshold, Math.Max(0.5, minDuration * 0.5), startOffset, maxDuration);
                    int before = stamps.Count;
                    stamps = MergeAdaptiveSceneCandidates(stamps, sensitive, minDuration);
                    if (startOffset == 0 && maxDuration == null)
                    {
                        Log($"  [AdaptiveDetector] 弱切点补充：{before} → {stamps.Count}");
                    }
                }

                return stamps;
            });
        }

        private List<double> DetectScenesWithThreshold(string path, double threshold, double minDuration, double startOffset, double? maxDuration)
        {
            List<double> t = new List<double>();
            if (startOffset == 0) t.Add(0);

            string timeArgs = "";
            if (startOffset > 0) timeArgs += $"-ss {startOffset.ToString("0.###", CultureInfo.InvariantCulture)} ";
            if (maxDuration.HasValue) timeArgs += $"-t {maxDuration.Value.ToString("0.###", CultureInfo.InvariantCulture)} ";

            string thresholdText = threshold.ToString("0.###", CultureInfo.InvariantCulture);
            string args = $"{timeArgs}-i \"{path}\" -vf \"select='gt(scene,{thresholdText})',showinfo\" -f null -";

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (Process p = Process.Start(psi))
            {
                Regex r = new Regex(@"pts_time:([0-9.]+)");
                List<string> errors = new List<string>();
                while (!p.StandardError.EndOfStream)
                {
                    string l = p.StandardError.ReadLine();
                    if (l != null) errors.Add(l);

                    if (l != null && l.Contains("showinfo"))
                    {
                        Match m = r.Match(l);
                        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rawVal))
                        {
                            double val = rawVal + startOffset;
                            if (t.Count == 0 || val - t[t.Count - 1] > minDuration)
                            {
                                t.Add(val);
                            }
                        }
                    }
                }
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    throw new InvalidOperationException($"ffmpeg 场景检测失败 (代码 {p.ExitCode}): {string.Join("\n", errors.Take(20))}");
                }
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

        private Task<Bitmap> GetFrameAt(string path, double time)
        {
            return Task.Run(() =>
            {
                string tmp = Path.Combine(Path.GetTempPath(), $"tmp_{Guid.NewGuid()}.jpg");
                try
                {
                    RunFFmpeg($"-ss {time} -i \"{path}\" -vframes 1 -q:v 2 \"{tmp}\" -y");
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
                catch (Exception ex)
                {
                    Debug.WriteLine($"GetFrameAt Error: {ex.Message}");
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
                return null;
            });
        }

        private async Task<Dictionary<int, Bitmap>> GetFramesAtAsync(string path, IReadOnlyList<double> times)
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
                    int count = Math.Min(batchSize, times.Count - offset);
                    string batchDir = Path.Combine(root, offset.ToString(CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(batchDir);

                    try
                    {
                        await ExtractFrameBatchAsync(path, times, offset, count, batchDir);
                        for (int i = 0; i < count; i++)
                        {
                            string framePath = Path.Combine(batchDir, $"frame_{i + 1:0000}.jpg");
                            if (File.Exists(framePath))
                            {
                                frames[offset + i] = LoadBitmap(framePath);
                            }
                        }
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

        private Task ExtractFrameBatchAsync(string path, IReadOnlyList<double> times, int offset, int count, string batchDir)
        {
            return Task.Run(() =>
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
                RunProcessCapture(
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
                    });
            });
        }

        private static Bitmap LoadBitmap(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (Bitmap temp = new Bitmap(stream))
            {
                return new Bitmap(temp);
            }
        }

        private Task CutVideo(string path, string outPath, double time, double duration, bool mute, EncoderProfile profile)
        {
            return Task.Run(() =>
            {
                string hw = profile.IsHardware ? "-hwaccel auto" : "";
                string an = mute ? "-an" : "-c:a aac";
                string args = $"{hw} -ss {time} -i \"{path}\" -t {duration} -c:v {profile.VideoCodec} {profile.ExtraArgs} {an} -avoid_negative_ts make_zero \"{outPath}\" -y";
                RunFFmpeg(args);
            });
        }

        private void RunFFmpeg(string args)
        {
            RunProcessCapture(_ffmpegPath, args);
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
