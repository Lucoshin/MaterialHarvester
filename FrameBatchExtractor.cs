using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace VideoToMaterial
{
    internal static class FrameBatchExtractor
    {
        public static FrameBatch Extract(string ffmpegPath, string videoPath, int width, int height, double fps)
        {
            int frameBytes = checked(width * height * 3);
            FrameBatch batch = new FrameBatch();

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(videoPath);
            startInfo.ArgumentList.Add("-vf");
            startInfo.ArgumentList.Add($"fps={fps.ToString("0.###", CultureInfo.InvariantCulture)},scale={width}:{height}");
            startInfo.ArgumentList.Add("-pix_fmt");
            startInfo.ArgumentList.Add("rgb24");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("rawvideo");
            startInfo.ArgumentList.Add("pipe:1");

            using Process process = Process.Start(startInfo);
            if (process == null) throw new InvalidOperationException("FFmpeg 采样进程启动失败。");

            byte[] buffer = new byte[frameBytes];
            while (true)
            {
                int read = ReadExact(process.StandardOutput.BaseStream, buffer, frameBytes);
                if (read == 0) break;
                if (read != frameBytes) break;

                byte[] frame = new byte[frameBytes];
                Buffer.BlockCopy(buffer, 0, frame, 0, frameBytes);
                batch.Frames.Add(frame);
            }

            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                batch.Dispose();
                string message = string.IsNullOrWhiteSpace(error) ? "无错误输出" : error.Trim();
                throw new InvalidOperationException($"FFmpeg 采样失败 (代码 {process.ExitCode}): {message}");
            }

            return batch;
        }

        private static int ReadExact(Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);
                if (read == 0) break;
                total += read;
            }
            return total;
        }
    }
}
