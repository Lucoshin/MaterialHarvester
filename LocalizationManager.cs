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
            ["Ui.WorkspaceSlice"] = ("素材切片", "Clip Extraction"),
            ["Ui.WorkspaceCompose"] = ("随机成片", "Random Composer"),
            ["Ui.CompositionTitle"] = ("随机成片工作台", "Random Composition Studio"),
            ["Ui.CompositionSubtitle"] = ("平衡抽取短视频素材，单条成片内部绝不重复", "Balance short clips with no duplicate inside an output"),
            ["Ui.CompositionReady"] = ("等待素材", "Waiting for clips"),
            ["Ui.ClipLibrary"] = ("短视频素材库", "Short Clip Library"),
            ["Ui.ClipLibraryEmpty"] = ("尚未导入素材", "No clips imported"),
            ["Ui.AddClipFiles"] = ("添加文件", "Add Files"),
            ["Ui.AddClipFolder"] = ("添加文件夹", "Add Folder"),
            ["Ui.CompositionDropTitle"] = ("拖入短视频或素材文件夹", "Drop short clips or a folder"),
            ["Ui.CompositionDropHint"] = ("支持 MP4、MKV、AVI、MOV、FLV、WMV", "Supports MP4, MKV, AVI, MOV, FLV and WMV"),
            ["Ui.CompositionSettings"] = ("组合设置", "Composition Settings"),
            ["Ui.OutputVideoCount"] = ("生成视频数量", "Output Videos"),
            ["Ui.ClipsPerVideo"] = ("每条片段数量", "Clips per Video"),
            ["Ui.RandomSeed"] = ("随机种子", "Random Seed"),
            ["Ui.NoDuplicateGuarantee"] = ("✓ 单条成片内强制无重复；跨成片按使用次数和片段搭配进行平衡。", "✓ No duplicate clip inside one output; usage and clip pairings are balanced across outputs."),
            ["Ui.CompositionOutput"] = ("输出与编码", "Output and Encoding"),
            ["Ui.AverageReuse"] = ("平均素材复用", "Average Reuse"),
            ["Ui.TotalOutputDuration"] = ("预计成片总时长", "Estimated Output Duration"),
            ["Ui.ClearClipLibrary"] = ("清空素材库", "Clear Library"),
            ["Ui.CheckCompositionPlan"] = ("检查方案", "Check Plan"),
            ["Ui.StartComposition"] = ("开始随机成片", "Start Composition"),
            ["Ui.CancelComposition"] = ("取消任务", "Cancel"),
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
            ["Ui.OutputRuleSingleFolder"] = ("全部放入同一文件夹（默认）", "Put all clips in one folder (default)"),
            ["Ui.OutputRulePerVideo"] = ("按视频创建子文件夹", "Create a folder for each video"),
            ["Ui.PathHintSingleFolder"] = ("文件名自动添加源视频名前缀，避免批量切片重名", "Source video names prefix clip files to avoid batch collisions"),
            ["Ui.PathHintPerVideo"] = ("自动创建: ...\\[视频名]_Scenes\\", "Auto create: ...\\[VideoName]_Scenes\\"),
            ["Ui.Audio"] = ("音频", "Audio"),
            ["Ui.Mute"] = ("静音处理", "Mute audio"),
            ["Ui.MediaProcessing"] = ("音频与画面", "Audio and Video"),
            ["Ui.RemoveSubtitles"] = ("AI 去字幕", "AI subtitle removal"),
            ["Ui.AiEngineStatus"] = ("AI: {0}", "AI: {0}"),
            ["Ui.AiEngineChecking"] = ("AI: 检测中...", "AI: checking..."),
            ["Ui.AiEngineUnavailable"] = ("AI: 未安装", "AI: not installed"),
            ["Ui.SubtitleRegion"] = ("选择区域", "Select region"),
            ["Ui.SubtitleScope"] = ("检测范围", "Detection scope"),
            ["Ui.SubtitleScopeAuto"] = ("自动检测", "Automatic"),
            ["Ui.SubtitleScopeRegion"] = ("框选区域", "Selected region"),
            ["Ui.StartSubtitleSelection"] = ("开始框选", "Draw region"),
            ["Ui.SubtitleScopeAutoTooltip"] = ("自动检测整幅画面中的文字，使用方便，但可能误处理路牌、标题或水印。", "Detect text across the entire frame. Convenient, but signs, titles, or watermarks may also be processed."),
            ["Ui.SubtitleScopeRegionTooltip"] = ("先添加视频，再框选字幕可能出现的范围；只在框内检测文字，可降低误删。", "Add a video, then draw the area where subtitles may appear. Text is detected only inside that area."),
            ["Ui.SubtitleRegionTitle"] = ("选择字幕区域", "Select Subtitle Region"),
            ["Ui.SubtitleRegionHint"] = ("拖动框选字幕可能出现的范围。AI 只会检测并修复框内文字，不会裁剪画面。", "Drag around the area where subtitles may appear. AI only detects and repairs text inside the box; it does not crop the video."),
            ["Ui.Confirm"] = ("确定", "Confirm"),
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
            ["Ui.FixedSelectionMode"] = ("取片方式", "Clip Selection"),
            ["Ui.FixedSelectionTooltip"] = ("选择固定时长模式要保留的位置；自动全部会从头到尾连续切片。", "Choose which positions to keep in Fixed Duration mode. Automatic slices continuously from start to finish."),
            ["Ui.FixedSelectionAutomatic"] = ("自动全部（默认）", "Automatic - All (default)"),
            ["Ui.FixedSelectionStart"] = ("1 段：开头", "1 Clip: Start"),
            ["Ui.FixedSelectionStartEnd"] = ("2 段：开头、结尾", "2 Clips: Start and End"),
            ["Ui.FixedSelectionStartMiddleEnd"] = ("3 段：开头、中间、结尾", "3 Clips: Start, Middle and End"),
            ["Ui.MinimumDurationLabel"] = ("最小时长(秒)", "Minimum Duration (sec)"),
            ["Ui.Threads"] = ("并发数", "Concurrency"),
            ["Ui.Detector"] = ("检测器", "Detector"),
            ["Ui.DetectorTooltip"] = ("选择智能场景模式使用的场景检测器。固定时长模式不会使用检测器。", "Choose the scene detector used by Smart Scene mode. Fixed Duration mode does not use a detector."),
            ["Ui.ContentDetector"] = ("ContentDetector 内容变化", "ContentDetector - Content Change"),
            ["Ui.ContentDetectorTooltip"] = ("基础内容变化检测：基于 FFmpeg scene 阈值识别明显镜头切换，速度快、结果稳定，适合大多数素材。", "Basic content-change detection: uses FFmpeg scene thresholding to find obvious shot changes. Fast, stable, and suitable for most footage."),
            ["Ui.AdaptiveDetector"] = ("AdaptiveDetector 自适应智能", "AdaptiveDetector - Adaptive Smart"),
            ["Ui.AdaptiveDetectorTooltip"] = ("自适应智能检测：在基础检测后补充弱切点并做合并优化，更容易找出细微转场，但处理时间会略长。", "Adaptive smart detection: supplements weak cuts after the basic pass and optimizes merging. Better for subtle transitions, with slightly longer processing time."),
            ["Ui.RunLog"] = ("运行日志", "Run Log"),
            ["Ui.Language"] = ("语言", "Language"),
            ["Ui.LanguageAuto"] = ("跟随系统", "System"),
            ["Ui.LanguageZh"] = ("中文", "Chinese"),
            ["Ui.LanguageEn"] = ("English", "English"),
            ["Ui.SelectVideoFilter"] = ("视频文件|*.mp4;*.mkv;*.avi;*.mov;*.flv;*.wmv", "Video files|*.mp4;*.mkv;*.avi;*.mov;*.flv;*.wmv"),
            ["Ui.AddNetworkVideo"] = ("添加网络视频", "Add Network Video"),
            ["Ui.PasteVideoLinks"] = ("粘贴视频链接（支持多行或空格分隔）", "Paste video links (multiple lines or spaces supported)"),
            ["Ui.Cancel"] = ("取消", "Cancel"),
            ["Ui.CancelTask"] = ("取消任务", "Cancel task"),
            ["Ui.AddAndParse"] = ("添加并解析", "Add and Parse"),
            ["Ui.AutoAddDownloadsToQueue"] = ("下载完成后自动加入任务队列", "Automatically add completed downloads to the task queue"),
            ["Ui.SelectOutputDir"] = ("选择输出目录", "Select Output Folder"),
            ["Ui.MissingComponent"] = ("缺失组件", "Missing Component"),
            ["Ui.CannotStart"] = ("无法开始", "Cannot Start"),
            ["Ui.InvalidParameter"] = ("参数无效", "Invalid Parameter"),
            ["Ui.EngineReady"] = ("引擎就绪：{0}", "Engine ready: {0}"),
            ["Ui.AddedFilesStatus"] = ("已添加 {0} 个文件", "{0} file(s) added"),
            ["Ui.CompositionClipStatus"] = ("素材库：{0} 个片段", "Library: {0} clip(s)"),
            ["Ui.CompositionLibrarySummary"] = ("{0} 个可用素材 · 单条选择 {1} 个", "{0} usable clips · {1} per output"),
            ["Ui.CompositionPlanReady"] = ("方案可生成：{0} 条视频，共 {1} 次素材引用。", "Plan ready: {0} videos and {1} clip references."),
            ["Ui.CompositionFinished"] = ("随机成片结束：成功 {0}，失败 {1}。", "Composition finished: {0} succeeded, {1} failed."),
            ["Ui.CompositionCancelled"] = ("随机成片任务已取消。", "Composition was cancelled."),
            ["Ui.CompositionNeedOutput"] = ("请先选择随机成片输出目录。", "Select an output folder for composed videos."),
            ["Ui.CompositionNeedClips"] = ("素材数量不足：当前 {0} 个，每条需要 {1} 个。", "Not enough clips: {0} available, {1} required per output."),
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
            ["Log.YtDlpOutputMissing"] = ("yt-dlp 已结束，但没有返回可确认的下载文件：\n{0}", "yt-dlp exited without reporting a verifiable output file:\n{0}"),
            ["Log.DownloadItemFailed"] = ("[下载失败 {0}/{1}] {2}", "[Download failed {0}/{1}] {2}"),
            ["Log.DownloadBatchSummary"] = ("下载批次完成：成功 {0}，失败 {1}；失败项未影响后续链接。", "Download batch finished: {0} succeeded, {1} failed; later URLs were still processed."),
            ["Log.OperationCancelled"] = ("任务已取消，相关下载/AI 子进程已停止。", "Operation cancelled; related download/AI child processes were stopped."),
            ["Log.DownloadParsing"] = (">>> 正在解析并下载 {0} 个链接", ">>> Parsing and downloading {0} link(s)"),
            ["Log.DownloadSuccess"] = ("> 下载成功: {0}", "> Download succeeded: {0}"),
            ["Log.DownloadSavedOnly"] = ("> 已保存到下载目录，未加入任务队列。", "> Saved to the download folder without adding it to the task queue."),
            ["Log.DownloadFailed"] = ("[错误] 下载失败，请检查链接或网络。", "[Error] Download failed. Check the link or network."),
            ["Log.Fatal"] = ("[严重错误] {0}", "[Fatal Error] {0}"),
            ["Log.EncoderNotReady"] = ("编码器尚未初始化，请确认 ffmpeg.exe 可用。", "Encoder is not initialized. Confirm ffmpeg.exe is available."),
            ["Log.Processing"] = (">>> 正在处理 [{0}/{1}]: {2}", ">>> Processing [{0}/{1}]: {2}"),
            ["Log.BatchVideoFailed"] = ("[视频失败 {0}/{1}] {2}：{3}。已跳过该视频，继续处理队列。", "[Video failed {0}/{1}] {2}: {3}. This video was skipped and the queue will continue."),
            ["Log.BatchFinished"] = ("> 批处理结束：成功 {0}，失败 {1}；单个失败不会中断后续视频。", "> Batch finished: {0} succeeded, {1} failed; an individual failure did not stop later videos."),
            ["Log.AllDone"] = ("> 全部任务完成。", "> All tasks completed."),
            ["Log.SubtitleBackendMissing"] = ("未找到 AI 去字幕组件。请将 video-subtitle-remover 及其 Python 环境放到 ai\\video-subtitle-remover 目录。", "AI subtitle removal component was not found. Put video-subtitle-remover and its Python environment in ai\\video-subtitle-remover."),
            ["Log.SubtitleAccelerator"] = ("> AI 推理设备：{0}", "> AI inference device: {0}"),
            ["Log.SubtitleCpuWarning"] = ("[性能提示] 当前去字幕侧车仅含 CPU 引擎；程序将使用更高效的 LaMa 动态文字修复并启用范围优化，但长视频仍无法达到 GPU 速度。CUDA/DirectML 侧车可获得显著加速。", "[Performance] This subtitle sidecar is CPU-only. The efficient LaMa dynamic-text path and range optimization are enabled, but long videos cannot match GPU speed. A CUDA/DirectML sidecar is substantially faster."),
            ["Log.SubtitleRemovalStarted"] = ("> AI 去字幕：正在使用 {0} 修复字幕区域...", "> AI subtitle removal: repairing the selected region with {0}..."),
            ["Log.SubtitleRangeOptimization"] = ("> 去字幕优化：只对 {0} 个保留时间段（共 {1:F1} 秒）执行 AI 推理。", "> Subtitle optimization: AI inference is limited to {0} retained range(s), {1:F1} seconds total."),
            ["Log.SubtitleRemovalFinished"] = ("> AI 去字幕完成，开始素材切片。", "> AI subtitle removal completed. Starting material slicing."),
            ["Log.SubtitleRegionNeedsVideo"] = ("请先添加一个视频，再选择字幕区域。", "Add a video before selecting a subtitle region."),
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
