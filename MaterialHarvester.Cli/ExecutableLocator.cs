namespace MaterialHarvester.Cli;

public static class ExecutableLocator
{
    public static string ResolveFfmpeg(string? applicationDirectory = null)
    {
        string? configured = Environment.GetEnvironmentVariable("MATERIAL_HARVESTER_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return RequireFfmpegPair(configured);
        }

        string baseDirectory = applicationDirectory ?? AppContext.BaseDirectory;
        string bundled = Path.Combine(baseDirectory, "ffmpeg.exe");
        if (File.Exists(bundled)) return RequireFfmpegPair(bundled);

        string? path = Environment.GetEnvironmentVariable("PATH");
        foreach (string directory in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = Path.Combine(directory.Trim(), "ffmpeg.exe"); }
            catch (ArgumentException) { continue; }
            if (File.Exists(candidate)) return RequireFfmpegPair(candidate);
        }

        throw new MissingDependencyException(
            "未找到 ffmpeg.exe。请将 ffmpeg.exe 与 ffprobe.exe 放在视频引擎目录中。");
    }

    public static string ResolveFfprobe(string ffmpegPath)
    {
        string fullFfmpegPath = Path.GetFullPath(ffmpegPath);
        string ffprobePath = Path.Combine(Path.GetDirectoryName(fullFfmpegPath)!, "ffprobe.exe");
        if (!File.Exists(ffprobePath))
        {
            throw new MissingDependencyException(
                $"未找到与 ffmpeg.exe 配套的 ffprobe.exe：{ffprobePath}");
        }
        return ffprobePath;
    }

    private static string RequireFfmpegPair(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new MissingDependencyException($"配置的 ffmpeg.exe 不存在：{fullPath}");
        ResolveFfprobe(fullPath);
        return fullPath;
    }
}
