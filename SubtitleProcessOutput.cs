using System;
using System.Text.RegularExpressions;

namespace VideoToMaterial
{
    public sealed class SubtitleProcessOutput
    {
        private static readonly Regex AnsiSequence = new Regex(
            @"\x1B\[[0-?]*[ -/]*[@-~]",
            RegexOptions.Compiled);

        private static readonly Regex TqdmProgress = new Regex(
            @"Subtitle (?:Removing|Finding):.*?(?<current>\d+)\s*/\s*(?<total>\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex BlockProgress = new Regex(
            @"^Processing:\s*\d+\s*-\s*(?<current>\d+)\s*/\s*Total:\s*(?<total>\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] StableMessageMarkers =
        {
            "Subtitle removal model:",
            "Subtitle detection model:",
            "[Processing]",
            "[Complete]",
            "Processing time:"
        };

        private SubtitleProcessOutput(string message, bool isProgress, int current, int total)
        {
            Message = message;
            IsProgress = isProgress;
            Current = current;
            Total = total;
        }

        public string Message { get; }
        public bool IsProgress { get; }
        public int Current { get; }
        public int Total { get; }

        public static SubtitleProcessOutput Parse(string rawLine)
        {
            string clean = AnsiSequence.Replace(rawLine ?? string.Empty, string.Empty)
                .Replace("\r", string.Empty)
                .Trim();

            Match progress = TqdmProgress.Match(clean);
            if (!progress.Success) progress = BlockProgress.Match(clean);
            if (progress.Success)
            {
                int current = int.Parse(progress.Groups["current"].Value);
                int total = int.Parse(progress.Groups["total"].Value);
                return new SubtitleProcessOutput(ExtractStableMessage(clean), true, current, total);
            }

            return new SubtitleProcessOutput(
                string.IsNullOrWhiteSpace(clean) ? null : clean,
                false,
                0,
                0);
        }

        private static string ExtractStableMessage(string line)
        {
            int firstMarker = -1;
            foreach (string marker in StableMessageMarkers)
            {
                int index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && (firstMarker < 0 || index < firstMarker)) firstMarker = index;
            }

            return firstMarker < 0 ? null : line.Substring(firstMarker).Trim();
        }
    }
}
