using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VideoToMaterial
{
    public enum DownloadCookieSource
    {
        None,
        Edge,
        Chrome,
        Firefox,
        CookieFile,
        EmbeddedPlatform
    }

    public sealed class DownloadCookieOptions
    {
        public DownloadCookieSource Source { get; set; }
        public string CookieFilePath { get; set; } = "";

        public bool IsConfigured => Source != DownloadCookieSource.None;
    }

    public class DownloadService
    {
        private const string ProgressPrefix = "MH_PROGRESS:";
        private const string FilePrefix = "MH_FILE:";
        private static readonly Regex ProgressPattern = new Regex(
            @"(?<percent>\d+(?:\.\d+)?)\s*%",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly string _ytdlpPath;
        private readonly string _ffmpegPath;

        public event Action<string> OnLog;
        public event Action<float> OnProgress;

        public DownloadService(string ytdlpPath, string ffmpegPath = null)
        {
            _ytdlpPath = ytdlpPath;
            _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath)
                ? Path.Combine(Path.GetDirectoryName(ytdlpPath) ?? "", "ffmpeg.exe")
                : ffmpegPath;
        }

        public async Task<string> DownloadVideoAsync(
            string url,
            string targetDir,
            CancellationToken cancellationToken = default)
        {
            return await DownloadVideoAsync(url, targetDir, null, cancellationToken).ConfigureAwait(false);
        }

        public async Task<string> DownloadVideoAsync(
            string url,
            string targetDir,
            DownloadCookieOptions cookieOptions,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("视频链接不能为空。", nameof(url));
            if (string.IsNullOrWhiteSpace(targetDir)) throw new ArgumentException("下载目录不能为空。", nameof(targetDir));
            if ((cookieOptions?.Source == DownloadCookieSource.CookieFile
                    || cookieOptions?.Source == DownloadCookieSource.EmbeddedPlatform)
                && (string.IsNullOrWhiteSpace(cookieOptions.CookieFilePath) || !File.Exists(cookieOptions.CookieFilePath)))
                throw new FileNotFoundException("选择的 Cookie 文件不存在。", cookieOptions.CookieFilePath);

            Directory.CreateDirectory(targetDir);
            int maxAttempts = cookieOptions?.IsConfigured == true ? 2 : 1;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    return await DownloadOnceAsync(url, targetDir, cookieOptions, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex) when (
                    attempt < maxAttempts && IsFreshCookieFailure(ex.Message))
                {
                    Log("[Cookie] 平台登录态未被接受，正在重新读取 Cookie 并重试一次...");
                }
            }

            throw new InvalidOperationException("下载失败。");
        }

        private async Task<string> DownloadOnceAsync(
            string url,
            string targetDir,
            DownloadCookieOptions cookieOptions,
            CancellationToken cancellationToken)
        {
            ProcessStartInfo startInfo = CreateStartInfo(_ytdlpPath, _ffmpegPath, url, targetDir, cookieOptions);
            Log(LocalizationManager.Format(
                "Log.DownloadCommand",
                Path.GetFileName(_ytdlpPath),
                $"--ffmpeg-location \"{Path.GetDirectoryName(_ffmpegPath)}\" ... <URL>"));

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(LocalizationManager.Text("Log.YtDlpStartFailed"));

            Task<DownloadStreamResult> stdoutTask = ReadStreamAsync(process.StandardOutput, logStableLines: false);
            Task<DownloadStreamResult> stderrTask = ReadStreamAsync(process.StandardError, logStableLines: true);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                await Task.WhenAll(stdoutTask, stderrTask);
                throw;
            }
            DownloadStreamResult[] streams = await Task.WhenAll(stdoutTask, stderrTask);

            string finalPath = streams
                .Select(stream => stream.FinalPath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            finalPath = ResolveDownloadedPath(finalPath, targetDir);

            if (process.ExitCode != 0)
            {
                string errorMessage = string.Join(
                    Environment.NewLine,
                    streams.SelectMany(stream => stream.Diagnostics).TakeLast(80));
                if (IsCookieAccessFailure(errorMessage))
                {
                    throw new InvalidOperationException(
                        "平台登录态不可用。建议选择“平台扫码登录（推荐）”；" +
                        "如果继续读取 Edge/Chrome，请先完全关闭对应浏览器。\n" + errorMessage);
                }
                throw new InvalidOperationException(
                    LocalizationManager.Format("Log.YtDlpFailed", process.ExitCode, errorMessage));
            }

            if (string.IsNullOrWhiteSpace(finalPath) || !File.Exists(finalPath))
            {
                string diagnostics = string.Join(
                    Environment.NewLine,
                    streams.SelectMany(stream => stream.Diagnostics).TakeLast(80));
                throw new InvalidOperationException(
                    LocalizationManager.Format("Log.YtDlpOutputMissing", diagnostics));
            }

            return finalPath;
        }

        public static bool IsFreshCookieFailure(string message)
        {
            return !string.IsNullOrWhiteSpace(message)
                && (message.Contains("Fresh cookies", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("cookies are needed", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("login required", StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsCookieAccessFailure(string message)
        {
            return IsFreshCookieFailure(message)
                || (!string.IsNullOrWhiteSpace(message)
                    && message.Contains("Could not copy", StringComparison.OrdinalIgnoreCase)
                    && message.Contains("cookie database", StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsPlatformLoginFailure(VideoPlatform platform, string message)
        {
            if (IsCookieAccessFailure(message)) return true;
            if (string.IsNullOrWhiteSpace(message)) return false;
            return platform switch
            {
                VideoPlatform.Bilibili =>
                    message.Contains("HTTP Error 412", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("Precondition Failed", StringComparison.OrdinalIgnoreCase),
                VideoPlatform.Xiaohongshu =>
                    message.Contains("[XiaoHongShu]", StringComparison.OrdinalIgnoreCase)
                    && (message.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("HTTP Error 461", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("No video formats found", StringComparison.OrdinalIgnoreCase)),
                _ => false
            };
        }

        public static ProcessStartInfo CreateStartInfo(
            string ytdlpPath,
            string ffmpegPath,
            string url,
            string targetDir)
        {
            return CreateStartInfo(ytdlpPath, ffmpegPath, url, targetDir, null);
        }

        public static ProcessStartInfo CreateStartInfo(
            string ytdlpPath,
            string ffmpegPath,
            string url,
            string targetDir,
            DownloadCookieOptions cookieOptions)
        {
            string outputTemplate = Path.Combine(targetDir, "%(title).50s_%(id)s.%(ext)s");
            string ffmpegDirectory = Path.GetDirectoryName(ffmpegPath) ?? targetDir;
            string denoPath = Path.Combine(Path.GetDirectoryName(ytdlpPath) ?? targetDir, "deno.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = ytdlpPath,
                WorkingDirectory = targetDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            AddArguments(
                startInfo,
                "--no-color",
                "--newline",
                "--progress",
                "--progress-template", $"download:{ProgressPrefix}%(progress._percent_str)s",
                "--print", $"after_move:{FilePrefix}%(filepath)s",
                "--ffmpeg-location", ffmpegDirectory,
                "--js-runtimes", "deno:" + denoPath,
                "--no-playlist",
                "--merge-output-format", "mp4",
                "--remux-video", "mp4",
                "-f", "bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/bestvideo+bestaudio/best",
                "-o", outputTemplate);
            AddCookieArguments(startInfo, cookieOptions);
            startInfo.ArgumentList.Add(url);
            return startInfo;
        }

        private static void AddCookieArguments(ProcessStartInfo startInfo, DownloadCookieOptions options)
        {
            if (options == null || options.Source == DownloadCookieSource.None) return;
            switch (options.Source)
            {
                case DownloadCookieSource.Edge:
                    AddArguments(startInfo, "--cookies-from-browser", "edge");
                    break;
                case DownloadCookieSource.Chrome:
                    AddArguments(startInfo, "--cookies-from-browser", "chrome");
                    break;
                case DownloadCookieSource.Firefox:
                    AddArguments(startInfo, "--cookies-from-browser", "firefox");
                    break;
                case DownloadCookieSource.CookieFile:
                case DownloadCookieSource.EmbeddedPlatform:
                    AddArguments(startInfo, "--cookies", options.CookieFilePath);
                    break;
            }
        }

        public static string RedactUrlForLog(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri parsed))
            {
                return parsed.GetLeftPart(UriPartial.Path);
            }
            return "<URL>";
        }

        private async Task<DownloadStreamResult> ReadStreamAsync(StreamReader reader, bool logStableLines)
        {
            var result = new DownloadStreamResult();
            string line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                string cleanLine = line.Trim();
                if (cleanLine.StartsWith(FilePrefix, StringComparison.Ordinal))
                {
                    result.FinalPath = cleanLine.Substring(FilePrefix.Length).Trim();
                    continue;
                }

                int progressIndex = cleanLine.IndexOf(ProgressPrefix, StringComparison.Ordinal);
                if (progressIndex >= 0)
                {
                    Match match = ProgressPattern.Match(cleanLine.Substring(progressIndex + ProgressPrefix.Length));
                    if (match.Success && float.TryParse(
                        match.Groups["percent"].Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out float percent))
                    {
                        OnProgress?.Invoke(Math.Clamp(percent, 0, 100));
                    }
                    continue;
                }

                if (string.IsNullOrWhiteSpace(cleanLine)) continue;
                result.Diagnostics.Add(cleanLine);
                if (logStableLines) Log("  " + cleanLine);
            }
            return result;
        }

        private static void AddArguments(ProcessStartInfo startInfo, params string[] arguments)
        {
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        }

        private static string ResolveDownloadedPath(string path, string targetDir)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string trimmed = path.Trim().Trim('"');
            return Path.IsPathRooted(trimmed) ? trimmed : Path.GetFullPath(Path.Combine(targetDir, trimmed));
        }

        private void Log(string message)
        {
            OnLog?.Invoke(message);
        }

        private sealed class DownloadStreamResult
        {
            public string FinalPath { get; set; }
            public List<string> Diagnostics { get; } = new List<string>();
        }
    }
}
