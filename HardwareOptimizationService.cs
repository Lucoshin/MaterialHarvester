using System;
using System.Runtime.InteropServices;

namespace VideoToMaterial
{
    public class HardwareOptimizationService
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public MEMORYSTATUSEX()
            {
                this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        /// <summary>
        /// 计算最佳并发数
        /// </summary>
        /// <param name="profile">当前编码器配置</param>
        /// <returns>
        /// suggestedCount: 建议并发数
        /// reason: 限制原因
        /// systemInfo: 系统硬件信息摘要
        /// </returns>
        public (int suggestedCount, string reason, string systemInfo) CalculateOptimalThreadCount(EncoderProfile profile)
        {
            int coreCount = Environment.ProcessorCount;
            int suggested = Math.Max(1, coreCount / 2); // 基础建议：核心数的一半
            
            // 1. 获取物理内存信息
            ulong totalRamGB = 0;
            try
            {
                var memStatus = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(memStatus))
                {
                    totalRamGB = memStatus.ullTotalPhys / (1024 * 1024 * 1024);
                }
            }
            catch { /* Ignore */ }

            // 2. 基于内存的并发限制 (防止内存溢出)
            // 视频处理非常吃内存，保守估计每路并发建议预留 2-3GB
            int memoryLimit = 4; // 默认
            if (totalRamGB > 0)
            {
                if (totalRamGB < 8) memoryLimit = 1;        // <8GB: 1路 (保命要紧)
                else if (totalRamGB < 12) memoryLimit = 2;  // 8-12GB: 2路
                else if (totalRamGB < 16) memoryLimit = 3;  // 12-16GB: 3路
                else if (totalRamGB < 32) memoryLimit = 6;  // 16-32GB: 6路
                else memoryLimit = 16;                      // 32GB+: 豪横
            }

            // 3. 取 CPU 和 内存 限制的较小值
            suggested = Math.Min(suggested, memoryLimit);

            // 4. 硬件编码器的特殊限制 (显存/Session限制)
            string reason = "";
            if (profile != null && profile.IsHardware)
            {
                // 硬件加速通常受限于显卡Session数
                // 消费级显卡通常限制 3-5 路，这里取保守值
                int hwLimit = (totalRamGB >= 32) ? 5 : 3; 
                
                if (suggested > hwLimit)
                {
                    suggested = hwLimit;
                    reason = $"(受限于显卡并发能力, 限制为 {hwLimit})";
                }
            }
            else
            {
                // 纯 CPU 模式，如果内存小，严格限制
                if (totalRamGB > 0 && totalRamGB < 16 && suggested > 4) 
                {
                    suggested = 4;
                    reason = "(受限于内存, 限制为 4)";
                }
            }

            // 确保至少有1个
            if (suggested < 1) suggested = 1;

            string systemInfo = $"CPU核心: {coreCount}, 内存: {(totalRamGB > 0 ? totalRamGB + "GB" : "Unknown")}";
            return (suggested, reason, systemInfo);
        }
    }
}
