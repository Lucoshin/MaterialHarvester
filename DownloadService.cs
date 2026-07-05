using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VideoToMaterial
{
    public class DownloadService
    {
        private readonly string _ytdlpPath;
        
        public event Action<string> OnLog;
        public event Action<float> OnProgress;

        public DownloadService(string ytdlpPath)
        {
            _ytdlpPath = ytdlpPath;
        }

        public Task<string> DownloadVideoAsync(string url, string targetDir)
        {
            return Task.Run(() =>
            {
                string outputTemplate = Path.Combine(targetDir, "%(title).50s_%(id)s.%(ext)s");
                string args = $"--print after_move:filepath -f \"bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best\" " +
                             $"--merge-output-format mp4 --no-playlist -o \"{outputTemplate}\" \"{url}\"";

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = _ytdlpPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };

                string finalPath = "";
                List<string> errors = new List<string>();

                Log(LocalizationManager.Format("Log.DownloadCommand", _ytdlpPath, args));

                using (Process p = Process.Start(psi))
                {
                    if (p == null) throw new InvalidOperationException(LocalizationManager.Text("Log.YtDlpStartFailed"));

                    while (!p.StandardOutput.EndOfStream)
                    {
                        string line = p.StandardOutput.ReadLine();
                        if (!string.IsNullOrWhiteSpace(line) && File.Exists(line))
                        {
                            finalPath = line;
                        }
                        
                        // 解析进度
                        if (line != null && line.Contains("[download]") && line.Contains("%"))
                        {
                            var match = Regex.Match(line, @"(\d+\.\d+)%");
                            if (match.Success)
                            {
                                float progress = float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                                OnProgress?.Invoke(progress);
                            }
                        }
                    }

                    while (!p.StandardError.EndOfStream)
                    {
                        errors.Add(p.StandardError.ReadLine());
                    }

                    p.WaitForExit();

                    if (p.ExitCode != 0)
                    {
                        string errorMsg = string.Join("\n", errors.Where(e => !string.IsNullOrWhiteSpace(e)));
                        if (string.IsNullOrEmpty(finalPath))
                        {
                            throw new Exception(LocalizationManager.Format("Log.YtDlpFailed", p.ExitCode, errorMsg));
                        }
                    }
                }

                if (string.IsNullOrEmpty(finalPath) || !File.Exists(finalPath))
                {
                    var files = new DirectoryInfo(targetDir).GetFiles("*.mp4")
                        .OrderByDescending(f => f.LastWriteTime)
                        .FirstOrDefault();
                    finalPath = files?.FullName;
                }

                return finalPath;
            });
        }

        private void Log(string msg)
        {
            OnLog?.Invoke(msg);
        }
    }
}
