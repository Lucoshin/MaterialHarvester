using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;

namespace VideoToMaterial
{
    public enum LanguagePreference
    {
        Auto,
        Chinese,
        English
    }

    public static class LocalizationManager
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MaterialHarvester");

        private static readonly string LanguageFile = Path.Combine(SettingsDir, "language.txt");

        private static readonly Dictionary<string, (string Zh, string En)> Strings = new Dictionary<string, (string, string)>
        {
            ["Ui.WindowTitle"] = ("Material Harvester", "Material Harvester"),
            ["Ui.DependencyMissing"] = ("环境缺失：未检测到依赖项 FFmpeg，核心切片功能将无法使用", "Missing dependency: FFmpeg was not found. Core slicing features are unavailable."),
            ["Ui.OpenDependencyDir"] = ("打开依赖目录", "Open Dependency Folder"),
            ["Ui.QueueEmpty"] = ("队列为空", "Queue is empty"),
            ["Ui.HideLog"] = ("隐藏日志", "Hide Log"),
            ["Ui.ShowLog"] = ("显示日志", "Show Log"),
            ["Ui.ResetQueue"] = ("重置队列", "Reset Queue"),
            ["Ui.StartBatch"] = ("开始批量处理", "Start Batch"),
            ["Ui.DropTitleEmpty"] = ("拖拽视频文件到此处", "Drop video files here"),
            ["Ui.DropTitleWithCount"] = ("队列中已有 {0} 个视频", "{0} video(s) in queue"),
            ["Ui.DropSubtitle"] = ("支持多选本地视频，添加后将在此处生成队列预览", "Select multiple local videos. Queue previews will appear here."),
            ["Ui.AddLink"] = ("或 粘贴一个/多个视频链接", "or paste one/more video links"),
            ["Ui.PreviewLoading"] = ("预览生成中", "Generating preview"),
            ["Ui.RemoveVideo"] = ("移除此视频", "Remove this video"),
            ["Ui.OutputLocation"] = ("输出位置", "Output Location"),
            ["Ui.DefaultOutput"] = ("默认同级目录...", "Default: same folder..."),
            ["Ui.Browse"] = ("浏览", "Browse"),
            ["Ui.Open"] = ("打开", "Open"),
            ["Ui.OutputRule"] = ("输出规则", "Output Rule"),
            ["Ui.PathHint"] = ("自动创建: ...\\[视频名]_Scenes\\", "Auto create: ...\\[VideoName]_Scenes\\"),
            ["Ui.Audio"] = ("音频", "Audio"),
            ["Ui.Mute"] = ("静音处理", "Mute audio"),
            ["Ui.SplitMode"] = ("切片模式", "Slicing Mode"),
            ["Ui.SplitModeTooltip"] = ("选择素材切分方式：智能场景适合自动找镜头边界，固定时长适合批量生成等长片段。", "Choose how clips are generated: Smart Scene detects shot boundaries, while Fixed Duration creates equal-length clips."),
            ["Ui.SmartScene"] = ("智能场景", "Smart Scene"),
            ["Ui.SmartTooltip"] = ("根据画面变化自动识别镜头切点，并结合相似度分析减少重复片段。适合从长视频中提炼可用素材。", "Automatically detects shot cuts from visual changes and reduces repeated clips with similarity analysis."),
            ["Ui.FixedDuration"] = ("固定时长", "Fixed Duration"),
            ["Ui.FixedTooltip"] = ("按设定秒数均匀切片，不做场景识别和智能去重。适合需要固定长度素材的批量处理。", "Splits evenly by seconds without scene detection or deduplication. Useful for fixed-length batch output."),
            ["Ui.Sensitivity"] = ("灵敏度", "Sensitivity"),
            ["Ui.SensitivityTooltip"] = ("控制智能场景对画面变化的敏感程度。数值越高越容易切出细小变化；切得太碎就调低，漏切就调高。", "Controls how sensitive Smart Scene is to visual changes. Increase for more cuts, decrease if clips are too fragmented."),
            ["Ui.SensitivitySliderTooltip"] = ("推荐 0.75。提高会检测更多镜头变化，降低会合并更多相近片段。", "Recommended: 0.75. Increase to detect more shot changes; decrease to merge similar segments."),
            ["Ui.DurationLabel"] = ("时长(秒)", "Duration (sec)"),
            ["Ui.FixedDurationLabel"] = ("每段时长(秒)", "Clip Duration (sec)"),
            ["Ui.MinimumDurationLabel"] = ("最小时长(秒)", "Minimum Duration (sec)"),
            ["Ui.Threads"] = ("并发数", "Concurrency"),
            ["Ui.Detector"] = ("检测器", "Detector"),
            ["Ui.ContentDetector"] = ("ContentDetector 内容变化", "ContentDetector - Content Change"),
            ["Ui.AdaptiveDetector"] = ("AdaptiveDetector 自适应智能", "AdaptiveDetector - Adaptive Smart"),
            ["Ui.RunLog"] = ("运行日志", "Run Log"),
            ["Ui.Language"] = ("语言", "Language"),
            ["Ui.LanguageAuto"] = ("跟随系统", "System"),
            ["Ui.LanguageZh"] = ("中文", "Chinese"),
            ["Ui.LanguageEn"] = ("English", "English"),
            ["Ui.SelectVideoFilter"] = ("视频文件|*.mp4;*.mkv;*.avi;*.mov;*.flv;*.wmv", "Video files|*.mp4;*.mkv;*.avi;*.mov;*.flv;*.wmv"),
            ["Ui.AddNetworkVideo"] = ("添加网络视频", "Add Network Video"),
            ["Ui.PasteVideoLinks"] = ("粘贴视频链接（支持多行或空格分隔）", "Paste video links (multiple lines or spaces supported)"),
            ["Ui.Cancel"] = ("取消", "Cancel"),
            ["Ui.AddAndParse"] = ("添加并解析", "Add and Parse"),
            ["Ui.SelectOutputDir"] = ("选择输出目录", "Select Output Folder"),
            ["Ui.MissingComponent"] = ("缺失组件", "Missing Component"),
            ["Ui.CannotStart"] = ("无法开始", "Cannot Start"),
            ["Ui.InvalidParameter"] = ("参数无效", "Invalid Parameter"),
            ["Ui.EngineReady"] = ("引擎就绪：{0}", "Engine ready: {0}"),
            ["Ui.AddedFilesStatus"] = ("已添加 {0} 个文件", "{0} file(s) added"),
            ["Log.FFmpegMissing"] = ("[错误] 未找到 ffmpeg.exe，请将 ffmpeg.exe 放到程序目录或构建输出目录。", "[Error] ffmpeg.exe was not found. Put it in the app folder or build output folder."),
            ["Log.YtDlpMissing"] = ("[提示] 未找到 yt-dlp.exe，链接下载功能将不可用。", "[Notice] yt-dlp.exe was not found. Link download will be unavailable."),
            ["Log.EncoderDetectFailed"] = ("[初始化错误] 编码器检测失败: {0}", "[Init Error] Encoder detection failed: {0}"),
            ["Log.SystemConfig"] = ("[系统配置] {0}", "[System] {0}"),
            ["Log.ThreadSuggestion"] = ("[并发建议] {0} 路并行 {1}", "[Concurrency] {0} parallel task(s) {1}"),
            ["Log.AddedFiles"] = ("> 已添加 {0} 个文件。当前队列总数: {1}", "> Added {0} file(s). Current queue total: {1}"),
            ["Log.RemovedFile"] = ("> 已移除：{0}。当前队列总数: {1}", "> Removed: {0}. Current queue total: {1}"),
            ["Log.ThumbnailSkipped"] = ("> 缩略图跳过：未找到 ffmpeg.exe", "> Thumbnail skipped: ffmpeg.exe not found"),
            ["Log.ThumbnailFailed"] = ("> 缩略图生成失败：{0}", "> Thumbnail generation failed: {0}"),
            ["Log.FFmpegProcessFailed"] = ("FFmpeg 进程启动失败", "Failed to start FFmpeg process"),
            ["Log.MissingYtDlpMessage"] = ("未找到 yt-dlp.exe，请下载后放到程序运行目录。", "yt-dlp.exe was not found. Download it and put it in the app folder."),
            ["Log.DownloadCommand"] = ("执行下载指令: {0} {1}", "Running download command: {0} {1}"),
            ["Log.YtDlpStartFailed"] = ("无法启动 yt-dlp 进程。", "Failed to start yt-dlp process."),
            ["Log.YtDlpFailed"] = ("yt-dlp 失败 (代码 {0}):\n{1}", "yt-dlp failed (code {0}):\n{1}"),
            ["Log.DownloadParsing"] = (">>> 正在解析并下载 {0} 个链接", ">>> Parsing and downloading {0} link(s)"),
            ["Log.DownloadSuccess"] = ("> 下载成功: {0}", "> Download succeeded: {0}"),
            ["Log.DownloadFailed"] = ("[错误] 下载失败，请检查链接或网络。", "[Error] Download failed. Check the link or network."),
            ["Log.Fatal"] = ("[严重错误] {0}", "[Fatal Error] {0}"),
            ["Log.EncoderNotReady"] = ("编码器尚未初始化，请确认 ffmpeg.exe 可用。", "Encoder is not initialized. Confirm ffmpeg.exe is available."),
            ["Log.Processing"] = (">>> 正在处理 [{0}/{1}]: {2}", ">>> Processing [{0}/{1}]: {2}"),
            ["Log.AllDone"] = ("> 全部任务完成。", "> All tasks completed."),
            ["Log.InvalidInteger"] = ("{0} 必须是 {1}-{2} 的整数。", "{0} must be an integer from {1} to {2}."),
            ["Log.InvalidNumber"] = ("{0} 必须是 {1}-{2} 的数字。", "{0} must be a number from {1} to {2}."),
            ["Log.QueueCleared"] = ("> 任务队列已清空。", "> Task queue cleared."),
            ["Log.DependencyHelp"] = ("> 请将 ffmpeg.exe、ffprobe.exe 和 yt-dlp.exe 放入程序目录后重启应用。", "> Put ffmpeg.exe, ffprobe.exe and yt-dlp.exe in the app folder, then restart the app."),
            ["Param.Threads"] = ("并发数", "Concurrency"),
            ["Param.Duration"] = ("时长", "Duration")
        };

