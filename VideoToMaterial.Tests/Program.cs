using System;
using System.Drawing;
using System.IO;
using System.Linq;
using VideoToMaterial;

namespace VideoToMaterial.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            Run("LowFrequencyDctHash returns stable value for same bitmap", LowFrequencyDctHashReturnsStableValue);
            Run("OpenCvSharp DCT backend is available", OpenCvSharpDctBackendIsAvailable);
            Run("Histogram sums all sampled pixels", HistogramSumsSampledPixels);
            Run("TransNetV2 model metadata is readable", TransNetV2ModelMetadataIsReadable);
            Run("TransNetV2 decodes contiguous score ranges into cuts", TransNetV2DecodesScoreRanges);
            Run("TransNetV2 runs inference on sampled video", TransNetV2RunsInferenceOnSampledVideo);
            Console.WriteLine("All tests passed.");
            return 0;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
                Environment.Exit(1);
            }
        }

        private static void LowFrequencyDctHashReturnsStableValue()
        {
            using Bitmap bitmap = CreateGradientBitmap(64, 64);
            ulong first = VideoAnalysisAlgorithms.CalculateLowFrequencyDctHash(bitmap);
            ulong second = VideoAnalysisAlgorithms.CalculateLowFrequencyDctHash(bitmap);
            Assert(first == second, "same bitmap should produce same hash");
            Assert(first != 0, "gradient hash should not be zero");
        }

        private static void HistogramSumsSampledPixels()
        {
            using Bitmap bitmap = CreateGradientBitmap(32, 32);
            float[] histogram = VideoAnalysisAlgorithms.CalculateColorHistogram(bitmap);
            float sum = 0;
            foreach (float value in histogram) sum += value;
            Assert(Math.Abs(sum - 1.0f) < 0.001f, $"histogram should be normalized, got {sum}");
        }

        private static void OpenCvSharpDctBackendIsAvailable()
        {
            using Bitmap bitmap = CreateGradientBitmap(64, 64);
            bool ok = VideoAnalysisAlgorithms.TryCalculateOpenCvDctHash(bitmap, out ulong hash);
            Assert(ok, "OpenCvSharp backend should be available");
            Assert(hash != 0, "OpenCvSharp hash should not be zero");
        }

        private static void TransNetV2ModelMetadataIsReadable()
        {
            string modelPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "models", "transnetv2.onnx"));
            Assert(File.Exists(modelPath), $"missing model: {modelPath}");

            using TransNetV2DetectorService service = new TransNetV2DetectorService(modelPath);
            TransNetV2ModelInfo info = service.GetModelInfo();

            Assert(!string.IsNullOrWhiteSpace(info.InputName), "input name should be present");
            Assert(info.InputDimensions.Length == 5, "input should be 5D");
            Assert(info.InputDimensions.Contains(TransNetV2DetectorService.WindowSize), "input should contain 100-frame window");
            Assert(info.OutputNames.Length > 0, "outputs should be present");
        }

        private static void TransNetV2DecodesScoreRanges()
        {
            string modelPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "models", "transnetv2.onnx"));
            using TransNetV2DetectorService service = new TransNetV2DetectorService(modelPath);

            float[] scores = new float[30];
            scores[10] = 0.7f;
            scores[11] = 0.8f;
            scores[20] = 0.9f;

            var cuts = service.DecodeSceneCuts(scores, fps: 10, threshold: 0.5);
            Assert(cuts.Count == 2, $"expected 2 cuts, got {cuts.Count}");
            Assert(Math.Abs(cuts[0] - 1.0) < 0.11, $"first cut should be around 1.0s, got {cuts[0]}");
            Assert(Math.Abs(cuts[1] - 2.0) < 0.11, $"second cut should be around 2.0s, got {cuts[1]}");
        }

        private static void TransNetV2RunsInferenceOnSampledVideo()
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            string modelPath = Path.Combine(root, "models", "transnetv2.onnx");
            string ffmpegPath = Path.Combine(root, "bin", "Release", "ffmpeg.exe");
            Assert(File.Exists(ffmpegPath), $"missing ffmpeg: {ffmpegPath}");

            string tempDir = Path.Combine(Path.GetTempPath(), "VideoToMaterialTransNetV2Test");
            Directory.CreateDirectory(tempDir);
            string videoPath = Path.Combine(tempDir, "transnetv2-test.mp4");
            RunProcess(ffmpegPath, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=25", "-t", "4", "-pix_fmt", "yuv420p", "-y", videoPath);

            using TransNetV2DetectorService service = new TransNetV2DetectorService(modelPath);
            var cuts = service.DetectAsync(ffmpegPath, videoPath, fps: 25, threshold: 0.5).GetAwaiter().GetResult();
            Assert(cuts != null, "cuts should not be null");
        }

        private static void RunProcess(string fileName, params string[] args)
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            foreach (string arg in args) startInfo.ArgumentList.Add(arg);

            using var process = System.Diagnostics.Process.Start(startInfo);
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert(process.ExitCode == 0, error);
        }

        private static Bitmap CreateGradientBitmap(int width, int height)
        {
            Bitmap bitmap = new Bitmap(width, height);
            using Graphics graphics = Graphics.FromImage(bitmap);
            for (int y = 0; y < height; y++)
            {
                int shade = (int)(255.0 * y / Math.Max(1, height - 1));
                using Pen pen = new Pen(Color.FromArgb(shade, 255 - shade, shade / 2));
                graphics.DrawLine(pen, 0, y, width, y);
            }
            return bitmap;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
