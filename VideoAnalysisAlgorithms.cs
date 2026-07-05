using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace VideoToMaterial
{
    public static class VideoAnalysisAlgorithms
    {
        private const int HashSize = 32;
        private const int LowFrequencySize = 8;

        public static ulong CalculateLowFrequencyDctHash(Bitmap image)
        {
            if (TryCalculateOpenCvDctHash(image, out ulong openCvHash))
            {
                return openCvHash;
            }

            using Bitmap resized = Resize(image, HashSize, HashSize);
            float[] gray = ReadGrayscale(resized);
            float[,] lowFrequency = CalculateLowFrequencyDct(gray, HashSize, LowFrequencySize);

            float avg = 0;
            for (int y = 0; y < LowFrequencySize; y++)
            {
                for (int x = 0; x < LowFrequencySize; x++)
                {
                    avg += lowFrequency[x, y];
                }
            }
            avg /= LowFrequencySize * LowFrequencySize;

            ulong hash = 0;
            for (int y = 0; y < LowFrequencySize; y++)
            {
                for (int x = 0; x < LowFrequencySize; x++)
                {
                    if (lowFrequency[x, y] > avg)
                    {
                        hash |= 1UL << (y * LowFrequencySize + x);
                    }
                }
            }

            return hash;
        }

        public static bool TryCalculateOpenCvDctHash(Bitmap image, out ulong hash)
        {
            hash = 0;

            try
            {
                using Bitmap converted = Ensure32Bpp(image);
                byte[] pixels = ReadBgraPixels(converted, out int stride, out int width, out int height);
                using Mat bgra = Mat.FromPixelData(height, width, MatType.CV_8UC4, pixels, stride);
                using Mat gray = new Mat();
                using Mat resized = new Mat();
                using Mat normalized = new Mat();
                using Mat dct = new Mat();

                Cv2.CvtColor(bgra, gray, ColorConversionCodes.BGRA2GRAY);
                Cv2.Resize(gray, resized, new OpenCvSharp.Size(HashSize, HashSize), 0, 0, InterpolationFlags.Cubic);
                resized.ConvertTo(normalized, MatType.CV_32F, 1.0 / 255.0);
                Cv2.Dct(normalized, dct);

                float avg = 0;
                for (int y = 0; y < LowFrequencySize; y++)
                {
                    for (int x = 0; x < LowFrequencySize; x++)
                    {
                        avg += dct.At<float>(y, x);
                    }
                }
                avg /= LowFrequencySize * LowFrequencySize;

                for (int y = 0; y < LowFrequencySize; y++)
                {
                    for (int x = 0; x < LowFrequencySize; x++)
                    {
                        if (dct.At<float>(y, x) > avg)
                        {
                            hash |= 1UL << (y * LowFrequencySize + x);
                        }
                    }
                }

                return true;
            }
            catch
            {
                hash = 0;
                return false;
            }
        }

        public static float[] CalculateColorHistogram(Bitmap image)
        {
            using Bitmap small = Resize(image, 16, 16);
            byte[] pixels = ReadBgraPixels(small, out int stride, out int width, out int height);
            float[] hist = new float[24];

            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int offset = row + x * 4;
                    int b = pixels[offset] / 32;
                    int g = pixels[offset + 1] / 32;
                    int r = pixels[offset + 2] / 32;
                    hist[r]++;
                    hist[8 + g]++;
                    hist[16 + b]++;
                }
            }

            float total = width * height * 3.0f;
            for (int i = 0; i < hist.Length; i++)
            {
                hist[i] /= total;
            }

            return hist;
        }

        private static Bitmap Resize(Bitmap source, int width, int height)
        {
            Bitmap resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using Graphics graphics = Graphics.FromImage(resized);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, 0, 0, width, height);
            return resized;
        }

        private static float[] ReadGrayscale(Bitmap bitmap)
        {
            byte[] pixels = ReadBgraPixels(bitmap, out int stride, out int width, out int height);
            float[] gray = new float[width * height];

            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                int targetRow = y * width;
                for (int x = 0; x < width; x++)
                {
                    int offset = row + x * 4;
                    byte b = pixels[offset];
                    byte g = pixels[offset + 1];
                    byte r = pixels[offset + 2];
                    gray[targetRow + x] = (0.299f * r + 0.587f * g + 0.114f * b) / 255.0f;
                }
            }

            return gray;
        }

        private static byte[] ReadBgraPixels(Bitmap source, out int stride, out int width, out int height)
        {
            width = source.Width;
            height = source.Height;
            using Bitmap bitmap = Ensure32Bpp(source);
            Rectangle rect = new Rectangle(0, 0, width, height);
            BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                stride = data.Stride;
                byte[] pixels = new byte[Math.Abs(stride) * height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                return pixels;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        private static Bitmap Ensure32Bpp(Bitmap source)
        {
            if (source.PixelFormat == PixelFormat.Format32bppArgb)
            {
                return (Bitmap)source.Clone();
            }

            Bitmap converted = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using Graphics graphics = Graphics.FromImage(converted);
            graphics.DrawImage(source, 0, 0, source.Width, source.Height);
            return converted;
        }

        private static float[,] CalculateLowFrequencyDct(float[] input, int size, int coefficientCount)
        {
            float[,] output = new float[coefficientCount, coefficientCount];
            double c0 = Math.Sqrt(1.0 / size);
            double c = Math.Sqrt(2.0 / size);

            for (int u = 0; u < coefficientCount; u++)
            {
                double au = u == 0 ? c0 : c;
                for (int v = 0; v < coefficientCount; v++)
                {
                    double av = v == 0 ? c0 : c;
                    double sum = 0;

                    for (int y = 0; y < size; y++)
                    {
                        double cosY = Math.Cos((2 * y + 1) * v * Math.PI / (2 * size));
                        int row = y * size;
                        for (int x = 0; x < size; x++)
                        {
                            double cosX = Math.Cos((2 * x + 1) * u * Math.PI / (2 * size));
                            sum += input[row + x] * cosX * cosY;
                        }
                    }

                    output[u, v] = (float)(au * av * sum);
                }
            }

            return output;
        }
    }
}
