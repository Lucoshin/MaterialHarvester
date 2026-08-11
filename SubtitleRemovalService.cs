using System;
using System.Diagnostics;
using System.IO;
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

    public sealed class SubtitleRemovalInstallation
    {
        public SubtitleRemovalInstallation(string pythonPath, string backendScriptPath)
        {
            PythonPath = pythonPath;
            BackendScriptPath = backendScriptPath;
        }

        public string PythonPath { get; }
        public string BackendScriptPath { get; }
    }

    public sealed class SubtitleRemovalOptions
    {
        public bool Enabled { get; set; }
        public bool UseAutomaticDetection { get; set; } = true;
        public string InpaintMode { get; set; } = "sttn-auto";
        public SubtitleRegion Region { get; set; } = SubtitleRegion.Default;
        public SubtitleRemovalInstallation Installation { get; set; }
    }

    public static class SubtitleRemovalService
    {
        public static SubtitleRemovalInstallation FindInstallation(string applicationDirectory)
        {
            string root = Path.Combine(applicationDirectory, "ai", "video-subtitle-remover");
            string script = Path.Combine(root, "backend", "main.py");
            string[] pythonCandidates =
            {
                Path.Combine(root, "videoEnv", "Scripts", "python.exe"),
                Path.Combine(root, "python", "python.exe"),
                Path.Combine(root, "python.exe")
            };

            if (!File.Exists(script)) return null;
            foreach (string python in pythonCandidates)
            {
                if (File.Exists(python)) return new SubtitleRemovalInstallation(python, script);
            }

            return null;
        }

        public static ProcessStartInfo CreateStartInfo(
            string pythonPath,
            string backendScriptPath,
            string inputPath,
            string outputPath,
            SubtitleRemovalOptions options,
            int videoWidth,
            int videoHeight)
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
            return startInfo;
        }

        public static async Task RemoveAsync(
            SubtitleRemovalInstallation installation,
            string inputPath,
            string outputPath,
            SubtitleRemovalOptions options,
            int videoWidth,
            int videoHeight,
            Action<string> log)
        {
            if (installation == null) throw new ArgumentNullException(nameof(installation));
            ProcessStartInfo startInfo = CreateStartInfo(
                installation.PythonPath,
                installation.BackendScriptPath,
                inputPath,
                outputPath,
                options,
                videoWidth,
                videoHeight);

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("AI 去字幕进程启动失败。");

            Task stdout = PumpLinesAsync(process.StandardOutput, log);
            Task stderr = PumpLinesAsync(process.StandardError, log);
            await process.WaitForExitAsync();
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

        private static async Task PumpLinesAsync(StreamReader reader, Action<string> log)
        {
            string line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line)) log?.Invoke("  " + line);
            }
        }
    }
}
