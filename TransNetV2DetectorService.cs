using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace VideoToMaterial
{
    public sealed class TransNetV2DetectorService : IDisposable
    {
        public const int WindowSize = 100;
        public const int InputWidth = 48;
        public const int InputHeight = 27;

        private readonly string _modelPath;
        private readonly InferenceSession _session;

        public TransNetV2DetectorService(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath)) throw new ArgumentNullException(nameof(modelPath));
            if (!File.Exists(modelPath)) throw new FileNotFoundException("未找到 TransNetV2 ONNX 模型。", modelPath);

            _modelPath = modelPath;
            _session = new InferenceSession(modelPath);
        }

        public TransNetV2ModelInfo GetModelInfo()
        {
            var input = _session.InputMetadata.First();
            var outputs = _session.OutputMetadata.Select(kvp => kvp.Key).ToArray();
            return new TransNetV2ModelInfo
            {
                ModelPath = _modelPath,
                InputName = input.Key,
                InputDimensions = input.Value.Dimensions?.ToArray() ?? Array.Empty<int>(),
                OutputNames = outputs
            };
        }

        public List<double> DecodeSceneCuts(IReadOnlyList<float> frameScores, double fps, double threshold = 0.5)
        {
            if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
            List<double> cuts = new List<double>();
            bool inCut = false;
            int segmentStart = 0;

            for (int i = 0; i < frameScores.Count; i++)
            {
                bool above = frameScores[i] >= threshold;
                if (above && !inCut)
                {
                    inCut = true;
                    segmentStart = i;
                }
                else if (!above && inCut)
                {
                    AddCut(cuts, segmentStart, i - 1, fps);
                    inCut = false;
                }
            }

            if (inCut)
            {
                AddCut(cuts, segmentStart, frameScores.Count - 1, fps);
            }

            return cuts;
        }

        public Task<List<double>> DetectAsync(string ffmpegPath, string videoPath, double fps = 25.0, double threshold = 0.5)
        {
            return Task.Run(() =>
            {
                if (!File.Exists(ffmpegPath)) throw new FileNotFoundException("未找到 ffmpeg.exe。", ffmpegPath);
                if (!File.Exists(videoPath)) throw new FileNotFoundException("未找到视频文件。", videoPath);

                using FrameBatch frames = FrameBatchExtractor.Extract(ffmpegPath, videoPath, InputWidth, InputHeight, fps);
                if (frames.Count == 0) return new List<double>();

                List<float> scores = new List<float>();
                int offset = 0;
                while (offset < frames.Count)
                {
                    DenseTensor<float> tensor = BuildInputTensor(frames, offset);
                    string inputName = _session.InputMetadata.Keys.First();
                    using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(new[]
                    {
                        NamedOnnxValue.CreateFromTensor(inputName, tensor)
                    });

                    Tensor<float> output = results.First().AsTensor<float>();
                    int usable = Math.Min(WindowSize, frames.Count - offset);
                    for (int i = 0; i < usable; i++)
                    {
                        scores.Add(ReadScore(output, i));
                    }

                    offset += WindowSize;
                }

                return DecodeSceneCuts(scores, fps, threshold);
            });
        }

        private static DenseTensor<float> BuildInputTensor(FrameBatch frames, int offset)
        {
            DenseTensor<float> tensor = new DenseTensor<float>(new[] { 1, WindowSize, InputHeight, InputWidth, 3 });

            for (int i = 0; i < WindowSize; i++)
            {
                int frameIndex = Math.Min(frames.Count - 1, offset + i);
                byte[] frame = frames.Frames[frameIndex];
                int pixel = 0;
                for (int y = 0; y < InputHeight; y++)
                {
                    for (int x = 0; x < InputWidth; x++)
                    {
                        tensor[0, i, y, x, 0] = frame[pixel++] / 255.0f;
                        tensor[0, i, y, x, 1] = frame[pixel++] / 255.0f;
                        tensor[0, i, y, x, 2] = frame[pixel++] / 255.0f;
                    }
                }
            }

            return tensor;
        }

        private static float ReadScore(Tensor<float> output, int frameIndex)
        {
            int[] dims = output.Dimensions.ToArray();
            if (dims.Length == 3 && dims[0] == 1 && dims[2] >= 1)
            {
                return output[0, frameIndex, 0];
            }
            if (dims.Length == 2 && dims[0] == 1)
            {
                return output[0, frameIndex];
            }
            if (dims.Length == 2)
            {
                return output[frameIndex, 0];
            }
            return output.ToArray()[Math.Min(frameIndex, output.Length - 1)];
        }

        private static void AddCut(List<double> cuts, int start, int end, double fps)
        {
            int center = (start + end) / 2;
            double seconds = center / fps;
            if (seconds > 0.05 && (cuts.Count == 0 || seconds - cuts[cuts.Count - 1] > 0.25))
            {
                cuts.Add(seconds);
            }
        }

        public void Dispose()
        {
            _session?.Dispose();
        }
    }

    public sealed class TransNetV2ModelInfo
    {
        public string ModelPath { get; set; }
        public string InputName { get; set; }
        public int[] InputDimensions { get; set; }
        public string[] OutputNames { get; set; }
    }

    internal sealed class FrameBatch : IDisposable
    {
        public List<byte[]> Frames { get; } = new List<byte[]>();
        public int Count => Frames.Count;

        public void Dispose()
        {
            Frames.Clear();
        }
    }
}
