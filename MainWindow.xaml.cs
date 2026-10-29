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
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
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
        private readonly ObservableCollection<CompositionClipItem> _compositionClips = new ObservableCollection<CompositionClipItem>();
        private readonly UserSettingsStore _settingsStore = new UserSettingsStore();

        private string _outputFolderPath = "";
        private string _compositionOutputFolderPath = "";
        private FFmpegHelper _ffmpegHelper;
        private VideoProcessorService _videoProcessor;
        private DownloadService _downloadService;
        private PlatformLoginService _platformLoginService;
        private HardwareOptimizationService _hardwareOptimizer;
        private VideoComposerService _videoComposer;
        private EncoderProfile _currentProfile;
        private bool _isBusy;
        private bool _isApplyingLanguage;
        private SubtitleRegion _subtitleRegion = SubtitleRegion.Default;
        private GridLength _lastLogRowHeight = new GridLength(1.35, GridUnitType.Star);
        private CancellationTokenSource _compositionCancellation;
        private CancellationTokenSource _operationCancellation;
        private int _subtitleProbeGeneration;
        private bool _subtitleRemovalAvailable;
        private bool _hasPersistedSettings;
        private DownloadCookieSource _downloadCookieSource;
        private string _downloadCookieFilePath = "";

        public MainWindow()
        {
            InitializeComponent();

            _ffmpegPath = FindToolPath("ffmpeg.exe");
            _ytdlpPath = FindToolPath("yt-dlp.exe");

            _ffmpegHelper = new FFmpegHelper(_ffmpegPath);
            _videoProcessor = new VideoProcessorService(_ffmpegPath);
            _downloadService = new DownloadService(_ytdlpPath, _ffmpegPath);
            _platformLoginService = new PlatformLoginService();
            _hardwareOptimizer = new HardwareOptimizationService();
            _videoComposer = new VideoComposerService(_ffmpegPath);
            QueuePreview.ItemsSource = _queuePreviewItems;
            CompositionLibraryList.ItemsSource = _compositionClips;
            LoadUserSettings();
            SelectLanguagePreference(LocalizationManager.Preference);

            _videoProcessor.OnLog += Log;
            _videoProcessor.OnProgress += UpdateProgress;
            _downloadService.OnLog += Log;
            _videoComposer.OnLog += Log;
            _videoComposer.OnProgress += UpdateCompositionProgress;
            _videoComposer.OnStageProgress += UpdateCompositionStageProgress;
            _downloadService.OnProgress += p => Dispatcher.Invoke(() =>
            {
                Progress.Visibility = Visibility.Visible;
                Progress.IsIndeterminate = false;
                Progress.Maximum = 100;
                Progress.Value = Math.Max(0, Math.Min(100, p));
            });

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            UpdateSplitModeControls();
            UpdateQueueState();
            UpdateCompositionState();
        }

        private static string L(string key) => LocalizationManager.Text(key);
        private static string LF(string key, params object[] args) => LocalizationManager.Format(key, args);

        private void LoadUserSettings()
        {
            _hasPersistedSettings = _settingsStore.Exists;
            UserSettings settings = _settingsStore.Load();

            ComposeWorkspaceRadio.IsChecked = settings.UseCompositionWorkspace;
            SliceWorkspaceRadio.IsChecked = !settings.UseCompositionWorkspace;
            FixedModeRadio.IsChecked = settings.SplitMode == SplitMode.FixedDuration;
            SmartModeRadio.IsChecked = settings.SplitMode != SplitMode.FixedDuration;
            FixedClipSelectionCombo.SelectedIndex = (int)settings.FixedClipSelectionMode;
            DetectorCombo.SelectedIndex = settings.SceneDetectionEngine == SceneDetectionEngine.AdaptiveDetector ? 1 : 0;
            SensitivitySlider.Value = settings.Sensitivity;
            TxtDuration.Text = settings.DurationSeconds.ToString("0.##", CultureInfo.CurrentCulture);
            TxtThreads.Text = settings.MaxThreads.ToString(CultureInfo.CurrentCulture);
            ChkMute.IsChecked = settings.Mute;
            ChkRemoveSubtitles.IsChecked = settings.RemoveSubtitles;
            AutomaticSubtitleScopeRadio.IsChecked = settings.AutomaticSubtitleScope;
            RegionSubtitleScopeRadio.IsChecked = !settings.AutomaticSubtitleScope;
            _subtitleRegion = new SubtitleRegion(
                settings.SubtitleLeft,
                settings.SubtitleTop,
                settings.SubtitleRight,
                settings.SubtitleBottom);
            _outputFolderPath = settings.OutputFolderPath;
            TxtOutputDir.Text = string.IsNullOrWhiteSpace(_outputFolderPath) ? L("Ui.DefaultOutput") : _outputFolderPath;
            PerVideoFolderOutputRadio.IsChecked = settings.BatchOutputRule == BatchOutputRule.PerVideoSubfolder;
            SingleFolderOutputRadio.IsChecked = settings.BatchOutputRule != BatchOutputRule.PerVideoSubfolder;

            TxtCompositionOutputCount.Text = settings.CompositionOutputCount.ToString(CultureInfo.CurrentCulture);
            TxtCompositionClipsPerOutput.Text = settings.CompositionClipsPerOutput.ToString(CultureInfo.CurrentCulture);
            TxtCompositionSeed.Text = settings.CompositionSeed.ToString(CultureInfo.CurrentCulture);
            TxtCompositionThreads.Text = settings.CompositionThreads.ToString(CultureInfo.CurrentCulture);
            ChkCompositionMute.IsChecked = settings.CompositionMute;
            _compositionOutputFolderPath = settings.CompositionOutputFolderPath;
            _downloadCookieSource = settings.DownloadCookieSource;
            _downloadCookieFilePath = settings.DownloadCookieFilePath;
            TxtCompositionOutputDir.Text = string.IsNullOrWhiteSpace(_compositionOutputFolderPath)
                ? L("Ui.SelectOutputDir")
                : _compositionOutputFolderPath;
            UpdateOutputRuleHint();
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            _compositionCancellation?.Cancel();
            _videoComposer?.StopActiveProcesses();
            _operationCancellation?.Cancel();
            _settingsStore.Save(new UserSettings
            {
                UseCompositionWorkspace = ComposeWorkspaceRadio.IsChecked == true,
                SplitMode = FixedModeRadio.IsChecked == true ? SplitMode.FixedDuration : SplitMode.SmartScene,
                FixedClipSelectionMode = ReadFixedClipSelectionMode(),
                SceneDetectionEngine = DetectorCombo.SelectedIndex == 1
                    ? SceneDetectionEngine.AdaptiveDetector
                    : SceneDetectionEngine.ContentDetector,
                Sensitivity = SensitivitySlider.Value,
                DurationSeconds = ReadDoubleOrDefault(TxtDuration.Text, 10),
                MaxThreads = ReadIntOrDefault(TxtThreads.Text, 3),
                Mute = ChkMute.IsChecked == true,
                RemoveSubtitles = ChkRemoveSubtitles.IsChecked == true,
                AutomaticSubtitleScope = AutomaticSubtitleScopeRadio.IsChecked == true,
                SubtitleLeft = _subtitleRegion.Left,
                SubtitleTop = _subtitleRegion.Top,
                SubtitleRight = _subtitleRegion.Right,
                SubtitleBottom = _subtitleRegion.Bottom,
                OutputFolderPath = _outputFolderPath,
                BatchOutputRule = PerVideoFolderOutputRadio.IsChecked == true
                    ? BatchOutputRule.PerVideoSubfolder
                    : BatchOutputRule.SingleFolder,
                CompositionOutputCount = ReadIntOrDefault(TxtCompositionOutputCount.Text, 100),
                CompositionClipsPerOutput = ReadIntOrDefault(TxtCompositionClipsPerOutput.Text, 50),
                CompositionSeed = ReadIntOrDefault(TxtCompositionSeed.Text, 20260813),
                CompositionThreads = ReadIntOrDefault(TxtCompositionThreads.Text, 2),
                CompositionMute = ChkCompositionMute.IsChecked == true,
                CompositionOutputFolderPath = _compositionOutputFolderPath,
                DownloadCookieSource = _downloadCookieSource,
                DownloadCookieFilePath = _downloadCookieFilePath
            });
        }

        private static int ReadIntOrDefault(string value, int fallback) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out int result) ? result : fallback;

        private static double ReadDoubleOrDefault(string value, double fallback) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out double result) ? result : fallback;

        private void OutputRule_Checked(object sender, RoutedEventArgs e) => UpdateOutputRuleHint();

        private void UpdateOutputRuleHint()
        {
            if (PathHint == null || PerVideoFolderOutputRadio == null) return;
            PathHint.Text = L(PerVideoFolderOutputRadio.IsChecked == true
                ? "Ui.PathHintPerVideo"
                : "Ui.PathHintSingleFolder");
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            bool ffmpegMissing = !File.Exists(_ffmpegPath);
            DependencyBanner.Visibility = ffmpegMissing ? Visibility.Visible : Visibility.Collapsed;

            if (ffmpegMissing)
            {
                Log(L("Log.FFmpegMissing"));
                UpdateQueueState();
                return;
            }

            if (!File.Exists(_ytdlpPath))
            {
                Log(L("Log.YtDlpMissing"));
            }

            try
            {
                _currentProfile = await _ffmpegHelper.DetectBestEncoderAsync();
                QueueStatus.Text = LF("Ui.EngineReady", _currentProfile.Name);
                CalculateSafeThreads();
            }
            catch (Exception ex)
            {
                Log(LF("Log.EncoderDetectFailed", ex.Message));
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
            if (!_hasPersistedSettings)
                TxtThreads.Text = result.suggestedCount.ToString();
            Log(LF("Log.SystemConfig", result.systemInfo));
            Log(LF("Log.ThreadSuggestion", result.suggestedCount, result.reason));
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
                string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (ComposeWorkspaceRadio?.IsChecked == true)
                    AddCompositionFiles(ExpandCompositionPaths(paths));
                else
                    AddFilesToQueue(paths);
                e.Handled = true;
            }
        }

        private void DropZone_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isBusy) return;

            var dialog = new OpenFileDialog
            {
                Filter = L("Ui.SelectVideoFilter"),
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
                if (IsVideoFile(file))
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
                DropTitle.Text = LF("Ui.DropTitleWithCount", _taskQueue.Count);
                TxtOutputDir.Text = string.IsNullOrEmpty(_outputFolderPath) ? L("Ui.DefaultOutput") : _outputFolderPath;
                UpdateOutputRuleHint();
                UpdateSourcePreviewState();
                Log(LF("Log.AddedFiles", added, _taskQueue.Count));
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
                    TxtOutputDir.Text = L("Ui.DefaultOutput");
                    UpdateOutputRuleHint();
                    DropTitle.Text = L("Ui.DropTitleEmpty");
                }
                else
                {
                    DropTitle.Text = LF("Ui.DropTitleWithCount", _taskQueue.Count);
                }

                UpdateSourcePreviewState();
                UpdateQueueState();
                Log(LF("Log.RemovedFile", item.Name, _taskQueue.Count));
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
                Log(L("Log.ThumbnailSkipped"));
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
                    Log(LF("Log.ThumbnailFailed", item.Name + (string.IsNullOrWhiteSpace(error) ? "" : " - " + error.Trim())));
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
                Log(LF("Log.ThumbnailFailed", item.Name));
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
                if (process == null) return L("Log.FFmpegProcessFailed");
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
                MessageBox.Show(this, L("Log.MissingYtDlpMessage"), L("Ui.MissingComponent"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string initialUrl = Clipboard.ContainsText() && Clipboard.GetText().Contains("http", StringComparison.OrdinalIgnoreCase)
                ? Clipboard.GetText()
                : "";

            NetworkVideoImportRequest request = PromptForUrls(initialUrl);
            List<string> urls = ExtractUrls(request?.UrlText);
            if (urls.Count == 0) return;

            var temporaryCookieFiles = new Dictionary<VideoPlatform, string>();
            bool useEmbeddedLogin = request.CookieOptions.Source == DownloadCookieSource.EmbeddedPlatform;

            SetBusy(true);
            _operationCancellation = new CancellationTokenSource();
            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = true;
            Log(LF("Log.DownloadParsing", urls.Count));

            string downloadDir = string.IsNullOrEmpty(_outputFolderPath) ? Path.Combine(_appDir, "Downloads") : _outputFolderPath;
            Directory.CreateDirectory(downloadDir);

            async Task<string> AcquirePlatformCookieAsync(VideoPlatform platform, bool forceRelogin)
            {
                if (!forceRelogin
                    && temporaryCookieFiles.TryGetValue(platform, out string existing)
                    && File.Exists(existing)) return existing;
                if (temporaryCookieFiles.Remove(platform, out string stale))
                    PlatformLoginService.DeleteTemporaryCookieFile(stale);
                string acquired = await _platformLoginService.AcquireCookieFileAsync(
                    this,
                    platform,
                    forceRelogin,
                    _operationCancellation.Token);
                if (string.IsNullOrWhiteSpace(acquired))
                    throw new OperationCanceledException("用户取消了平台扫码登录。");
                temporaryCookieFiles[platform] = acquired;
                return acquired;
            }

            try
            {
                int index = 0;
                int failed = 0;
                foreach (string url in urls)
                {
                    index++;
                    Log($">>> [{index}/{urls.Count}] {DownloadService.RedactUrlForLog(url)}");
                    try
                    {
                        VideoPlatform? platform = VideoPlatformCatalog.Detect(url);
                        DownloadCookieOptions activeCookieOptions = request.CookieOptions;
                        if (useEmbeddedLogin)
                        {
                            activeCookieOptions = platform.HasValue
                                ? new DownloadCookieOptions
                                {
                                    Source = DownloadCookieSource.EmbeddedPlatform,
                                    CookieFilePath = await AcquirePlatformCookieAsync(platform.Value, forceRelogin: false)
                                }
                                : new DownloadCookieOptions { Source = DownloadCookieSource.None };
                        }
                        string downloadedFile;
                        try
                        {
                            downloadedFile = await _downloadService.DownloadVideoAsync(
                                url,
                                downloadDir,
                                activeCookieOptions,
                                _operationCancellation.Token);
                        }
                        catch (InvalidOperationException cookieError) when (
                            platform.HasValue
                            && DownloadService.IsPlatformLoginFailure(platform.Value, cookieError.Message))
                        {
                            VideoPlatformDefinition definition = VideoPlatformCatalog.Get(platform.Value);
                            bool forceRelogin = activeCookieOptions.Source == DownloadCookieSource.EmbeddedPlatform;
                            Log($"[Cookie] {definition.DisplayName}登录态不可用，正在打开扫码登录…");
                            string temporaryCookieFile = await AcquirePlatformCookieAsync(platform.Value, forceRelogin);
                            activeCookieOptions = new DownloadCookieOptions
                            {
                                Source = DownloadCookieSource.EmbeddedPlatform,
                                CookieFilePath = temporaryCookieFile
                            };
                            useEmbeddedLogin = true;
                            _downloadCookieSource = DownloadCookieSource.EmbeddedPlatform;
                            downloadedFile = await _downloadService.DownloadVideoAsync(
                                url,
                                downloadDir,
                                activeCookieOptions,
                                _operationCancellation.Token);
                        }
                        if (!string.IsNullOrEmpty(downloadedFile) && File.Exists(downloadedFile))
                        {
                            Log(LF("Log.DownloadSuccess", Path.GetFileName(downloadedFile)));
                            string[] queueFiles = request.FilesForQueue(new[] { downloadedFile }).ToArray();
                            if (queueFiles.Length > 0)
                            {
                                AddFilesToQueue(queueFiles);
                            }
                            else
                            {
                                Log(L("Log.DownloadSavedOnly"));
                            }
                        }
                        else
                        {
                            throw new InvalidOperationException(L("Log.DownloadFailed"));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Log(LF("Log.DownloadItemFailed", index, urls.Count, ex.Message));
                    }
                }

                if (failed > 0)
                {
                    Log(LF("Log.DownloadBatchSummary", urls.Count - failed, failed));
                }
            }
            catch (OperationCanceledException)
            {
                Log(L("Log.OperationCancelled"));
            }
            catch (Exception ex)
            {
                Log(LF("Log.Fatal", ex.Message));
            }
            finally
            {
                foreach (string cookieFile in temporaryCookieFiles.Values)
                    PlatformLoginService.DeleteTemporaryCookieFile(cookieFile);
                _operationCancellation?.Dispose();
                _operationCancellation = null;
                Progress.IsIndeterminate = false;
                Progress.Visibility = Visibility.Collapsed;
                SetBusy(false);
            }
        }

        private NetworkVideoImportRequest PromptForUrls(string initialUrl)
        {
            Window prompt = new Window
            {
                Owner = this,
                Width = 660,
                Height = 510,
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
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            DockPanel header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            TextBlock title = new TextBlock
            {
                Text = L("Ui.AddNetworkVideo"),
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
                Text = L("Ui.PasteVideoLinks"), 
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
                Margin = new Thickness(0, 0, 0, 14),
                MinHeight = 160
            };
            Grid cookiePanel = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            cookiePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            cookiePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cookiePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cookiePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TextBlock cookieLabel = new TextBlock
            {
                Text = "Cookie 来源",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
            };
            ComboBox cookieSource = new ComboBox { MinHeight = 34 };
            cookieSource.Items.Add(new ComboBoxItem { Content = "平台扫码登录（推荐）", Tag = DownloadCookieSource.EmbeddedPlatform });
            cookieSource.Items.Add(new ComboBoxItem { Content = "不使用", Tag = DownloadCookieSource.None });
            cookieSource.Items.Add(new ComboBoxItem { Content = "Microsoft Edge", Tag = DownloadCookieSource.Edge });
            cookieSource.Items.Add(new ComboBoxItem { Content = "Google Chrome", Tag = DownloadCookieSource.Chrome });
            cookieSource.Items.Add(new ComboBoxItem { Content = "Firefox", Tag = DownloadCookieSource.Firefox });
            cookieSource.Items.Add(new ComboBoxItem { Content = "Cookie 文件", Tag = DownloadCookieSource.CookieFile });
            cookieSource.SelectedIndex = Math.Max(0, Array.FindIndex(
                cookieSource.Items.Cast<ComboBoxItem>().ToArray(),
                item => item.Tag is DownloadCookieSource source && source == _downloadCookieSource));

            Grid cookieFilePanel = new Grid { Margin = new Thickness(150, 8, 0, 0) };
            cookieFilePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cookieFilePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBox cookieFile = new TextBox
            {
                Text = _downloadCookieFilePath,
                IsReadOnly = true,
                MinHeight = 32,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            Button browseCookie = new Button
            {
                Content = L("Ui.Browse"),
                Style = (Style)FindResource("SecondaryButton"),
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 72
            };
            browseCookie.Click += (s, e) =>
            {
                var dialog = new OpenFileDialog
                {
                    Title = "选择 Netscape 格式 Cookie 文件",
                    Filter = "Cookie 文件 (*.txt)|*.txt|所有文件 (*.*)|*.*"
                };
                if (dialog.ShowDialog(prompt) == true) cookieFile.Text = dialog.FileName;
            };
            Grid.SetColumn(cookieFile, 0);
            Grid.SetColumn(browseCookie, 1);
            cookieFilePanel.Children.Add(cookieFile);
            cookieFilePanel.Children.Add(browseCookie);
            Grid.SetRow(cookieLabel, 0);
            Grid.SetColumn(cookieLabel, 0);
            Grid.SetRow(cookieSource, 0);
            Grid.SetColumn(cookieSource, 1);
            Grid.SetRow(cookieFilePanel, 1);
            Grid.SetColumnSpan(cookieFilePanel, 2);
            cookiePanel.Children.Add(cookieLabel);
            cookiePanel.Children.Add(cookieSource);
            cookiePanel.Children.Add(cookieFilePanel);
            void UpdateCookieFileVisibility()
            {
                DownloadCookieSource selected = (cookieSource.SelectedItem as ComboBoxItem)?.Tag is DownloadCookieSource source
                    ? source
                    : DownloadCookieSource.None;
                cookieFilePanel.Visibility = selected == DownloadCookieSource.CookieFile
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            cookieSource.SelectionChanged += (s, e) => UpdateCookieFileVisibility();
            UpdateCookieFileVisibility();
            CheckBox autoQueue = new CheckBox
            {
                Content = L("Ui.AutoAddDownloadsToQueue"),
                IsChecked = true,
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
                Margin = new Thickness(0, 0, 0, 16)
            };
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button cancel = new Button { Content = L("Ui.Cancel"), Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 10, 0), MinWidth = 86 };
            Button ok = new Button { Content = L("Ui.AddAndParse"), Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(18, 8, 18, 8), MinWidth = 118 };

            NetworkVideoImportRequest result = null;
            DownloadCookieOptions ReadCookieOptions()
            {
                DownloadCookieSource selected = (cookieSource.SelectedItem as ComboBoxItem)?.Tag is DownloadCookieSource source
                    ? source
                    : DownloadCookieSource.None;
                return new DownloadCookieOptions { Source = selected, CookieFilePath = cookieFile.Text.Trim() };
            }
            cancel.Click += (s, e) => prompt.Close();
            ok.Click += (s, e) =>
            {
                result = new NetworkVideoImportRequest
                {
                    UrlText = input.Text,
                    AddDownloadsToQueue = autoQueue.IsChecked == true,
                    CookieOptions = ReadCookieOptions()
                };
                _downloadCookieSource = result.CookieOptions.Source;
                _downloadCookieFilePath = result.CookieOptions.CookieFilePath;
                prompt.Close();
            };
            input.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    result = new NetworkVideoImportRequest
                    {
                        UrlText = input.Text,
                        AddDownloadsToQueue = autoQueue.IsChecked == true,
                        CookieOptions = ReadCookieOptions()
                    };
                    _downloadCookieSource = result.CookieOptions.Source;
                    _downloadCookieFilePath = result.CookieOptions.CookieFilePath;
                    prompt.Close();
                }
            };

            actions.Children.Add(cancel);
            actions.Children.Add(ok);
            Grid.SetRow(header, 0);
            Grid.SetRow(label, 1);
            Grid.SetRow(input, 2);
            Grid.SetRow(cookiePanel, 3);
            Grid.SetRow(autoQueue, 4);
            Grid.SetRow(actions, 5);
            root.Children.Add(header);
            root.Children.Add(label);
            root.Children.Add(input);
            root.Children.Add(cookiePanel);
            root.Children.Add(autoQueue);
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

        private void SelectLanguagePreference(LanguagePreference preference)
        {
            if (LanguageCombo == null) return;

            _isApplyingLanguage = true;
            LanguageCombo.SelectedValue = preference == LanguagePreference.Auto
                ? (LocalizationManager.UseChinese ? LanguagePreference.Chinese.ToString() : LanguagePreference.English.ToString())
                : preference.ToString();
            _isApplyingLanguage = false;
        }

        private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isApplyingLanguage || LanguageCombo.SelectedValue == null) return;

            if (Enum.TryParse(LanguageCombo.SelectedValue.ToString(), out LanguagePreference preference))
            {
                LocalizationManager.SetPreference(preference, save: true);
                ApplyLocalizedState();
            }
        }

        private void ApplyLocalizedState()
        {
            UpdateSplitModeControls();
            UpdateQueueState();
            UpdateCompositionState();

            DropTitle.Text = _taskQueue.Count > 0
                ? LF("Ui.DropTitleWithCount", _taskQueue.Count)
                : L("Ui.DropTitleEmpty");

            if (string.IsNullOrEmpty(_outputFolderPath))
            {
                TxtOutputDir.Text = L("Ui.DefaultOutput");
            }
            if (string.IsNullOrEmpty(_compositionOutputFolderPath))
            {
                TxtCompositionOutputDir.Text = L("Ui.SelectOutputDir");
            }

            UpdateOutputRuleHint();
            BtnToggleLog.Content = LogDrawer.Visibility == Visibility.Visible ? L("Ui.HideLog") : L("Ui.ShowLog");
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = L("Ui.SelectOutputDir")
            };

            if (dialog.ShowDialog(this) == true)
            {
                _outputFolderPath = dialog.FolderName;
                TxtOutputDir.Text = _outputFolderPath;
                UpdateOutputRuleHint();
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
                MessageBox.Show(this, L("Log.EncoderNotReady"), L("Ui.CannotStart"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryReadInt(TxtThreads.Text, 1, 16, out int maxThreads, L("Param.Threads"))) return;
            if (!TryReadDouble(TxtDuration.Text, 1, 600, out double fixedDuration, L("Param.Duration"))) return;

            SubtitleRemovalInstallation subtitleInstallation = null;
            string subtitleAccelerator = null;
            if (ChkRemoveSubtitles.IsChecked == true)
            {
                subtitleInstallation = await SubtitleRemovalService.FindBestInstallationAsync(_appDir);
                if (subtitleInstallation == null)
                {
                    MessageBox.Show(this, L("Log.SubtitleBackendMissing"), L("Ui.MissingComponent"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                subtitleAccelerator = await SubtitleRemovalService.ProbeAcceleratorAsync(subtitleInstallation);
            }

            var options = new ProcessingOptions
            {
                SplitMode = FixedModeRadio.IsChecked == true ? SplitMode.FixedDuration : SplitMode.SmartScene,
                FixedClipSelectionMode = ReadFixedClipSelectionMode(),
                SceneDetectionEngine = DetectorCombo.SelectedIndex == 1 ? SceneDetectionEngine.AdaptiveDetector : SceneDetectionEngine.ContentDetector,
                FixedDurationSeconds = fixedDuration,
                MinimumSceneDurationSeconds = fixedDuration,
                Sensitivity = SensitivitySlider.Value,
                Mute = ChkMute.IsChecked == true,
                SubtitleRemoval = new SubtitleRemovalOptions
                {
                    Enabled = ChkRemoveSubtitles.IsChecked == true,
                    UseAutomaticDetection = AutomaticSubtitleScopeRadio.IsChecked == true,
                    Region = _subtitleRegion,
                    InpaintMode = SubtitleRemovalService.SelectDefaultInpaintMode(subtitleAccelerator),
                    Installation = subtitleInstallation
                },
                MaxThreads = maxThreads,
                Profile = _currentProfile
            };

            SetBusy(true);
            _operationCancellation = new CancellationTokenSource();
            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = false;
            Progress.Value = 0;
            BtnOpenDir.Visibility = Visibility.Collapsed;

            int total = _taskQueue.Count;
            int count = 0;
            int succeeded = 0;
            int failed = 0;

            try
            {
                while (_taskQueue.Count > 0)
                {
                    string video = _taskQueue.Peek();
                    count++;
                    string name = Path.GetFileNameWithoutExtension(video);
                    try
                    {
                        BatchOutputRule outputRule = PerVideoFolderOutputRadio.IsChecked == true
                            ? BatchOutputRule.PerVideoSubfolder
                            : BatchOutputRule.SingleFolder;
                        SliceOutputPlan outputPlan = SliceOutputPlanner.Create(video, _outputFolderPath, _appDir, outputRule);
                        Directory.CreateDirectory(outputPlan.DirectoryPath);
                        _outputFolderPath = string.IsNullOrEmpty(_outputFolderPath)
                            ? Path.GetDirectoryName(video) ?? _appDir
                            : _outputFolderPath;
                        options.OutputFileNamePrefix = outputPlan.FileNamePrefix;

                        Log(LF("Log.Processing", count, total, name));
                        await _videoProcessor.ProcessSingleVideoAsync(
                            video,
                            outputPlan.DirectoryPath,
                            options,
                            _operationCancellation.Token);
                        succeeded++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception itemError)
                    {
                        failed++;
                        Log(LF("Log.BatchVideoFailed", count, total, Path.GetFileName(video), itemError.Message));
                    }

                    _taskQueue.Dequeue();
                    if (_queuePreviewItems.Count > 0) _queuePreviewItems.RemoveAt(0);
                    UpdateSourcePreviewState();
                    UpdateQueueState();
                }

                if (succeeded > 0) SystemSounds.Exclamation.Play();
                BtnOpenDir.Visibility = Directory.Exists(_outputFolderPath) ? Visibility.Visible : Visibility.Collapsed;
                DropTitle.Text = L("Ui.DropTitleEmpty");
                UpdateOutputRuleHint();
                Log(LF("Log.BatchFinished", succeeded, failed));
                if (failed == 0) Log(L("Log.AllDone"));
            }
            catch (OperationCanceledException)
            {
                Log(L("Log.OperationCancelled"));
            }
            catch (Exception ex)
            {
                Log($"[Error] {ex.Message}");
            }
            finally
            {
                _operationCancellation?.Dispose();
                _operationCancellation = null;
                SetBusy(false);
                Progress.Visibility = Visibility.Collapsed;
                UpdateSourcePreviewState();
                UpdateQueueState();
            }
        }

        private bool TryReadInt(string text, int min, int max, out int value, string label)
        {
            if (int.TryParse(text, out value) && value >= min && value <= max) return true;
            MessageBox.Show(this, LF("Log.InvalidInteger", label, min, max), L("Ui.InvalidParameter"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private bool TryReadDouble(string text, double min, double max, out double value, string label)
        {
            if (double.TryParse(text, out value) && value >= min && value <= max) return true;
            MessageBox.Show(this, LF("Log.InvalidNumber", label, min, max), L("Ui.InvalidParameter"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private void SplitMode_Checked(object sender, RoutedEventArgs e) => UpdateSplitModeControls();

        private FixedClipSelectionMode ReadFixedClipSelectionMode()
        {
            int selectedIndex = FixedClipSelectionCombo?.SelectedIndex ?? 0;
            return Enum.IsDefined(typeof(FixedClipSelectionMode), selectedIndex)
                ? (FixedClipSelectionMode)selectedIndex
                : FixedClipSelectionMode.Automatic;
        }

        private void RemoveSubtitles_Checked(object sender, RoutedEventArgs e)
        {
            UpdateSubtitleControls();
        }

        private void SubtitleScope_Checked(object sender, RoutedEventArgs e)
        {
            UpdateSubtitleControls();
        }

        private async void UpdateSubtitleControls()
        {
            if (BtnSubtitleRegion == null || AutomaticSubtitleScopeRadio == null || RegionSubtitleScopeRadio == null || TxtAiEngine == null) return;

            ChkRemoveSubtitles.IsEnabled = !_isBusy && _subtitleRemovalAvailable;
            bool enabled = !_isBusy && _subtitleRemovalAvailable && ChkRemoveSubtitles.IsChecked == true;
            AutomaticSubtitleScopeRadio.IsEnabled = enabled;
            RegionSubtitleScopeRadio.IsEnabled = enabled;
            BtnSubtitleRegion.IsEnabled = enabled && RegionSubtitleScopeRadio.IsChecked == true && _taskQueue.Count > 0;

            int probeGeneration = ++_subtitleProbeGeneration;
            TxtAiEngine.Text = L("Ui.AiEngineChecking");
            TxtAiEngine.Foreground = (Brush)FindResource("DimTextBrush");
            TxtAiEngine.ToolTip = null;
            SubtitleRemovalInstallation installation = await SubtitleRemovalService.FindBestInstallationAsync(_appDir);
            if (probeGeneration != _subtitleProbeGeneration) return;
            if (installation == null)
            {
                _subtitleRemovalAvailable = false;
                ChkRemoveSubtitles.IsChecked = false;
                ChkRemoveSubtitles.IsEnabled = false;
                ChkRemoveSubtitles.ToolTip = L("Log.SubtitleBackendMissing");
                AutomaticSubtitleScopeRadio.IsEnabled = false;
                RegionSubtitleScopeRadio.IsEnabled = false;
                BtnSubtitleRegion.IsEnabled = false;
                TxtAiEngine.Text = L("Ui.AiEngineUnavailable");
                TxtAiEngine.Foreground = (Brush)FindResource("DimTextBrush");
                TxtAiEngine.ToolTip = L("Log.SubtitleBackendMissing");
                return;
            }

            _subtitleRemovalAvailable = true;
            ChkRemoveSubtitles.IsEnabled = !_isBusy;
            ChkRemoveSubtitles.ToolTip = null;
            enabled = !_isBusy && ChkRemoveSubtitles.IsChecked == true;
            AutomaticSubtitleScopeRadio.IsEnabled = enabled;
            RegionSubtitleScopeRadio.IsEnabled = enabled;
            BtnSubtitleRegion.IsEnabled = enabled && RegionSubtitleScopeRadio.IsChecked == true && _taskQueue.Count > 0;

            string accelerator = await SubtitleRemovalService.ProbeAcceleratorAsync(installation);
            if (probeGeneration != _subtitleProbeGeneration) return;
            TxtAiEngine.Text = LF("Ui.AiEngineStatus", accelerator);
            TxtAiEngine.Foreground = accelerator == "CPU"
                ? new SolidColorBrush(Color.FromRgb(245, 158, 11))
                : (Brush)FindResource("AccentBrush");
            TxtAiEngine.ToolTip = accelerator == "CPU"
                ? L("Log.SubtitleCpuWarning")
                : LF("Log.SubtitleAccelerator", accelerator);
        }

        private async void SelectSubtitleRegion_Click(object sender, RoutedEventArgs e)
        {
            VideoQueueItem item = _queuePreviewItems.FirstOrDefault();
            if (item == null)
            {
                MessageBox.Show(this, L("Log.SubtitleRegionNeedsVideo"), L("Ui.SubtitleRegionTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (item.Thumbnail == null)
            {
                await LoadThumbnailAsync(item);
            }

            if (item.Thumbnail == null)
            {
                MessageBox.Show(this, LF("Log.ThumbnailFailed", item.Name), L("Ui.SubtitleRegionTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new SubtitleRegionDialog(item.Thumbnail, _subtitleRegion) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                _subtitleRegion = dialog.Region;
            }
        }

        private static bool IsVideoFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".mp4" || ext == ".mkv" || ext == ".webm" || ext == ".avi" || ext == ".mov" || ext == ".flv" || ext == ".wmv";
        }

        private void WorkspaceMode_Checked(object sender, RoutedEventArgs e)
        {
            UpdateWorkspaceMode();
        }

        private void UpdateWorkspaceMode()
        {
            if (CompositionWorkspace == null || DropZone == null || SliceConfigPanel == null) return;

            bool compositionMode = ComposeWorkspaceRadio.IsChecked == true;
            CompositionWorkspace.Visibility = compositionMode ? Visibility.Visible : Visibility.Collapsed;
            DropZone.Visibility = compositionMode ? Visibility.Collapsed : Visibility.Visible;
            SliceSourceSplitter.Visibility = compositionMode ? Visibility.Collapsed : Visibility.Visible;
            SliceConfigPanel.Visibility = compositionMode ? Visibility.Collapsed : Visibility.Visible;
            BtnAddLink.Visibility = compositionMode ? Visibility.Collapsed : Visibility.Visible;
            BtnReset.Visibility = compositionMode ? Visibility.Collapsed : Visibility.Visible;
            BtnStart.Visibility = compositionMode ? Visibility.Collapsed : Visibility.Visible;
            UpdateQueueState();
            UpdateCompositionState();
        }

        private void CompositionWorkspace_Drop(object sender, DragEventArgs e)
        {
            if (_isBusy || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            AddCompositionFiles(ExpandCompositionPaths((string[])e.Data.GetData(DataFormats.FileDrop)));
            e.Handled = true;
        }

        private void CompositionAddFiles_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            var dialog = new OpenFileDialog { Filter = L("Ui.SelectVideoFilter"), Multiselect = true };
            if (dialog.ShowDialog(this) == true) AddCompositionFiles(dialog.FileNames);
        }

        private void CompositionAddFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            var dialog = new OpenFolderDialog { Title = L("Ui.AddClipFolder"), Multiselect = false };
            if (dialog.ShowDialog(this) == true)
                AddCompositionFiles(ExpandCompositionPaths(new[] { dialog.FolderName }));
        }

        private static IEnumerable<string> ExpandCompositionPaths(IEnumerable<string> paths)
        {
            var expanded = new List<string>();
            foreach (string path in paths ?? Enumerable.Empty<string>())
            {
                if (File.Exists(path))
                {
                    expanded.Add(path);
                    continue;
                }

                if (!Directory.Exists(path)) continue;
                try
                {
                    string[] directoryFiles = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories).ToArray();
                    expanded.AddRange(CompositionMaterialFilter.PreferGeneratedScenes(directoryFiles));
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip inaccessible subtrees; valid files already discovered remain usable.
                }
                catch (IOException)
                {
                    // A removable or network folder may disappear during enumeration.
                }
            }
            return expanded;
        }

        private void AddCompositionFiles(IEnumerable<string> files)
        {
            int added = 0;
            foreach (string file in files.Where(IsVideoFile))
            {
                string fullPath = Path.GetFullPath(file);
                if (_compositionClips.Any(item => string.Equals(item.Path, fullPath, StringComparison.OrdinalIgnoreCase))) continue;
                _compositionClips.Add(new CompositionClipItem(fullPath));
                added++;
            }

            if (added > 0)
            {
                Log($"> 随机成片素材库新增 {added} 个片段，当前共 {_compositionClips.Count} 个。");
                UpdateCompositionState();
            }
        }

        private void CompositionBrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = L("Ui.SelectOutputDir") };
            if (dialog.ShowDialog(this) != true) return;
            _compositionOutputFolderPath = dialog.FolderName;
            TxtCompositionOutputDir.Text = _compositionOutputFolderPath;
            UpdateCompositionState();
        }

        private void CompositionClear_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            _compositionClips.Clear();
            UpdateCompositionState();
        }

        private void CompositionParameter_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateCompositionState();
        }

        private void UpdateCompositionState()
        {
            if (CompositionLibraryList == null || CompositionEmptyPanel == null || BtnCompositionStart == null) return;

            int clipCount = _compositionClips.Count;
            int.TryParse(TxtCompositionOutputCount?.Text, out int outputCount);
            int.TryParse(TxtCompositionClipsPerOutput?.Text, out int clipsPerOutput);
            outputCount = Math.Max(0, outputCount);
            clipsPerOutput = Math.Max(0, clipsPerOutput);

            CompositionEmptyPanel.Visibility = clipCount == 0 ? Visibility.Visible : Visibility.Collapsed;
            CompositionLibraryList.Visibility = clipCount == 0 ? Visibility.Collapsed : Visibility.Visible;
            CompositionLibrarySummary.Text = clipCount == 0
                ? L("Ui.ClipLibraryEmpty")
                : LF("Ui.CompositionLibrarySummary", clipCount, clipsPerOutput);

            long references = (long)outputCount * clipsPerOutput;
            double averageReuse = clipCount == 0 ? 0 : references / (double)clipCount;
            double totalHours = references * 2.0 / 3600.0;
            CompositionAverageReuse.Text = averageReuse.ToString("0.0") + " ×";
            CompositionTotalDuration.Text = totalHours < 1
                ? (totalHours * 60).ToString("0.0") + " min"
                : totalHours.ToString("0.0") + " h";

            if (clipCount > 0 && clipCount < clipsPerOutput)
                CompositionRiskText.Text = LF("Ui.CompositionNeedClips", clipCount, clipsPerOutput);
            else if (clipCount > 0 && clipCount == clipsPerOutput)
                CompositionRiskText.Text = "素材数等于单条片段数：所有成片只能使用同一组素材，仅顺序不同。";
            else if (averageReuse >= 50)
                CompositionRiskText.Text = "平均复用次数较高，建议增加素材量以进一步降低跨成片重复率。";
            else
                CompositionRiskText.Text = "";

            bool ready = !_isBusy && clipCount >= clipsPerOutput && clipsPerOutput > 0 &&
                outputCount > 0 && !string.IsNullOrWhiteSpace(_compositionOutputFolderPath) && File.Exists(_ffmpegPath);
            BtnCompositionStart.IsEnabled = ready;
            CompositionStatusText.Text = clipCount == 0 ? L("Ui.CompositionReady") : LF("Ui.CompositionClipStatus", clipCount);
        }

        private bool TryCreateCompositionPlan(bool showError, out CompositionOptions options, out CompositionPlan plan)
        {
            options = null;
            plan = null;
            if (!int.TryParse(TxtCompositionOutputCount.Text, out int outputCount) || outputCount < 1 || outputCount > 5000)
                return ShowCompositionError(showError, LF("Log.InvalidInteger", L("Ui.OutputVideoCount"), 1, 5000));
            if (!int.TryParse(TxtCompositionClipsPerOutput.Text, out int clipsPerOutput) || clipsPerOutput < 1 || clipsPerOutput > 500)
                return ShowCompositionError(showError, LF("Log.InvalidInteger", L("Ui.ClipsPerVideo"), 1, 500));
            if (!int.TryParse(TxtCompositionSeed.Text, out int seed))
                return ShowCompositionError(showError, LF("Log.InvalidInteger", L("Ui.RandomSeed"), int.MinValue, int.MaxValue));
            if (!int.TryParse(TxtCompositionThreads.Text, out int threads) || threads < 1 || threads > 16)
                return ShowCompositionError(showError, LF("Log.InvalidInteger", L("Ui.Threads"), 1, 16));
            if (string.IsNullOrWhiteSpace(_compositionOutputFolderPath))
                return ShowCompositionError(showError, L("Ui.CompositionNeedOutput"));
            if (_compositionClips.Count < clipsPerOutput)
                return ShowCompositionError(showError, LF("Ui.CompositionNeedClips", _compositionClips.Count, clipsPerOutput));

            options = new CompositionOptions
            {
                OutputCount = outputCount,
                ClipsPerOutput = clipsPerOutput,
                Seed = seed,
                OutputDirectory = _compositionOutputFolderPath,
                FileNamePrefix = "Mix",
                MuteAudio = ChkCompositionMute.IsChecked == true,
                MaxParallelism = threads,
                EncoderProfile = _currentProfile
            };

            try
            {
                plan = new RandomCompositionPlanner().CreatePlan(_compositionClips.Select(item => item.Path), options);
                return true;
            }
            catch (Exception ex)
            {
                return ShowCompositionError(showError, ex.Message);
            }
        }

        private bool ShowCompositionError(bool show, string message)
        {
            if (show) MessageBox.Show(this, message, L("Ui.CannotStart"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private void CompositionCheck_Click(object sender, RoutedEventArgs e)
        {
            if (!TryCreateCompositionPlan(true, out CompositionOptions options, out CompositionPlan plan)) return;
            long references = (long)options.OutputCount * options.ClipsPerOutput;
            MessageBox.Show(this, LF("Ui.CompositionPlanReady", plan.Jobs.Count, references),
                L("Ui.CheckCompositionPlan"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void CompositionStart_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || !TryCreateCompositionPlan(true, out CompositionOptions options, out CompositionPlan plan)) return;

            _compositionCancellation = new CancellationTokenSource();
            SetBusy(true);
            Progress.Visibility = Visibility.Visible;
            Progress.Maximum = plan.Jobs.Count;
            Progress.Value = 0;
            CompositionStatusText.Text = $"0 / {plan.Jobs.Count}";
            BtnCompositionCancel.Visibility = Visibility.Visible;
            Log(LF("Ui.CompositionPlanReady", plan.Jobs.Count, (long)options.OutputCount * options.ClipsPerOutput));

            try
            {
                var validatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (true)
                {
                    string[] pendingValidation = plan.Jobs
                        .SelectMany(job => job.Clips)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(path => !validatedPaths.Contains(path))
                        .ToArray();
                    if (pendingValidation.Length == 0) break;

                    CompositionStatusText.Text = $"正在预检素材：0 / {pendingValidation.Length}";
                    Log($"> 成片前完整解码预检：{pendingValidation.Length} 个候选素材...");
                    ClipValidationResult validation = await _videoComposer.ValidateClipsAsync(
                        pendingValidation,
                        Math.Min(2, options.MaxParallelism),
                        validateAudio: !options.MuteAudio,
                        cancellationToken: _compositionCancellation.Token);
                    foreach (string path in validation.ValidClips) validatedPaths.Add(path);
                    if (validation.InvalidClips.Count == 0) break;

                    var invalidSet = new HashSet<string>(validation.InvalidClips, StringComparer.OrdinalIgnoreCase);
                    foreach (CompositionClipItem item in _compositionClips.Where(item => invalidSet.Contains(item.Path)).ToArray())
                    {
                        _compositionClips.Remove(item);
                    }
                    Log($"[素材预检] 已排除 {validation.InvalidClips.Count} 个无法完整解码的片段：");
                    foreach (string path in validation.InvalidClips.Take(20))
                    {
                        Log($"  - {path}");
                    }
                    if (validation.InvalidClips.Count > 20)
                    {
                        Log($"  ...其余 {validation.InvalidClips.Count - 20} 个详见素材库移除结果。");
                    }
                    if (_compositionClips.Count < options.ClipsPerOutput)
                    {
                        throw new InvalidOperationException(
                            $"预检后仅剩 {_compositionClips.Count} 个可解码素材，少于每条所需的 {options.ClipsPerOutput} 个。");
                    }
                    plan = new RandomCompositionPlanner().CreatePlan(
                        _compositionClips.Select(item => item.Path),
                        options);
                }

                Progress.Maximum = plan.Jobs.Count;
                Progress.Value = 0;
                CompositionStatusText.Text = $"0 / {plan.Jobs.Count}";
                CompositionRunResult result = await _videoComposer.ComposeAsync(plan, options, _compositionCancellation.Token);
                Log(LF("Ui.CompositionFinished", result.Succeeded, result.Failed));
                SystemSounds.Exclamation.Play();
            }
            catch (OperationCanceledException)
            {
                Log(L("Ui.CompositionCancelled"));
            }
            catch (Exception ex)
            {
                Log($"[Error] {ex.Message}");
            }
            finally
            {
                _compositionCancellation.Dispose();
                _compositionCancellation = null;
                BtnCompositionCancel.Visibility = Visibility.Collapsed;
                Progress.Visibility = Visibility.Collapsed;
                SetBusy(false);
                UpdateCompositionState();
            }
        }

        private void CompositionCancel_Click(object sender, RoutedEventArgs e)
        {
            _compositionCancellation?.Cancel();
        }

        private void UpdateSplitModeControls()
        {
            if (TxtDuration == null || SensitivitySlider == null || DetectorCombo == null
                || FixedClipSelectionCombo == null) return;

            bool fixedMode = FixedModeRadio.IsChecked == true;
            TxtDuration.IsEnabled = true;
            SensitivitySlider.IsEnabled = !fixedMode;
            DetectorCombo.IsEnabled = !fixedMode;
            FixedClipSelectionCombo.IsEnabled = fixedMode;
            DetectorPanel.Visibility = fixedMode ? Visibility.Collapsed : Visibility.Visible;
            FixedClipSelectionPanel.Visibility = fixedMode ? Visibility.Visible : Visibility.Collapsed;
            DurationLabel.Text = fixedMode ? L("Ui.FixedDurationLabel") : L("Ui.MinimumDurationLabel");
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
                BtnToggleLog.Content = L("Ui.HideLog");
            }
            else
            {
                if (LogRow.Height.Value > 0) _lastLogRowHeight = LogRow.Height;
                LogDrawer.Visibility = Visibility.Collapsed;
                LogSplitter.Visibility = Visibility.Collapsed;
                LogRow.Height = new GridLength(0);
                BtnToggleLog.Content = L("Ui.ShowLog");
            }
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                _operationCancellation?.Cancel();
                BtnReset.IsEnabled = false;
                return;
            }

            _taskQueue.Clear();
            _queuePreviewItems.Clear();
            _outputFolderPath = "";
            TxtOutputDir.Text = L("Ui.DefaultOutput");
            UpdateOutputRuleHint();
            DropTitle.Text = L("Ui.DropTitleEmpty");
            Progress.Value = 0;
            Progress.Visibility = Visibility.Collapsed;
            BtnOpenDir.Visibility = Visibility.Collapsed;
            UpdateSourcePreviewState();
            Log(L("Log.QueueCleared"));
            UpdateQueueState();
        }

        private void FixDependency_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", AppContext.BaseDirectory) { UseShellExecute = true });
            Log(L("Log.DependencyHelp"));
        }

        private void SetBusy(bool busy)
        {
            _isBusy = busy;
            SliceWorkspaceRadio.IsEnabled = !busy;
            ComposeWorkspaceRadio.IsEnabled = !busy;
            DropZone.IsEnabled = !busy;
            BtnAddLink.IsEnabled = !busy;
            SmartModeRadio.IsEnabled = !busy;
            FixedModeRadio.IsEnabled = !busy;
            TxtDuration.IsEnabled = !busy;
            FixedClipSelectionCombo.IsEnabled = !busy && FixedModeRadio.IsChecked == true;
            SensitivitySlider.IsEnabled = !busy && SmartModeRadio.IsChecked == true;
            DetectorCombo.IsEnabled = !busy && SmartModeRadio.IsChecked == true;
            TxtThreads.IsEnabled = !busy;
            ChkMute.IsEnabled = !busy;
            ChkRemoveSubtitles.IsEnabled = !busy && _subtitleRemovalAvailable;
            UpdateSubtitleControls();
            BtnReset.IsEnabled = true;
            BtnReset.Content = busy ? L("Ui.CancelTask") : L("Ui.ResetQueue");
            BtnStart.IsEnabled = !busy && _taskQueue.Count > 0 && File.Exists(_ffmpegPath);
            BtnCompositionAddFiles.IsEnabled = !busy;
            BtnCompositionAddFolder.IsEnabled = !busy;
            BtnCompositionClear.IsEnabled = !busy;
            BtnCompositionCheck.IsEnabled = !busy;
            TxtCompositionOutputCount.IsEnabled = !busy;
            TxtCompositionClipsPerOutput.IsEnabled = !busy;
            TxtCompositionSeed.IsEnabled = !busy;
            TxtCompositionThreads.IsEnabled = !busy;
            ChkCompositionMute.IsEnabled = !busy;
            UpdateCompositionState();
        }

        private void UpdateQueueState()
        {
            BtnStart.IsEnabled = !_isBusy && _taskQueue.Count > 0 && File.Exists(_ffmpegPath);
            UpdateSubtitleControls();
            QueueStatus.Text = ComposeWorkspaceRadio?.IsChecked == true
                ? LF("Ui.CompositionClipStatus", _compositionClips.Count)
                : (_taskQueue.Count > 0
                    ? LF("Ui.AddedFilesStatus", _taskQueue.Count)
                    : (_currentProfile != null ? LF("Ui.EngineReady", _currentProfile.Name) : L("Ui.QueueEmpty")));
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

        private void UpdateCompositionProgress(int current, int total)
        {
            Dispatcher.Invoke(() =>
            {
                Progress.Visibility = Visibility.Visible;
                Progress.IsIndeterminate = false;
                Progress.Maximum = Math.Max(1, total);
                Progress.Value = Math.Max(0, Math.Min(current, Progress.Maximum));
                CompositionStatusText.Text = $"{current} / {total}";
            });
        }

        private void UpdateCompositionStageProgress(string message, double percent)
        {
            Dispatcher.Invoke(() =>
            {
                CompositionStatusText.Text = message;
                Progress.Visibility = Visibility.Visible;
                Progress.IsIndeterminate = false;
                Progress.Maximum = 100;
                Progress.Value = Math.Clamp(percent, 0, 100);
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

        private sealed class CompositionClipItem
        {
            public string Path { get; }
            public string Name { get; }
            public string Directory { get; }
            public string Extension { get; }

            public CompositionClipItem(string path)
            {
                Path = path;
                Name = System.IO.Path.GetFileName(path);
                Directory = System.IO.Path.GetDirectoryName(path) ?? "";
                Extension = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            }
        }
    }
}


