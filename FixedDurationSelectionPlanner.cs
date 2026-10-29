using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoToMaterial
{
    public enum FixedClipSelectionMode
    {
        Automatic,
        StartOnly,
        StartAndEnd,
        StartMiddleAndEnd
    }

    public readonly struct FixedClipRange
    {
        public FixedClipRange(double startSeconds, double durationSeconds)
        {
            StartSeconds = startSeconds;
            DurationSeconds = durationSeconds;
        }

        public double StartSeconds { get; }
        public double DurationSeconds { get; }
    }

    public static class FixedDurationSelectionPlanner
    {
        private const double MinimumAutomaticRemainderSeconds = 0.5;
        private const double TimeToleranceSeconds = 0.001;

        public static IReadOnlyList<FixedClipRange> Create(
            double videoDurationSeconds,
            double clipDurationSeconds,
            FixedClipSelectionMode mode)
        {
            if (videoDurationSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(videoDurationSeconds));
            if (clipDurationSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(clipDurationSeconds));
            if (!Enum.IsDefined(mode))
                throw new ArgumentOutOfRangeException(nameof(mode));

            double duration = Math.Min(videoDurationSeconds, clipDurationSeconds);
            if (mode == FixedClipSelectionMode.Automatic)
            {
                var automatic = new List<FixedClipRange>();
                for (double start = 0; start < videoDurationSeconds; start += clipDurationSeconds)
                {
                    double remaining = videoDurationSeconds - start;
                    double rangeDuration = Math.Min(clipDurationSeconds, remaining);
                    if (rangeDuration < MinimumAutomaticRemainderSeconds) break;
                    automatic.Add(new FixedClipRange(start, rangeDuration));
                }
                return automatic;
            }

            double lastStart = Math.Max(0, videoDurationSeconds - duration);
            IEnumerable<double> starts = mode switch
            {
                FixedClipSelectionMode.StartOnly => new[] { 0.0 },
                FixedClipSelectionMode.StartAndEnd => new[] { 0.0, lastStart },
                FixedClipSelectionMode.StartMiddleAndEnd => new[] { 0.0, lastStart / 2.0, lastStart },
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };

            var selected = new List<FixedClipRange>();
            foreach (double start in starts.OrderBy(value => value))
            {
                if (selected.Count > 0
                    && start < selected[selected.Count - 1].StartSeconds
                        + selected[selected.Count - 1].DurationSeconds
                        - TimeToleranceSeconds)
                {
                    continue;
                }

                selected.Add(new FixedClipRange(start, duration));
            }
            return selected;
        }
    }
}
