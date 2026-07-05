using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoToMaterial
{
    public partial class MainWindow : Window
    {
        private readonly string _appDir = AppContext.BaseDirectory;
        private readonly string _ffmpegPath;
        private readonly string _ytdlpPath;
        private readonly Queue<string> _taskQueue = new Queue<string>();
        private readonly ObservableCollection<VideoQueueItem> _queuePreviewItems = new ObservableCollection<VideoQueueItem>();

        private string _outputFolderPath = "";
        private FFmpegHelper _ffmpegHelper;
        private VideoProcessorService _videoProcessor;
        private DownloadService _downloadService;
        private HardwareOptimizationService _hardwareOptimizer;
        private EncoderProfile _currentProfile;
        private bool _isBusy;
        private GridLength _lastLogRowHeight = new GridLength(1.35, GridUnitType.Star);

        public MainWindow()
        {
            InitializeComponent();

            _ffmpegPath = FindToolPath("ffmpeg.exe");
            _ytdlpPath = FindToolPath("yt-dlp.exe");

            _ffmpegHelper = new FFmpegHelper(_ffmpegPath);
            _videoProcessor = new VideoProcessorService(_ffmpegPath);
            _downloadService = new DownloadService(_ytdlpPath);
            _hardwareOptimizer = new HardwareOptimizationService();
            QueuePreview.ItemsSource = _queuePreviewItems;

            _videoProcessor.OnLog += Log;
            _videoProcessor.OnProgress += UpdateProgress;
            _downloadService.OnLog += Log;
            _downloadService.OnProgress += p => Dispatcher.Invoke(() =>
            {
                Progress.Visibility = Visibility.Visible;
                Progress.IsIndeterminate = false;
                Progress.Maximum = 100;
                Progress.Value = Math.Max(0, Math.Min(100, p));
            });

            Loaded += MainWindow_Loaded;
            UpdateSplitModeControls();
            UpdateQueueState();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            bool ffmpegMissing = !File.Exists(_ffmpegPath);
            DependencyBanner.Visibility = ffmpegMissing ? Visibility.Visible : Visibility.Collapsed;

            if (ffmpegMissing)
            {
                Log("[错误] 未找到 ffmpeg.exe，请将 ffmpeg.exe 放到程序目录或构建输出目录。");
                UpdateQueueState();
                return;
            }

            if (!File.Exists(_ytdlpPath))
            {
                Log("[提示] 未找到 yt-dlp.exe，链接下载功能将不可用。");
            }

            try
            {
                _currentProfile = await _ffmpegHelper.DetectBestEncoderAsync();
                QueueStatus.Text = $"引擎就绪：{_currentProfile.Name}";
                CalculateSafeThreads();
            }
            catch (Exception ex)
            {
                Log($"[初始化错误] 编码器检测失败: {ex.Message}");
            }

            UpdateQueueState();
        }

        private static string FindToolPath(string fileName)
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
            {
                string[] candidates =
                {
                    Path.Combine(dir, fileName),
                    Path.Combine(dir, "bin", "Release", fileName),
                    Path.Combine(dir, "bin", "Release", "net8.0-windows", fileName),
                    Path.Combine(dir, "bin", "Release", "net8.0-windows", "win-x64", fileName),
                    Path.Combine(dir, "bin", "Debug", fileName),
                    Path.Combine(dir, "bin", "Debug", "net8.0-windows", fileName)
                };

                foreach (string candidate in candidates)
                {
                    if (File.Exists(candidate)) return candidate;
                }

                dir = Directory.GetParent(dir)?.FullName;
            }

            return Path.Combine(AppContext.BaseDirectory, fileName);
        }

        private void CalculateSafeThreads()
        {
            var result = _hardwareOptimizer.CalculateOptimalThreadCount(_currentProfile);
            TxtThreads.Text = result.suggestedCount.ToString();
            Log($"[系统配置] {result.systemInfo}");
            Log($"[并发建议] {result.suggestedCount} 路并行 {result.reason}");
        }

        private void Window_DragEnter(object sender, DragEventArgs e) => ApplyFileDragEffect(e);
        private void DropZone_DragEnter(object sender, DragEventArgs e) => ApplyFileDragEffect(e);
        private void Window_Drop(object sender, DragEventArgs e) => AddDroppedFiles(e);
        private void DropZone_Drop(object sender, DragEventArgs e) => AddDroppedFiles(e);
        private void Window_PreviewDragOver(object sender, DragEventArgs e) => ApplyFileDragEffect(e);
        private void Window_PreviewDrop(object sender, DragEventArgs e) => AddDroppedFiles(e);

        private static void ApplyFileDragEffect(DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void AddDroppedFiles(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                AddFilesToQueue((string[])e.Data.GetData(DataFormats.FileDrop));
                e.Handled = true;
            }
        }

        private void DropZone_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isBusy) return;

            var dialog = new OpenFileDialog
            {
                Filter = "Video|*.mp4;*.mkv;*.avi;*.mov;*.flv;*.wmv",
                Multiselect = true
            };

            if (dialog.ShowDialog(this) == true)
            {
                AddFilesToQueue(dialog.FileNames);
            }
        }

        private void AddFilesToQueue(IEnumerable<string> files)
        {
            int added = 0;
            foreach (string file in files)
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".mp4" || ext == ".mkv" || ext == ".avi" || ext == ".mov" || ext == ".flv" || ext == ".wmv")
                {
                    _taskQueue.Enqueue(file);
                    var item = new VideoQueueItem(file);
                    _queuePreviewItems.Add(item);
                    _ = LoadThumbnailAsync(item);
                    added++;
                    if (string.IsNullOrEmpty(_outputFolderPath))
                    {
                        _outputFolderPath = Path.GetDirectoryName(file) ?? "";
                    }
                }
            }

            if (added > 0)
            {
                DropTitle.Text = $"队列中已有 {_taskQueue.Count} 个视频";
                TxtOutputDir.Text = string.IsNullOrEmpty(_outputFolderPath) ? "默认同级目录..." : _outputFolderPath;
                PathHint.Text = "自动创建: ...\\[视频名]_Scenes\\";
                UpdateSourcePreviewState();
                Log($"> 已添加 {added} 个文件。当前队列总数: {_taskQueue.Count}");
                UpdateQueueState();
            }
        }

        private void RemoveVideo_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (_isBusy) return;

            if (sender is Button button && button.DataContext is VideoQueueItem item)
            {
                _queuePreviewItems.Remove(item);
                RebuildTaskQueueFromPreview();

                if (_taskQueue.Count == 0)
                {
                    _outputFolderPath = "";
                    TxtOutputDir.Text = "默认同级目录...";
                    PathHint.Text = "";
                    DropTitle.Text = "拖拽视频文件到此处";
                }
                else
                {
                    DropTitle.Text = $"队列中已有 {_taskQueue.Count} 个视频";
                }

                UpdateSourcePreviewState();
                UpdateQueueState();
                Log($"> 已移除：{item.Name}。当前队列总数: {_taskQueue.Count}");
            }
        }

        private void RebuildTaskQueueFromPreview()
        {
            _taskQueue.Clear();
            foreach (VideoQueueItem item in _queuePreviewItems)
            {
                _taskQueue.Enqueue(item.Path);
            }
        }

        private void UpdateSourcePreviewState()
        {
            bool hasItems = _taskQueue.Count > 0;
            EmptySourcePanel.Visibility = Visibility.Visible;
            EmptySourcePanel.Opacity = hasItems ? 0.16 : 1.0;
            QueueScroll.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task LoadThumbnailAsync(VideoQueueItem item)
        {
            if (!File.Exists(_ffmpegPath))
            {
                Log($"> 缩略图跳过：未找到 ffmpeg.exe");
                return;
            }

            if (!File.Exists(item.Path)) return;

            try
            {
                string thumbDir = Path.Combine(Path.GetTempPath(), "VideoToMaterial", "Thumbnails");
                Directory.CreateDirectory(thumbDir);

                string outputPath = Path.Combine(
                    thumbDir,
                    $"{Path.GetFileNameWithoutExtension(item.Path)}_{Guid.NewGuid():N}.jpg");

                string error = await TryExtractThumbnailAsync(item.Path, outputPath, "00:00:01");
                if (!File.Exists(outputPath))
                {
                    error = await TryExtractThumbnailAsync(item.Path, outputPath, "00:00:00.1");
                }

                if (!File.Exists(outputPath))
                {
                    Log($"> 缩略图生成失败：{item.Name}{(string.IsNullOrWhiteSpace(error) ? "" : " - " + error.Trim())}");
                    return;
                }

                BitmapImage thumbnail = await Task.Run(() => LoadBitmap(outputPath));
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_queuePreviewItems.Contains(item))
                    {
                        item.Thumbnail = thumbnail;
                    }
                });
            }
            catch
            {
                Log($"> 缩略图生成失败：{item.Name}");
            }
        }

        private async Task<string> TryExtractThumbnailAsync(string videoPath, string outputPath, string seekTime)
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(seekTime);
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(videoPath);
            startInfo.ArgumentList.Add("-an");
            startInfo.ArgumentList.Add("-frames:v");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-vf");
            startInfo.ArgumentList.Add("scale=320:-2");
            startInfo.ArgumentList.Add("-q:v");
            startInfo.ArgumentList.Add("3");
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add(outputPath);

            using (var process = Process.Start(startInfo))
            {
                if (process == null) return "FFmpeg 进程启动失败";
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                return process.ExitCode == 0 ? "" : error;
            }
        }

        private static BitmapImage LoadBitmap(string path)
        {
            var bitmap = new BitmapImage();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
            }
            return bitmap;
        }

        private async void AddLink_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            if (!File.Exists(_ytdlpPath))
            {
                MessageBox.Show(this, "未找到 yt-dlp.exe，请下载后放到程序运行目录。", "缺失组件", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string initialUrl = Clipboard.ContainsText() && Clipboard.GetText().Contains("http", StringComparison.OrdinalIgnoreCase)
                ? Clipboard.GetText()
                : "";

            string urlText = PromptForUrls(initialUrl);
            List<string> urls = ExtractUrls(urlText);
            if (urls.Count == 0) return;

            SetBusy(true);
            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = true;
            Log($">>> 正在解析并下载 {urls.Count} 个链接");

            string downloadDir = string.IsNullOrEmpty(_outputFolderPath) ? Path.Combine(_appDir, "Downloads") : _outputFolderPath;
            Directory.CreateDirectory(downloadDir);

            try
            {
                int index = 0;
                foreach (string url in urls)
                {
                    index++;
                    Log($">>> [{index}/{urls.Count}] {url}");
                    string downloadedFile = await _downloadService.DownloadVideoAsync(url, downloadDir);
                    if (!string.IsNullOrEmpty(downloadedFile) && File.Exists(downloadedFile))
                    {
                        Log($"> 下载成功: {Path.GetFileName(downloadedFile)}");
                        AddFilesToQueue(new[] { downloadedFile });
                    }
                    else
                    {
                        Log("[错误] 下载失败，请检查链接或网络。");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[严重错误] {ex.Message}");
            }
            finally
            {
                Progress.IsIndeterminate = false;
                Progress.Visibility = Visibility.Collapsed;
                SetBusy(false);
            }
        }

        private string PromptForUrls(string initialUrl)
        {
            Window prompt = new Window
            {
                Owner = this,
                Width = 660,
                Height = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
            };

            Border shell = new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("PanelBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrushSoft"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(18)
            };

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            DockPanel header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            TextBlock title = new TextBlock
            {
                Text = "添加网络视频",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
            };
            Button close = new Button
            {
                Content = "×",
                Style = (Style)FindResource("LinkButton"),
                Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush"),
                FontSize = 20,
                Width = 28,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            close.Click += (s, e) => prompt.Close();
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Add(close);
            header.Children.Add(title);

            TextBlock label = new TextBlock 
            { 
                Text = "粘贴视频链接（支持多行或空格分隔）", 
                FontSize = 13, 
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
                Margin = new Thickness(0, 0, 0, 10) 
            };
            TextBox input = new TextBox 
            { 
                Text = initialUrl, 
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 16),
                MinHeight = 160
            };
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button cancel = new Button { Content = "取消", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 10, 0), MinWidth = 86 };
            Button ok = new Button { Content = "添加并解析", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(18, 8, 18, 8), MinWidth = 118 };

            string result = null;
            cancel.Click += (s, e) => prompt.Close();
            ok.Click += (s, e) => { result = input.Text; prompt.Close(); };
            input.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    result = input.Text;
                    prompt.Close();
                }
            };

            actions.Children.Add(cancel);
            actions.Children.Add(ok);
            Grid.SetRow(header, 0);
            Grid.SetRow(label, 1);
            Grid.SetRow(input, 2);
            Grid.SetRow(actions, 3);
            root.Children.Add(header);
            root.Children.Add(label);
            root.Children.Add(input);
            root.Children.Add(actions);
            shell.Child = root;
            prompt.Content = shell;
            prompt.ShowDialog();

            return result;
        }

        private List<string> ExtractUrls(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();
            return Regex.Matches(text, @"https?://[^\s""'<>]+")
                .Cast<Match>()
                .Select(m => m.Value.Trim().TrimEnd(',', ';'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择输出目录"
            };

            if (dialog.ShowDialog(this) == true)
            {
                _outputFolderPath = dialog.FolderName;
                TxtOutputDir.Text = _outputFolderPath;
                PathHint.Text = _taskQueue.Count > 0 ? "自动创建: ...\\[视频名]_Scenes\\" : "";
            }
        }

        private void OpenOutput_Click(object sender, RoutedEventArgs e)
        {
            if (!Directory.Exists(_outputFolderPath)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", _outputFolderPath) { UseShellExecute = true });
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_taskQueue.Count == 0 || _isBusy) return;
            if (_currentProfile == null)
            {
                MessageBox.Show(this, "编码器尚未初始化，请确认 ffmpeg.exe 可用。", "无法开始", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryReadInt(TxtThreads.Text, 1, 16, out int maxThreads, "并发数")) return;
            if (!TryReadDouble(TxtDuration.Text, 1, 600, out double fixedDuration, "时长")) return;

            var options = new ProcessingOptions
            {
                SplitMode = FixedModeRadio.IsChecked == true ? SplitMode.FixedDuration : SplitMode.SmartScene,
                SceneDetectionEngine = DetectorCombo.SelectedIndex == 1 ? SceneDetectionEngine.AdaptiveDetector : SceneDetectionEngine.ContentDetector,
                FixedDurationSeconds = fixedDuration,
                MinimumSceneDurationSeconds = fixedDuration,
                Sensitivity = SensitivitySlider.Value,
                Mute = ChkMute.IsChecked == true,
                MaxThreads = maxThreads,
                Profile = _currentProfile
            };

            SetBusy(true);
            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = false;
            Progress.Value = 0;
            BtnOpenDir.Visibility = Visibility.Collapsed;

            int total = _taskQueue.Count;
            int count = 0;

            try
            {
                while (_taskQueue.Count > 0)
                {
                    string video = _taskQueue.Dequeue();
                    if (_queuePreviewItems.Count > 0)
                    {
                        _queuePreviewItems.RemoveAt(0);
                    }
                    UpdateSourcePreviewState();
                    count++;
                    string baseOutput = string.IsNullOrEmpty(_outputFolderPath) ? Path.GetDirectoryName(video) ?? _appDir : _outputFolderPath;
                    string name = Path.GetFileNameWithoutExtension(video);
                    string outDir = Path.Combine(baseOutput, name + "_Scenes");
                    Directory.CreateDirectory(outDir);
                    _outputFolderPath = baseOutput;

                    Log($">>> 正在处理 [{count}/{total}]: {name}");
                    await _videoProcessor.ProcessSingleVideoAsync(video, outDir, options);
                    UpdateQueueState();
                }

                SystemSounds.Exclamation.Play();
                BtnOpenDir.Visibility = Directory.Exists(_outputFolderPath) ? Visibility.Visible : Visibility.Collapsed;
                DropTitle.Text = "拖拽视频文件到此处";
                PathHint.Text = "";
                Log("> 全部任务完成。");
            }
            catch (Exception ex)
            {
                Log($"[Error] {ex.Message}");
            }
            finally
            {
                _taskQueue.Clear();
                _queuePreviewItems.Clear();
                SetBusy(false);
                Progress.Visibility = Visibility.Collapsed;
                UpdateSourcePreviewState();
                UpdateQueueState();
            }
        }

        private bool TryReadInt(string text, int min, int max, out int value, string label)
        {
            if (int.TryParse(text, out value) && value >= min && value <= max) return true;
            MessageBox.Show(this, $"{label} 必须是 {min}-{max} 的整数。", "参数无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private bool TryReadDouble(string text, double min, double max, out double value, string label)
        {
            if (double.TryParse(text, out value) && value >= min && value <= max) return true;
            MessageBox.Show(this, $"{label} 必须是 {min}-{max} 的数字。", "参数无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private void SplitMode_Checked(object sender, RoutedEventArgs e) => UpdateSplitModeControls();

        private void UpdateSplitModeControls()
        {
            if (TxtDuration == null || SensitivitySlider == null || DetectorCombo == null) return;

            bool fixedMode = FixedModeRadio.IsChecked == true;
            TxtDuration.IsEnabled = true;
            SensitivitySlider.IsEnabled = !fixedMode;
            DetectorCombo.IsEnabled = !fixedMode;
            DurationLabel.Text = fixedMode ? "每段时长(秒)" : "最小时长(秒)";
            SensitivityLabel.Foreground = fixedMode
                ? (System.Windows.Media.Brush)FindResource("DimTextBrush")
                : (System.Windows.Media.Brush)FindResource("MutedTextBrush");
            DurationLabel.Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush");
        }

        private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SensitivityValue != null)
            {
                SensitivityValue.Text = e.NewValue.ToString("0.00");
            }
        }

        private void ToggleLog_Click(object sender, RoutedEventArgs e)
        {
            bool show = LogDrawer.Visibility != Visibility.Visible;
            if (show)
            {
                LogDrawer.Visibility = Visibility.Visible;
                LogSplitter.Visibility = Visibility.Visible;
                LogRow.Height = _lastLogRowHeight.Value > 0 ? _lastLogRowHeight : new GridLength(1.35, GridUnitType.Star);
                BtnToggleLog.Content = "隐藏日志";
            }
            else
            {
                if (LogRow.Height.Value > 0) _lastLogRowHeight = LogRow.Height;
                LogDrawer.Visibility = Visibility.Collapsed;
                LogSplitter.Visibility = Visibility.Collapsed;
                LogRow.Height = new GridLength(0);
                BtnToggleLog.Content = "显示日志";
            }
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            _taskQueue.Clear();
            _queuePreviewItems.Clear();
            _outputFolderPath = "";
            TxtOutputDir.Text = "默认同级目录...";
            PathHint.Text = "";
            DropTitle.Text = "拖拽视频文件到此处";
            Progress.Value = 0;
            Progress.Visibility = Visibility.Collapsed;
            BtnOpenDir.Visibility = Visibility.Collapsed;
            UpdateSourcePreviewState();
            Log("> 任务队列已清空。");
            UpdateQueueState();
        }

        private void FixDependency_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", AppContext.BaseDirectory) { UseShellExecute = true });
            Log("> 请将 ffmpeg.exe、ffprobe.exe 和 yt-dlp.exe 放入程序目录后重启应用。");
        }

        private void SetBusy(bool busy)
        {
            _isBusy = busy;
            DropZone.IsEnabled = !busy;
            SmartModeRadio.IsEnabled = !busy;
            FixedModeRadio.IsEnabled = !busy;
            TxtDuration.IsEnabled = !busy;
            SensitivitySlider.IsEnabled = !busy && SmartModeRadio.IsChecked == true;
            DetectorCombo.IsEnabled = !busy && SmartModeRadio.IsChecked == true;
            TxtThreads.IsEnabled = !busy;
            ChkMute.IsEnabled = !busy;
            BtnReset.IsEnabled = !busy;
            BtnStart.IsEnabled = !busy && _taskQueue.Count > 0 && File.Exists(_ffmpegPath);
        }

        private void UpdateQueueState()
        {
            BtnStart.IsEnabled = !_isBusy && _taskQueue.Count > 0 && File.Exists(_ffmpegPath);
            QueueStatus.Text = _taskQueue.Count > 0
                ? $"已添加 {_taskQueue.Count} 个文件"
                : (_currentProfile != null ? $"引擎就绪：{_currentProfile.Name}" : "队列为空");
        }

        private void UpdateProgress(int current, int total)
        {
            Dispatcher.Invoke(() =>
            {
                Progress.Visibility = Visibility.Visible;
                Progress.IsIndeterminate = false;
                Progress.Maximum = Math.Max(1, total);
                Progress.Value = Math.Max(0, Math.Min(current, Progress.Maximum));
            });
        }

        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                TxtLog.AppendText(message + Environment.NewLine);
                TxtLog.ScrollToEnd();
            });
        }

        private class VideoQueueItem : INotifyPropertyChanged
        {
            public string Path { get; }
            public string Name { get; }
            public string Extension { get; }

            private ImageSource _thumbnail;
            public ImageSource Thumbnail
            {
                get => _thumbnail;
                set
                {
                    if (_thumbnail == value) return;
                    _thumbnail = value;
                    OnPropertyChanged();
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            public VideoQueueItem(string path)
            {
                Path = path;
                Name = System.IO.Path.GetFileName(path);
                Extension = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            }

            private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}


