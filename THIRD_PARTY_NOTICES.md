# Third-Party Notices

Material Harvester 源码采用 MIT 许可证。第三方工具和库各自的许可证仍然适用，分发说明保留在 `packaging/licenses/` 中。

## 当前发行包（NoAI）

绿色包包含 .NET 运行时、FFmpeg、ffprobe、yt-dlp 和 Deno。`MaterialHarvester-win-x64.exe` 是 .NET 自包含单文件应用，不是自解压安装器；视频工具需要放在同目录。

- **FFmpeg / ffprobe**：Gyan FFmpeg 9.0.1 Essentials 静态构建，采用 GPLv3。随包保留 `FFmpeg-GPL-3.0.txt`、构建说明及对应源码说明。
- **yt-dlp**：随包版本为 2026.07.04。保留 `yt-dlp-UNLICENSE.txt` 和 `yt-dlp-THIRD_PARTY_LICENSES.txt`。
- **Deno**：为 yt-dlp 提供 JavaScript 运行环境，采用 MIT 许可证；随包保留 `DENO-LICENSE.txt`。项目源码：https://github.com/denoland/deno。
- **.NET**：包含 Windows 桌面运行时，用于无需预装 .NET 的自包含分发。运行时自带的许可证与第三方说明随包保留。
- **7-Zip**：用于创建 ZIP / 7z 压缩包；当前单文件应用不嵌入 7-Zip 自解压模块。仓库保留其许可证及历史分发说明。

## 应用依赖

- **OpenCvSharp4 / OpenCvSharp4.runtime.win**：图像分析。
- **Microsoft.ML.OnnxRuntime**：可选 ONNX 推理支持。
- **System.Drawing.Common**：图像与图标处理。
- **Microsoft.Web.WebView2**：内置平台登录界面，使用独立浏览器配置目录。随包保留 WebView2 许可证和第三方说明；WebView2 Runtime 由用户系统提供。

## 可选 AI 组件

源码保留对 Video Subtitle Remover 的可选支持。当前 NoAI 发行包不包含 VSR、Python、PyTorch、CUDA 运行库、去字幕包装器或模型权重。另行接入 AI 组件时，需保留相应项目、运行库与模型的许可说明；ProPainter 的非商用代码和权重不随公开发行包分发。

`models/*.onnx` 不纳入源码仓库或当前发行包。