        public static LanguagePreference Preference { get; private set; } = LanguagePreference.Auto;

        public static bool UseChinese
        {
            get
            {
                if (Preference == LanguagePreference.Chinese) return true;
                if (Preference == LanguagePreference.English) return false;
                return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
            }
        }

        public static void Initialize()
        {
            Preference = LoadPreference();
            ApplyResources();
        }

        public static void SetPreference(LanguagePreference preference, bool save)
        {
            Preference = preference;
            if (save) SavePreference(preference);
            ApplyResources();
        }

        public static string Text(string key)
        {
            return Strings.TryGetValue(key, out var value)
                ? (UseChinese ? value.Zh : value.En)
                : key;
        }

        public static string Format(string key, params object[] args)
        {
            return string.Format(CultureInfo.CurrentCulture, Text(key), args);
        }

        private static void ApplyResources()
        {
            if (Application.Current == null) return;

            foreach (var pair in Strings)
            {
                Application.Current.Resources[pair.Key] = UseChinese ? pair.Value.Zh : pair.Value.En;
            }
        }

        private static LanguagePreference LoadPreference()
        {
            try
            {
                if (!File.Exists(LanguageFile)) return LanguagePreference.Auto;
                return File.ReadAllText(LanguageFile).Trim().ToLowerInvariant() switch
                {
                    "zh" => LanguagePreference.Chinese,
                    "en" => LanguagePreference.English,
                    _ => LanguagePreference.Auto
                };
            }
            catch
            {
                return LanguagePreference.Auto;
            }
        }

        private static void SavePreference(LanguagePreference preference)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                string value = preference == LanguagePreference.Chinese ? "zh" :
                    preference == LanguagePreference.English ? "en" : "auto";
                File.WriteAllText(LanguageFile, value);
            }
            catch
            {
                // Language selection is non-critical; ignore persistence failures.
            }
        }
    }
}
