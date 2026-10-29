using System;
using System.IO;
using System.Linq;

namespace VideoToMaterial
{
    public sealed class SliceOutputPlan
    {
        public SliceOutputPlan(string directoryPath, string fileNamePrefix)
        {
            DirectoryPath = directoryPath;
            FileNamePrefix = fileNamePrefix;
        }

        public string DirectoryPath { get; }
        public string FileNamePrefix { get; }
    }

    public static class SliceOutputPlanner
    {
        public static SliceOutputPlan Create(
            string videoPath,
            string selectedOutputFolder,
            string fallbackApplicationFolder,
            BatchOutputRule rule)
        {
            if (string.IsNullOrWhiteSpace(videoPath)) throw new ArgumentException("Video path is required.", nameof(videoPath));

            string baseOutput = string.IsNullOrWhiteSpace(selectedOutputFolder)
                ? Path.GetDirectoryName(videoPath) ?? fallbackApplicationFolder
                : selectedOutputFolder;
            string videoName = Path.GetFileNameWithoutExtension(videoPath);

            if (rule == BatchOutputRule.PerVideoSubfolder)
                return new SliceOutputPlan(Path.Combine(baseOutput, videoName + "_Scenes"), "");

            char[] invalid = Path.GetInvalidFileNameChars();
            string safeVideoName = new string(videoName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            if (safeVideoName.Length > 80) safeVideoName = safeVideoName.Substring(0, 80);
            if (safeVideoName.Length == 0) safeVideoName = "Video";
            return new SliceOutputPlan(baseOutput, safeVideoName + "_");
        }
    }
}
