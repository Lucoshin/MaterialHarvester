using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace VideoToMaterial
{
    public sealed class CompositionOptions
    {
        public int OutputCount { get; set; } = 100;
        public int ClipsPerOutput { get; set; } = 50;
        public int Seed { get; set; } = Environment.TickCount;
        public string OutputDirectory { get; set; } = "";
        public string FileNamePrefix { get; set; } = "Mix";
        public bool MuteAudio { get; set; }
        public int MaxParallelism { get; set; } = 2;
        public EncoderProfile EncoderProfile { get; set; }
    }

    public sealed class CompositionJob
    {
        public int Index { get; }
        public string OutputPath { get; }
        public IReadOnlyList<string> Clips { get; }
        public string Status { get; set; } = "Pending";
        public string Error { get; set; } = "";

        public CompositionJob(int index, string outputPath, IEnumerable<string> clips)
        {
            Index = index;
            OutputPath = outputPath ?? "";
            Clips = new ReadOnlyCollection<string>((clips ?? throw new ArgumentNullException(nameof(clips))).ToList());
        }
    }

    public sealed class CompositionPlan
    {
        public int Seed { get; }
        public IReadOnlyList<CompositionJob> Jobs { get; }
        public IReadOnlyDictionary<string, int> UsageCounts { get; }

        public CompositionPlan(int seed, IEnumerable<CompositionJob> jobs, IDictionary<string, int> usageCounts)
        {
            Seed = seed;
            Jobs = new ReadOnlyCollection<CompositionJob>(jobs.ToList());
            UsageCounts = new ReadOnlyDictionary<string, int>(
                new Dictionary<string, int>(usageCounts, StringComparer.OrdinalIgnoreCase));
        }
    }

    public sealed class RandomCompositionPlanner
    {
        public CompositionPlan CreatePlan(IEnumerable<string> clipPaths, CompositionOptions options)
        {
            if (clipPaths == null) throw new ArgumentNullException(nameof(clipPaths));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.OutputCount < 1 || options.OutputCount > 5000)
                throw new ArgumentOutOfRangeException(nameof(options.OutputCount), "Output count must be between 1 and 5000.");
            if (options.ClipsPerOutput < 1)
                throw new ArgumentOutOfRangeException(nameof(options.ClipsPerOutput));

            string[] clips = clipPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (clips.Length < options.ClipsPerOutput)
                throw new ArgumentException("The clip library is smaller than the requested clips per output.", nameof(clipPaths));
            if (!HasAtLeastCombinations(clips.Length, options.ClipsPerOutput, options.OutputCount))
                throw new ArgumentException("The clip library cannot produce the requested number of unique combinations.", nameof(options.OutputCount));

            var random = new Random(options.Seed);
            var usage = clips.ToDictionary(path => path, _ => 0, StringComparer.OrdinalIgnoreCase);
            var pairUsage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var signatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var jobs = new List<CompositionJob>(options.OutputCount);

            for (int jobIndex = 1; jobIndex <= options.OutputCount; jobIndex++)
            {
                List<string> selected = SelectUniqueSet(clips, options.ClipsPerOutput, usage, pairUsage, signatures, random);
                string signature = CreateSignature(selected);
                signatures.Add(signature);

                foreach (string clip in selected) usage[clip]++;
                RecordPairs(selected, pairUsage);
                Shuffle(selected, random);

                string prefix = SanitizePrefix(options.FileNamePrefix);
                string outputName = $"{prefix}_{jobIndex:D5}.mp4";
                string outputPath = string.IsNullOrWhiteSpace(options.OutputDirectory)
                    ? outputName
                    : Path.Combine(options.OutputDirectory, outputName);
                jobs.Add(new CompositionJob(jobIndex, outputPath, selected));
            }

            return new CompositionPlan(options.Seed, jobs, usage);
        }

        private static List<string> SelectUniqueSet(
            string[] clips,
            int count,
            IReadOnlyDictionary<string, int> usage,
            IReadOnlyDictionary<string, int> pairUsage,
            ISet<string> signatures,
            Random random)
        {
            for (int attempt = 0; attempt < 10000; attempt++)
            {
                var selected = new List<string>(count);
                while (selected.Count < count)
                {
                    int minimumUsage = clips.Where(clip => !selected.Contains(clip, StringComparer.OrdinalIgnoreCase))
                        .Min(clip => usage[clip]);
                    string[] candidates = clips
                        .Where(clip => usage[clip] == minimumUsage && !selected.Contains(clip, StringComparer.OrdinalIgnoreCase))
                        .ToArray();

                    if (candidates.Length == 0)
                    {
                        candidates = clips.Where(clip => !selected.Contains(clip, StringComparer.OrdinalIgnoreCase)).ToArray();
                    }

                    int bestPairScore = candidates.Min(candidate => PairScore(candidate, selected, pairUsage));
                    string[] best = candidates
                        .Where(candidate => PairScore(candidate, selected, pairUsage) == bestPairScore)
                        .ToArray();
                    selected.Add(best[random.Next(best.Length)]);
                }

                if (!signatures.Contains(CreateSignature(selected))) return selected;
            }

            throw new InvalidOperationException("Unable to create another unique balanced composition. Try a larger clip library.");
        }

        private static int PairScore(string candidate, IEnumerable<string> selected, IReadOnlyDictionary<string, int> pairUsage)
        {
            int score = 0;
            foreach (string clip in selected)
            {
                pairUsage.TryGetValue(PairKey(candidate, clip), out int count);
                score += count;
            }
            return score;
        }

        private static void RecordPairs(IReadOnlyList<string> clips, IDictionary<string, int> pairUsage)
        {
            for (int i = 0; i < clips.Count; i++)
            {
                for (int j = i + 1; j < clips.Count; j++)
                {
                    string key = PairKey(clips[i], clips[j]);
                    pairUsage.TryGetValue(key, out int count);
                    pairUsage[key] = count + 1;
                }
            }
        }

        private static string PairKey(string first, string second) =>
            string.Compare(first, second, StringComparison.OrdinalIgnoreCase) <= 0
                ? first + "\n" + second
                : second + "\n" + first;

        private static string CreateSignature(IEnumerable<string> clips) =>
            string.Join("\n", clips.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));

        private static void Shuffle<T>(IList<T> values, Random random)
        {
            for (int i = values.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (values[i], values[j]) = (values[j], values[i]);
            }
        }

        private static bool HasAtLeastCombinations(int n, int k, int required)
        {
            k = Math.Min(k, n - k);
            long combinations = 1;
            for (int i = 1; i <= k; i++)
            {
                combinations = combinations * (n - k + i) / i;
                if (combinations >= required) return true;
            }
            return combinations >= required;
        }

        private static string SanitizePrefix(string prefix)
        {
            string value = string.IsNullOrWhiteSpace(prefix) ? "Mix" : prefix.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            return value;
        }
    }
}
