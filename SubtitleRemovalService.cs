using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VideoToMaterial
{
    public readonly struct SubtitlePixelRegion
    {
        public SubtitlePixelRegion(int yMin, int yMax, int xMin, int xMax)
        {
            YMin = yMin;
            YMax = yMax;
            XMin = xMin;
            XMax = xMax;
        }

        public int YMin { get; }
        public int YMax { get; }
        public int XMin { get; }
        public int XMax { get; }
    }

    public readonly struct SubtitleRegion
    {
        public SubtitleRegion(double left, double top, double right, double bottom)
        {
            Left = Math.Clamp(left, 0, 1);
            Top = Math.Clamp(top, 0, 1);
            Right = Math.Clamp(right, Left, 1);
            Bottom = Math.Clamp(bottom, Top, 1);
        }

        public double Left { get; }
        public double Top { get; }
        public double Right { get; }
        public double Bottom { get; }

        public SubtitlePixelRegion ToPixels(int width, int height)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            return new SubtitlePixelRegion(
                (int)Math.Round(Top * height),
                (int)Math.Round(Bottom * height),
                (int)Math.Round(Left * width),
                (int)Math.Round(Right * width));
        }

        public static SubtitleRegion FromSelection(
            double startX,
            double startY,
            double endX,
            double endY,
            double surfaceWidth,
            double surfaceHeight)
        {
            if (surfaceWidth <= 0) throw new ArgumentOutOfRangeException(nameof(surfaceWidth));
            if (surfaceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(surfaceHeight));

            return new SubtitleRegion(
                Math.Min(startX, endX) / surfaceWidth,
                Math.Min(startY, endY) / surfaceHeight,
                Math.Max(startX, endX) / surfaceWidth,
                Math.Max(startY, endY) / surfaceHeight);
        }

        public static SubtitleRegion Default => new SubtitleRegion(0.05, 0.65, 0.95, 0.96);
    }

    public readonly struct SubtitleTimeRange
    {
        public SubtitleTimeRange(double startSeconds, double endSeconds)
        {
            StartSeconds = Math.Max(0, startSeconds);
            EndSeconds = Math.Max(StartSeconds, endSeconds);
        }

        public double StartSeconds { get; }
        public double EndSeconds { get; }
    }

    public sealed class SubtitleRemovalInstallation
    {
        public SubtitleRemovalInstallation(
            string pythonPath,
            string backendScriptPath,
            string workingDirectory = null,
            bool supportsProcessingRanges = false,
            bool supportsSinglePassExport = false)
        {
            PythonPath = pythonPath;
            BackendScriptPath = backendScriptPath;
            WorkingDirectory = workingDirectory ?? ResolveWorkingDirectory(backendScriptPath);
            SupportsProcessingRanges = supportsProcessingRanges;
            SupportsSinglePassExport = supportsSinglePassExport;
        }

        public string PythonPath { get; }
        public string BackendScriptPath { get; }
        public string WorkingDirectory { get; }
        public bool SupportsProcessingRanges { get; }
        public bool SupportsSinglePassExport { get; }

        private static string ResolveWorkingDirectory(string scriptPath)
        {
            string scriptDirectory = Path.GetDirectoryName(scriptPath);
            return string.Equals(Path.GetFileName(scriptDirectory), "backend", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(scriptDirectory)
                : scriptDirectory;
        }
    }

    public sealed class SubtitleRemovalOptions
    {
        public bool Enabled { get; set; }
        public bool UseAutomaticDetection { get; set; } = true;
        public string InpaintMode { get; set; } = "sttn-det";
        public SubtitleRegion Region { get; set; } = SubtitleRegion.Default;
        public SubtitleRemovalInstallation Installation { get; set; }
        public IReadOnlyList<SubtitleTimeRange> ProcessingRanges { get; set; } = Array.Empty<SubtitleTimeRange>();
        public IReadOnlyList<SubtitleTimeRange> OutputSegments { get; set; } = Array.Empty<SubtitleTimeRange>();
        public string OutputVideoCodec { get; set; } = "libx264";
        public IReadOnlyList<string> OutputVideoArguments { get; set; } = Array.Empty<string>();
    }

    public static class SubtitleRemovalService
    {
        private const string AcceleratorPrefix = "MH_ACCELERATOR:";
        public static readonly TimeSpan AcceleratorProbeTimeout = TimeSpan.FromSeconds(120);
        private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> AcceleratorProbeCache =
            new ConcurrentDictionary<string, Lazy<Task<string>>>(StringComparer.OrdinalIgnoreCase);

        public static string DetectPackagedAccelerator(SubtitleRemovalInstallation installation)
        {
            if (installation == null) throw new ArgumentNullException(nameof(installation));
            string root = installation.WorkingDirectory ?? Path.GetDirectoryName(installation.PythonPath) ?? "";
            string[] cudaCandidates =
            {
                Path.Combine(root, "_internal", "torch", "lib", "torch_cuda.dll"),
                Path.Combine(root, "videoEnv", "Lib", "site-packages", "torch", "lib", "torch_cuda.dll"),
                Path.Combine(root, "python", "Lib", "site-packages", "torch", "lib", "torch_cuda.dll"),
                Path.Combine(root, "Lib", "site-packages", "torch", "lib", "torch_cuda.dll")
            };
            if (cudaCandidates.Any(File.Exists)) return "CUDA";

            string[] directMlCandidates =
            {
                Path.Combine(root, "_internal", "torch_directml"),
                Path.Combine(root, "videoEnv", "Lib", "site-packages", "torch_directml"),
                Path.Combine(root, "python", "Lib", "site-packages", "torch_directml"),
                Path.Combine(root, "Lib", "site-packages", "torch_directml")
            };
            if (directMlCandidates.Any(Directory.Exists)) return "DirectML";
            return "CPU";
        }

        public static Task<string> ProbeAcceleratorAsync(SubtitleRemovalInstallation installation)
        {
            if (installation == null) throw new ArgumentNullException(nameof(installation));
            string cacheKey = string.Join("|", installation.PythonPath, installation.WorkingDirectory);
            return AcceleratorProbeCache.GetOrAdd(
                cacheKey,
                _ => new Lazy<Task<string>>(
                    () => ProbeAcceleratorCoreAsync(installation),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        public static string SelectDefaultInpaintMode(string accelerator)
        {
            return string.Equals(accelerator, "CPU", StringComparison.OrdinalIgnoreCase)
                ? "lama"
                : "sttn-det";
        }

        private static async Task<string> ProbeAcceleratorCoreAsync(SubtitleRemovalInstallation installation)
        {
            if (!File.Exists(installation.PythonPath)) return "CPU";

            const string probeScript =
                "import importlib.util\n" +
                "device='CPU'\n" +
                "try:\n" +
                " import torch\n" +
                " if bool(torch.cuda.is_available()):\n" +
                "  (torch.ones(1,device='cuda')+1).cpu()\n" +
                "  device='CUDA'\n" +
                " elif importlib.util.find_spec('torch_directml') is not None:\n" +
                "  import torch_directml\n" +
                "  d=torch_directml.device()\n" +
                "  (torch.ones(1,device=d)+1).cpu()\n" +
                "  device='DirectML'\n" +
                "except Exception:\n" +
                " device='CPU'\n" +
                $"print('{AcceleratorPrefix}'+device)\n";

            var startInfo = new ProcessStartInfo
            {
                FileName = installation.PythonPath,
                WorkingDirectory = installation.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(probeScript);

            try
            {
                using Process process = Process.Start(startInfo);
                if (process == null) return "CPU";
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                Task waitTask = process.WaitForExitAsync();
                if (await Task.WhenAny(waitTask, Task.Delay(AcceleratorProbeTimeout)) != waitTask)
                {
                    try { process.Kill(true); } catch { }
                    await Task.WhenAll(stdoutTask, stderrTask);
                    return "CPU";
                }

                await waitTask;
                string stdout = await stdoutTask;
                _ = await stderrTask;
                if (process.ExitCode != 0) return "CPU";

                string marker = stdout
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .LastOrDefault(line => line.StartsWith(AcceleratorPrefix, StringComparison.Ordinal));
                string device = marker?.Substring(AcceleratorPrefix.Length).Trim();
                return device is "CUDA" or "DirectML" ? device : "CPU";
            }
            catch
            {
                return "CPU";
            }
        }

        public static IReadOnlyList<SubtitleTimeRange> BuildProcessingRanges(
            IEnumerable<(double Start, double Duration)> segments,
            double contextSeconds,
            double totalDurationSeconds)
        {
            if (segments == null) throw new ArgumentNullException(nameof(segments));
            double context = Math.Max(0, contextSeconds);
            double durationLimit = Math.Max(0, totalDurationSeconds);
            List<SubtitleTimeRange> candidates = segments
                .Where(segment => segment.Duration > 0)
                .Select(segment => new SubtitleTimeRange(
                    Math.Max(0, segment.Start - context),
                    Math.Min(durationLimit, segment.Start + segment.Duration + context)))
                .Where(range => range.EndSeconds > range.StartSeconds)
                .OrderBy(range => range.StartSeconds)
                .ToList();

            var merged = new List<SubtitleTimeRange>();
            foreach (SubtitleTimeRange candidate in candidates)
            {
                if (merged.Count == 0)
                {
                    merged.Add(candidate);
                    continue;
                }

                SubtitleTimeRange previous = merged[merged.Count - 1];
                if (candidate.StartSeconds <= previous.EndSeconds + 0.001)
                {
                    merged[merged.Count - 1] = new SubtitleTimeRange(
                        previous.StartSeconds,
                        Math.Max(previous.EndSeconds, candidate.EndSeconds));
                }
                else
                {
                    merged.Add(candidate);
                }
            }
            return merged;
        }

        public static SubtitleRemovalInstallation FindInstallation(string applicationDirectory)
        {
            return FindInstallations(applicationDirectory).FirstOrDefault();
        }

        public static async Task<SubtitleRemovalInstallation> FindBestInstallationAsync(string applicationDirectory)
        {
            IReadOnlyList<SubtitleRemovalInstallation> installations = FindInstallations(applicationDirectory);
            if (installations.Count == 0) return null;

            SubtitleRemovalInstallation cpuFallback = installations.FirstOrDefault(installation =>
                string.Equals(
                    Path.GetFileName(installation.WorkingDirectory),
                    "video-subtitle-remover",
                    StringComparison.OrdinalIgnoreCase));
            foreach (SubtitleRemovalInstallation installation in installations)
            {
                string folder = Path.GetFileName(installation.WorkingDirectory);
                bool claimsAcceleration = folder.EndsWith("-cuda", StringComparison.OrdinalIgnoreCase)
                    || folder.EndsWith("-directml", StringComparison.OrdinalIgnoreCase);
                if (!claimsAcceleration || await ProbeAcceleratorAsync(installation) != "CPU")
                {
                    return installation;
                }
            }

            return cpuFallback ?? installations[0];
        }

        private static IReadOnlyList<SubtitleRemovalInstallation> FindInstallations(string applicationDirectory)
        {
            string aiRoot = Path.Combine(applicationDirectory, "ai");
            var roots = new List<string>();
            string cudaRoot = Path.Combine(aiRoot, "video-subtitle-remover-cuda");
            if (File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll")))
            {
                roots.Add(cudaRoot);
            }
            roots.Add(Path.Combine(aiRoot, "video-subtitle-remover-directml"));
            roots.Add(Path.Combine(aiRoot, "video-subtitle-remover"));

            var installations = new List<SubtitleRemovalInstallation>();
            foreach (string root in roots)
            {
                string upstreamScript = Path.Combine(root, "backend", "main.py");
                if (!File.Exists(upstreamScript)) continue;

                string[] wrapperCandidates =
                {
                    Path.Combine(applicationDirectory, "packaging", "material_harvester_vsr.py"),
                    Path.Combine(root, "material_harvester_vsr.py")
                };
                string[] pythonCandidates =
                {
                    Path.Combine(root, "videoEnv", "Scripts", "python.exe"),
                    Path.Combine(root, "python", "python.exe"),
                    Path.Combine(root, "python.exe")
                };

                string wrapper = wrapperCandidates.FirstOrDefault(File.Exists);
                string script = wrapper ?? upstreamScript;
                foreach (string python in pythonCandidates)
                {
                    if (File.Exists(python))
                    {
                        installations.Add(new SubtitleRemovalInstallation(
                            python,
                            script,
                            root,
                            supportsProcessingRanges: wrapper != null,
                            supportsSinglePassExport: wrapper != null));
                        break;
                    }
                }
            }

            return installations;
        }

        public static ProcessStartInfo CreateStartInfo(
            string pythonPath,
            string backendScriptPath,
            string inputPath,
            string outputPath,
            SubtitleRemovalOptions options,
            int videoWidth,
            int videoHeight,
            string processingRangesFile = null)
        {
            return CreateStartInfo(
                pythonPath,
                backendScriptPath,
                inputPath,
                outputPath,
                options,
                videoWidth,
                videoHeight,
                processingRangesFile,
                null,
                null,
                null);
        }

        public static ProcessStartInfo CreateStartInfo(
            string pythonPath,
            string backendScriptPath,
            string inputPath,
            string outputPath,
            SubtitleRemovalOptions options,
            int videoWidth,
            int videoHeight,
            string processingRangesFile,
            string outputSegmentsFile,
            string videoCodec,
            IReadOnlyList<string> videoArguments)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            string backendDirectory = Path.GetDirectoryName(backendScriptPath);
            string sidecarRoot = string.IsNullOrWhiteSpace(backendDirectory)
                ? null
                : Path.GetDirectoryName(backendDirectory);
            if (!string.IsNullOrWhiteSpace(sidecarRoot))
            {
                startInfo.WorkingDirectory = sidecarRoot;
            }

            startInfo.ArgumentList.Add(backendScriptPath);
            startInfo.ArgumentList.Add("--input");
            startInfo.ArgumentList.Add(inputPath);
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(outputPath);
            if (!options.UseAutomaticDetection)
            {
                SubtitlePixelRegion pixels = options.Region.ToPixels(videoWidth, videoHeight);
                startInfo.ArgumentList.Add("--subtitle-area-coords");
                startInfo.ArgumentList.Add(pixels.YMin.ToString());
                startInfo.ArgumentList.Add(pixels.YMax.ToString());
                startInfo.ArgumentList.Add(pixels.XMin.ToString());
                startInfo.ArgumentList.Add(pixels.XMax.ToString());
            }
            startInfo.ArgumentList.Add("--inpaint-mode");
            startInfo.ArgumentList.Add(options.InpaintMode);
            if (!string.IsNullOrWhiteSpace(processingRangesFile))
            {
                startInfo.ArgumentList.Add("--process-ranges-file");
                startInfo.ArgumentList.Add(processingRangesFile);
            }
            if (!string.IsNullOrWhiteSpace(outputSegmentsFile))
            {
                startInfo.ArgumentList.Add("--output-segments-file");
                startInfo.ArgumentList.Add(outputSegmentsFile);
            }
            if (!string.IsNullOrWhiteSpace(videoCodec))
            {
                startInfo.ArgumentList.Add("--video-codec");
                startInfo.ArgumentList.Add(videoCodec);
            }
            if (videoArguments != null)
            {
                foreach (string argument in videoArguments.Where(argument => !string.IsNullOrWhiteSpace(argument)))
                {
                    startInfo.ArgumentList.Add("--video-arg=" + argument);
                }
            }
            return startInfo;
        }

        public static async Task RemoveAsync(
            SubtitleRemovalInstallation installation,
            string inputPath,
            string outputPath,
            SubtitleRemovalOptions options,
            int videoWidth,
            int videoHeight,
            Action<string> log,
            Action<int, int> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (installation == null) throw new ArgumentNullException(nameof(installation));
            string rangesFile = null;
            string segmentsFile = null;
            try
            {
                if (installation.SupportsProcessingRanges && options.ProcessingRanges?.Count > 0)
                {
                    rangesFile = Path.Combine(
                        Path.GetTempPath(),
                        $"material-harvester-subtitle-ranges-{Guid.NewGuid():N}.json");
                    await File.WriteAllTextAsync(rangesFile, JsonSerializer.Serialize(options.ProcessingRanges));
                }
                if (installation.SupportsSinglePassExport && options.OutputSegments?.Count > 0)
                {
                    segmentsFile = Path.Combine(
                        Path.GetTempPath(),
                        $"material-harvester-output-segments-{Guid.NewGuid():N}.json");
                    await File.WriteAllTextAsync(segmentsFile, JsonSerializer.Serialize(options.OutputSegments));
                }

                ProcessStartInfo startInfo = CreateStartInfo(
                    installation.PythonPath,
                    installation.BackendScriptPath,
                    inputPath,
                    outputPath,
                    options,
                    videoWidth,
                    videoHeight,
                    rangesFile,
                    segmentsFile,
                    installation.SupportsSinglePassExport ? options.OutputVideoCodec : null,
                    installation.SupportsSinglePassExport ? options.OutputVideoArguments : null);
                if (!string.IsNullOrWhiteSpace(installation.WorkingDirectory))
                {
                    startInfo.WorkingDirectory = installation.WorkingDirectory;
                }

                using Process process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("AI 去字幕进程启动失败。");

                Task stdout = PumpLinesAsync(process.StandardOutput, log, progress);
                Task stderr = PumpLinesAsync(process.StandardError, log, progress);
                try
                {
                    await process.WaitForExitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(true); } catch { }
                    await Task.WhenAll(stdout, stderr);
                    throw;
                }
                await Task.WhenAll(stdout, stderr);

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"AI 去字幕执行失败（代码 {process.ExitCode}）。");
                }
                if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                {
                    throw new InvalidOperationException("AI 去字幕没有生成有效的视频文件。");
                }
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(rangesFile))
                {
                    try { File.Delete(rangesFile); } catch { }
                }
                if (!string.IsNullOrWhiteSpace(segmentsFile))
                {
                    try { File.Delete(segmentsFile); } catch { }
                }
            }
        }

        private static async Task PumpLinesAsync(
            StreamReader reader,
            Action<string> log,
            Action<int, int> progress)
        {
            string line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                SubtitleProcessOutput output = SubtitleProcessOutput.Parse(line);
                if (output.IsProgress) progress?.Invoke(output.Current, output.Total);
                if (!string.IsNullOrWhiteSpace(output.Message)) log?.Invoke("  " + output.Message);
            }
        }
    }
}
