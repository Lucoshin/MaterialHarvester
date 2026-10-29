using System;
using System.IO;
using System.Text.Json;

namespace VideoToMaterial
{
    public enum BatchOutputRule
    {
        SingleFolder,
        PerVideoSubfolder
    }

    public sealed class UserSettings
    {
        public int Version { get; set; } = 1;
        public bool UseCompositionWorkspace { get; set; }
        public SplitMode SplitMode { get; set; } = SplitMode.SmartScene;
        public FixedClipSelectionMode FixedClipSelectionMode { get; set; } = FixedClipSelectionMode.Automatic;
        public SceneDetectionEngine SceneDetectionEngine { get; set; } = SceneDetectionEngine.ContentDetector;
        public double Sensitivity { get; set; } = 0.75;
        public double DurationSeconds { get; set; } = 10;
        public int MaxThreads { get; set; } = 3;
        public bool Mute { get; set; }
        public bool RemoveSubtitles { get; set; }
        public bool AutomaticSubtitleScope { get; set; } = true;
        public double SubtitleLeft { get; set; } = SubtitleRegion.Default.Left;
        public double SubtitleTop { get; set; } = SubtitleRegion.Default.Top;
        public double SubtitleRight { get; set; } = SubtitleRegion.Default.Right;
        public double SubtitleBottom { get; set; } = SubtitleRegion.Default.Bottom;
        public string OutputFolderPath { get; set; } = "";
        public BatchOutputRule BatchOutputRule { get; set; } = BatchOutputRule.SingleFolder;
        public int CompositionOutputCount { get; set; } = 100;
        public int CompositionClipsPerOutput { get; set; } = 50;
        public int CompositionSeed { get; set; } = 20260813;
        public int CompositionThreads { get; set; } = 2;
        public bool CompositionMute { get; set; }
        public string CompositionOutputFolderPath { get; set; } = "";
        public DownloadCookieSource DownloadCookieSource { get; set; } = DownloadCookieSource.None;
        public string DownloadCookieFilePath { get; set; } = "";
    }

    public sealed class UserSettingsStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private readonly string _path;

        public UserSettingsStore(string path = null)
        {
            _path = path ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MaterialHarvester",
                "settings.json");
        }

        public bool Exists => File.Exists(_path);

        public UserSettings Load()
        {
            try
            {
                if (!File.Exists(_path)) return new UserSettings();
                UserSettings settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_path), JsonOptions);
                return Normalize(settings ?? new UserSettings());
            }
            catch
            {
                return new UserSettings();
            }
        }

        public void Save(UserSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            try
            {
                string directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                string temporaryPath = _path + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Normalize(settings), JsonOptions));
                File.Move(temporaryPath, _path, true);
            }
            catch
            {
                // Preferences are non-critical and must never prevent the application from closing.
            }
        }

        private static UserSettings Normalize(UserSettings settings)
        {
            if (!Enum.IsDefined(settings.SplitMode)) settings.SplitMode = SplitMode.SmartScene;
            if (!Enum.IsDefined(settings.FixedClipSelectionMode))
                settings.FixedClipSelectionMode = FixedClipSelectionMode.Automatic;
            if (!Enum.IsDefined(settings.SceneDetectionEngine)) settings.SceneDetectionEngine = SceneDetectionEngine.ContentDetector;
            if (!Enum.IsDefined(settings.BatchOutputRule)) settings.BatchOutputRule = BatchOutputRule.SingleFolder;
            if (!Enum.IsDefined(settings.DownloadCookieSource)) settings.DownloadCookieSource = DownloadCookieSource.None;
            settings.Sensitivity = Math.Clamp(settings.Sensitivity, 0.01, 1);
            settings.DurationSeconds = Math.Clamp(settings.DurationSeconds, 1, 600);
            settings.MaxThreads = Math.Clamp(settings.MaxThreads, 1, 16);
            settings.CompositionOutputCount = Math.Clamp(settings.CompositionOutputCount, 1, 5000);
            settings.CompositionClipsPerOutput = Math.Clamp(settings.CompositionClipsPerOutput, 1, 500);
            settings.CompositionThreads = Math.Clamp(settings.CompositionThreads, 1, 16);
            settings.OutputFolderPath ??= "";
            settings.CompositionOutputFolderPath ??= "";
            settings.DownloadCookieFilePath ??= "";
            return settings;
        }
    }
}
