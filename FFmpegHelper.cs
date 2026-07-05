using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace VideoToMaterial
{
    // 定义编码配置结构
    public class EncoderProfile
    {
        public string Name { get; set; }        // 显卡名字
        public string VideoCodec { get; set; }  // FFmpeg 参数
        public string ExtraArgs { get; set; }   // 额外参数
        public bool IsHardware { get; set; }    // 是否是硬件加速
    }

    public class FFmpegHelper
    {
        private string _ffmpegPath;

        public FFmpegHelper(string ffmpegPath)
        {
            _ffmpegPath = ffmpegPath;
        }

        // 自动检测最佳编码器
        public async Task<EncoderProfile> DetectBestEncoderAsync()
        {
            // 1. 尝试 NVIDIA (N卡)
            if (await TestEncoder("h264_nvenc"))
                return new EncoderProfile { Name = "NVIDIA GPU (NVENC)", VideoCodec = "h264_nvenc", ExtraArgs = "-preset p4 -cq 25", IsHardware = true };

            // 2. 尝试 Intel 核显 (QSV)
            if (await TestEncoder("h264_qsv"))
                return new EncoderProfile { Name = "Intel GPU (QSV)", VideoCodec = "h264_qsv", ExtraArgs = "-global_quality 25 -preset medium", IsHardware = true };

            // 3. 尝试 AMD (A卡)
            if (await TestEncoder("h264_amf"))
                return new EncoderProfile { Name = "AMD GPU (AMF)", VideoCodec = "h264_amf", ExtraArgs = "-quality speed", IsHardware = true };

            // 4. 兜底使用 CPU
            return new EncoderProfile { Name = "CPU (libx264)", VideoCodec = "libx264", ExtraArgs = "-preset ultrafast -crf 23", IsHardware = false };
        }

        private Task<bool> TestEncoder(string encoderName)
        {
            return Task.Run(() =>
            {
                try
                {
                    // 尝试编码1秒黑屏视频
                    string args = $"-f lavfi -i color=c=black:s=640x480:d=1 -c:v {encoderName} -f null - -y";
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = _ffmpegPath,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true
                    };
                    using (Process p = Process.Start(psi))
                    {
                        // 必须读取输出流，否则如果缓冲区满了会导致死锁
                        string output = p.StandardError.ReadToEnd();
                        p.WaitForExit();
                        return p.ExitCode == 0; // 成功返回 0
                    }
                }
                catch { return false; }
            });
        }
    }
}