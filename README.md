# Material Harvester

**Language:** [中文](#中文) | [English](#english)

---

## 中文

Material Harvester 是一款面向视频创作者的 Windows 桌面工具，用于把长视频快速整理成可复用的素材片段。它支持基于画面变化的智能场景切分、固定时长切分、相似片段去重、批量处理和可选的网络视频下载。

> 当前项目名仍为 `VideoToMaterial`，应用对外名称为 `Material Harvester`。

### 功能特性

- **智能场景切分**：基于 FFmpeg 的 `scene` 画面变化检测识别镜头切点。
- **自适应补切**：`AdaptiveDetector` 会在默认检测基础上追加一轮更敏感的弱切点检测，降低漏切概率。
- **固定时长切分**：既可从头到尾自动连续切片，也可只保留开头、首尾或首中尾的指定长度片段。
- **相似片段去重**：使用 pHash（感知哈希）和颜色直方图判断相邻或历史场景是否重复。
- **批量处理队列**：支持多视频拖拽导入、缩略图预览、单个视频移除和并发导出。
- **网络视频导入**：集成新版 `yt-dlp` 与 Deno JS 运行时，粘贴视频链接后下载并可自动加入处理队列；支持抖音、小红书、B站的内置扫码登录，也可从 Edge、Chrome、Firefox 或 Netscape Cookie 文件读取登录态。
- **可靠随机成片**：每条成片内部不重复；大量短片按最多 8 路分组编码，优先使用已检测到的 QSV/NVENC/AMF，失败时自动回退 CPU 快速模式，最终完整解码与时长验收通过后才生成正式文件。整目录存在 `_Scene_` 切片时只导入切片，避免误把长原片混入素材库。
- **AI 去字幕**：可选调用本地 Video Subtitle Remover，默认动态检测字幕，也可框选检测区域降低误删风险；程序先在原片上规划最终片段，再仅对保留时间范围执行检测与修复（侧车支持范围包装器时）。
- **硬件编码优先**：自动检测可用编码器，优先使用 NVIDIA NVENC 等硬件编码能力。
- **现代 WPF 界面**：深色界面、拖拽上传区、折叠日志、参数说明 ToolTip。

### 当前检测实现

Material Harvester 当前不是一个完全基于深度学习的视频检测器。

| 模式 | 实现方式 | 说明 |
| --- | --- | --- |
| 智能场景 | FFmpeg `select='gt(scene,threshold)'` | 主切点来源，灵敏度会转换为 FFmpeg scene 阈值 |
| AdaptiveDetector | 两轮 FFmpeg scene 检测 + 合并规则 | 第二轮使用更敏感阈值补充弱切点 |
| 固定时长 | 本地时长计算 + FFmpeg 裁切 | 不做场景识别和去重 |
| 去重 | pHash + 颜色直方图 | 用于合并或丢弃相似场景 |
| HighPrecision | 预留入口 | `TransNetV2DetectorService` 已存在，但主 UI 默认未启用 |

### 系统要求

- Windows 10/11
- .NET 8 SDK 或 Visual Studio 2022（开发构建）
- `ffmpeg.exe`（必需，运行时需要）
- `ffprobe.exe`（可选，用于更稳定地读取视频时长）
- `yt-dlp.exe`（可选，用于链接下载）
- `deno.exe`（YouTube 完整解析所需，绿色包已包含）
- Microsoft Edge WebView2 Runtime（平台内置扫码登录使用；Windows 10/11 通常已安装）
- Video Subtitle Remover（可选，用于 AI 去除硬字幕；AI 完整绿色包已内置）

### 快速开始

1. 克隆仓库：

```powershell
git clone <your-repo-url>
cd VideoToMaterial
```

2. 还原并构建：

```powershell
dotnet restore VideoToMaterial.sln
dotnet build VideoToMaterial.csproj -c Release
```

3. 本地源码构建时，将运行时工具放到输出目录：

```text
bin\Release\net8.0-windows\
  ffmpeg.exe
  ffprobe.exe
  yt-dlp.exe
  deno.exe
```

4. 运行：

```powershell
.\bin\Release\net8.0-windows\VideoToMaterial.exe
```

### 使用方式

1. 将一个或多个视频拖入主窗口。
2. 选择输出目录；默认会在源视频同级目录生成 `[视频名]_Scenes` 文件夹。
3. 选择切片模式：
   - `智能场景`：自动识别镜头变化并执行相似片段去重。
   - `固定时长`：设置每段秒数，并选择自动全部、仅开头、开头与结尾、开头中间与结尾四种取片方式。
4. 调整灵敏度、最小时长、并发数和静音选项。固定位置片段发生重叠时，只保留不重叠的片段；视频不足一段时输出实际剩余长度。
5. 如需去除硬字幕，勾选“AI 去字幕”；默认自动检测，也可以选择“框选区域”，在首个视频预览上拖出绿色矩形后确认。
6. 点击“开始批量处理”。

下载抖音、小红书或 B站链接时，Cookie 来源可选择“平台扫码登录（推荐）”。程序会识别链接平台，在独立的内置浏览器窗口中打开对应网站，扫码成功后自动继续下载；三个平台的登录环境分别保存在 `%LocalAppData%\MaterialHarvester\PlatformWebView2` 下，不会读取或占用 Edge/Chrome 的 Cookie 数据库。传给 yt-dlp 的 Cookie 文件仅临时生成，下载批次结束即删除，Cookie 内容不会写入运行日志。如果未选择 Cookie但平台返回登录要求，或浏览器 Cookie 数据库正被占用，程序也会自动转入对应平台的扫码登录流程。

“用系统浏览器打开”仅作为登录页面打不开时的备用入口。系统浏览器与内置登录环境相互隔离；若需要使用系统浏览器已有登录态，应完全关闭对应浏览器后，在 Cookie 来源中选择 Edge、Chrome 或 Firefox。

界面会通过真实张量计算探针显示 AI 引擎实际可用的设备：`CUDA`（NVIDIA GPU）、`DirectML`（Windows GPU 通用加速）或 `CPU`。顶部的 NVENC 状态只代表视频编码加速，不代表 AI 推理已经使用 GPU。CPU 默认采用更高效的 LaMa 动态文字修复，CUDA 则采用时序一致性更强的 STTN；探针失败会自动回退 CPU，不会仅凭 DLL 文件误报 GPU。

启用 AI 去字幕时，程序会先完成切片与去重规划，再把最终保留范围交给 AI。包装器会在最终片段边界写入 IDR 关键帧，并优先使用通过实际编码探针的 NVENC 或 QSV 生成清理视频；为保证切片边界可靠，AMD AMF 当前会回退到 CPU 的 `libx264`。最后切片直接复制视频流，只重编码必要的音频，从而避免原先“整片清理编码一次、每个片段再编码一次”的二次有损压缩。

手机录屏等可变帧率（VFR）素材会在 AI 处理前自动转换为无损恒定帧率工作源，并基于同一时间轴重新规划片段，避免 OpenCV 固定帧率写回造成音画错速。转换前会检查临时磁盘空间；重置队列会取消正在运行的探测、转换、AI 和切片子进程并清理半成品。STTN 最终只在动态文字遮罩内融合，遮罩外像素保持原画面。

AI 组件目录约定（完整绿色包已经包含，用户无需安装 Python、模型或运行库）：

```text
ai\video-subtitle-remover\
  backend\main.py
  python.exe
  _internal\
```

发布时可提供 CPU 与 CUDA 两种完整绿色包。CPU 包使用用户电脑现有的 CPU 运行 LaMa，并不是附带或替代一颗“通用 CPU”；CUDA 包内置 PyTorch CUDA、cuDNN、cuBLAS 等软件运行库，用户无需安装 CUDA Toolkit，但仍需一块受支持的 NVIDIA 显卡和可用驱动。CUDA 运行库会显著增加压缩包体积。公开绿色包不包含仅限非商用的 ProPainter 代码和权重。

### 项目结构

```text
.
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / MainWindow.xaml.cs
├── VideoProcessorService.cs
├── SubtitleRemovalService.cs
├── SubtitleRegionDialog.xaml / SubtitleRegionDialog.xaml.cs
├── VideoAnalysisAlgorithms.cs
├── FFmpegHelper.cs
├── DownloadService.cs
├── HardwareOptimizationService.cs
├── FrameBatchExtractor.cs
├── TransNetV2DetectorService.cs
├── assets/
├── models/
├── VideoToMaterial.Tests/
├── VideoToMaterial.csproj
└── VideoToMaterial.sln
```

### 关键模块

| 文件 | 作用 |
| --- | --- |
| `MainWindow.xaml` / `MainWindow.xaml.cs` | WPF 界面、导入队列、参数读取、用户交互 |
| `VideoProcessorService.cs` | 场景检测、任务生成、FFmpeg 裁切导出 |
| `VideoAnalysisAlgorithms.cs` | pHash、颜色直方图和相似度计算 |
| `FFmpegHelper.cs` | 编码器检测和 FFmpeg 能力探测 |
| `DownloadService.cs` | 基于 `yt-dlp` 的链接下载 |
| `HardwareOptimizationService.cs` | CPU/GPU/内存信息与并发建议 |
| `TransNetV2DetectorService.cs` | 预留的 ONNX 高精度检测实现 |

### 构建发布包

展开式绿色版：

```powershell
dotnet publish VideoToMaterial.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -o .\dist\MaterialHarvester-Green
```

发布后将匹配版本的 `ffmpeg.exe` / `ffprobe.exe`、`yt-dlp.exe`、`deno.exe` 复制到绿色目录。当前发行门禁要求 FFmpeg/ffprobe 9.0.1、yt-dlp 2026.07.04 或更高版本，并验证这些工具在隔离系统 `PATH` 后仍可启动。AI 包还需把经过清理和命令行冒烟验证的 VSR 离线侧车放入 `ai\video-subtitle-remover` 后再压缩。

GitHub Release 中的 `MaterialHarvester-win-x64.exe` 是完整自解压版，不是仅包含 .NET 主程序的薄单文件。它内含绿色目录的全部文件；打开后选择一个目录完成解压，再运行其中的 `MaterialHarvester.exe`。它不会请求管理员权限。经常使用时建议下载 ZIP/7z，体积更小；自解压 EXE 适合电脑上没有解压软件的场景。

### 开源仓库边界

建议提交到 Git 的内容：

- C# / XAML 源码
- `.sln` / `.csproj`
- `assets/` 中的项目图标
- `models/README.md`
- `README.md`、`LICENSE`、`CONTRIBUTING.md`、`THIRD_PARTY_NOTICES.md`

不建议提交到 Git 的内容：

- `bin/`、`obj/`、`dist/`
- `.vs/`、`.codegraph/`
- `ffmpeg.exe`、`ffprobe.exe`、`yt-dlp.exe`、`deno.exe`
- `models/*.onnx`
- 下载的视频、生成的切片目录和本地测试数据

### 第三方依赖说明

本项目源码采用 MIT License。第三方工具和依赖遵循各自许可证。

- FFmpeg / ffprobe：视频处理、场景检测、缩略图提取、导出。
- yt-dlp：可选的视频链接下载。
- OpenCvSharp：图像分析加速。
- Microsoft.ML.OnnxRuntime：预留 ONNX 推理能力。

更多信息见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

### 贡献

欢迎提交 Issue 和 Pull Request。提交前请确认：

- Release 构建通过。
- 没有提交构建产物、第三方 exe、ONNX 模型或生成视频。
- 用户可见行为变化已更新 README。

详细规则见 [CONTRIBUTING.md](CONTRIBUTING.md)。

### 许可证

本项目采用 [MIT License](LICENSE)。

---

## English

Material Harvester is a Windows desktop application for creators who need to turn long videos into reusable material clips. It supports scene-based slicing, fixed-duration slicing, similarity-based deduplication, batch processing, and optional URL-based video import.

> The repository/project name is still `VideoToMaterial`; the user-facing application name is `Material Harvester`.

### Features

- **Smart scene slicing**: detects shot boundaries with FFmpeg scene-change analysis.
- **Adaptive cut recovery**: `AdaptiveDetector` runs an additional sensitive pass and merges weak cut candidates to reduce missed cuts.
- **Fixed-duration slicing**: slices continuously or keeps only a fixed-length start, start/end, or start/middle/end selection.
- **Similarity deduplication**: uses pHash and color histograms to merge or discard visually similar scenes.
- **Batch queue**: supports drag-and-drop import, thumbnail previews, per-item removal, and concurrent exports.
- **URL import**: bundles current `yt-dlp` and the Deno JavaScript runtime for full YouTube extraction, with optional automatic queue insertion and no separate tool installation.
- **Hardware encoding preference**: detects available encoders and prefers hardware acceleration such as NVIDIA NVENC when available.
- **AI subtitle removal**: optionally invokes a local Video Subtitle Remover sidecar. It dynamically detects subtitles by default, can constrain detection to a selected region, plans final clips from the original video first, and processes only retained time ranges when the range-aware wrapper is available.
- **Modern WPF UI**: dark interface, drag-and-drop source area, collapsible logs, and parameter tooltips.

### Current Detection Pipeline

Material Harvester is not currently a fully deep-learning-based scene detector.

| Mode | Implementation | Notes |
| --- | --- | --- |
| Smart Scene | FFmpeg `select='gt(scene,threshold)'` | Main source of cut timestamps; sensitivity is mapped to FFmpeg scene threshold |
| AdaptiveDetector | Two FFmpeg scene passes + merge rules | Adds weaker cut candidates from a more sensitive second pass |
| Fixed Duration | Duration calculation + FFmpeg clipping | No scene detection or deduplication |
| Deduplication | pHash + color histogram | Merges or drops visually similar scenes |
| HighPrecision | Reserved entry point | `TransNetV2DetectorService` exists, but the main UI does not enable it by default |

### Requirements

- Windows 10/11
- .NET 8 SDK or Visual Studio 2022 for development builds
- `ffmpeg.exe` required at runtime
- `ffprobe.exe` optional for more reliable duration probing
- `yt-dlp.exe` optional for URL downloads
- `deno.exe` for full YouTube JavaScript challenge support
- Video Subtitle Remover is optional for source builds; AI-enabled green packages bundle the complete offline sidecar

### Quick Start

1. Clone the repository:

```powershell
git clone <your-repo-url>
cd VideoToMaterial
```

2. Restore and build:

```powershell
dotnet restore VideoToMaterial.sln
dotnet build VideoToMaterial.csproj -c Release
```

3. Place runtime tools next to the built executable:

```text
bin\Release\net8.0-windows\
  ffmpeg.exe
  ffprobe.exe
  yt-dlp.exe
  deno.exe
```

4. Run:

```powershell
.\bin\Release\net8.0-windows\VideoToMaterial.exe
```

### Usage

1. Drag one or more videos into the main window.
2. Choose an output directory. By default, clips are written to `[VideoName]_Scenes` next to the source video.
3. Choose a split mode:
   - `Smart Scene`: detects visual shot changes and removes similar clips.
   - `Fixed Duration`: set the clip length, then choose Automatic All, Start Only, Start and End, or Start, Middle and End.
4. Adjust sensitivity, minimum duration, concurrency, and mute options. Overlapping fixed-position selections are skipped, and videos shorter than one clip produce their available duration.
5. To remove hard subtitles, enable "AI subtitle removal". Automatic detection is the default; optionally choose "Selected region", drag a green rectangle on the first video preview, and confirm.
6. Click "Start Batch Processing".

The UI runs an actual tensor-compute probe and reports the accelerator available to the AI engine: `CUDA`, `DirectML`, or `CPU`. The NVENC status only describes video encoding and does not imply GPU-accelerated AI inference. CPU defaults to the more efficient dynamic-text LaMa path, while CUDA uses temporally consistent STTN. A failed runtime probe falls back to CPU instead of trusting DLL presence.

With subtitle removal enabled, clip planning and deduplication happen first. The range-aware wrapper inserts IDR keyframes at final clip boundaries and prefers an NVENC or QSV encoder that passes a real encoding probe. AMD AMF currently falls back to CPU `libx264` to keep stream-copy boundaries reliable. Final clips copy that video stream and only re-encode audio when needed, eliminating the previous second lossy video encode.

Variable-frame-rate (VFR) recordings are normalized to a lossless CFR working source before AI processing, then replanned on that same timeline to prevent OpenCV from shortening or desynchronizing the video. Temporary-disk capacity is checked first, reset/cancel terminates every pipeline child process, and STTN blends repaired pixels only inside the dynamic text mask.

Expected AI sidecar layout:

```text
ai\video-subtitle-remover\
  backend\main.py
  python.exe
  _internal\
```

CPU and CUDA green packages are self-contained. The CPU package runs LaMa on the computer's existing CPU; it does not ship or replace CPU hardware. The CUDA build bundles the PyTorch CUDA software runtime, cuDNN, and cuBLAS, so users do not install the CUDA Toolkit separately; a supported NVIDIA GPU and driver are still required. Redistributable packages exclude the non-commercial ProPainter code and weights.

### Project Structure

```text
.
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / MainWindow.xaml.cs
├── VideoProcessorService.cs
├── SubtitleRemovalService.cs
├── SubtitleRegionDialog.xaml / SubtitleRegionDialog.xaml.cs
├── VideoAnalysisAlgorithms.cs
├── FFmpegHelper.cs
├── DownloadService.cs
├── HardwareOptimizationService.cs
├── FrameBatchExtractor.cs
├── TransNetV2DetectorService.cs
├── assets/
├── models/
├── VideoToMaterial.Tests/
├── VideoToMaterial.csproj
└── VideoToMaterial.sln
```

### Key Modules

| File | Responsibility |
| --- | --- |
| `MainWindow.xaml` / `MainWindow.xaml.cs` | WPF UI, queue management, parameter reading, user interactions |
| `VideoProcessorService.cs` | scene detection, task generation, FFmpeg clipping/export |
| `VideoAnalysisAlgorithms.cs` | pHash, color histograms, and similarity metrics |
| `FFmpegHelper.cs` | encoder detection and FFmpeg capability probing |
| `DownloadService.cs` | URL downloads through `yt-dlp` |
| `HardwareOptimizationService.cs` | CPU/GPU/memory inspection and concurrency suggestions |
| `TransNetV2DetectorService.cs` | reserved ONNX high-precision detector implementation |

### Build a Portable Release

Framework-contained portable folder:

```powershell
dotnet publish VideoToMaterial.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -o .\dist\MaterialHarvester-Green
```

After publishing, copy matching `ffmpeg.exe` / `ffprobe.exe`, `yt-dlp.exe`, and `deno.exe` into `dist\MaterialHarvester-Green`, then compress the folder. The current release gate requires FFmpeg/ffprobe 9.0.1, yt-dlp 2026.07.04 or newer, and isolated-`PATH` startup checks. Sanitize and smoke-test the offline VSR sidecar before packaging it. The GitHub Release asset `MaterialHarvester-win-x64.exe` is a complete non-elevated self-extracting archive containing the same runtime tools and offline AI sidecar. Open it, choose a destination, then run the extracted `MaterialHarvester.exe`.

### Open-Source Repository Boundary

Recommended to commit:

- C# / XAML source files
- `.sln` / `.csproj`
- Project icons in `assets/`
- `models/README.md`
- `README.md`, `LICENSE`, `CONTRIBUTING.md`, `THIRD_PARTY_NOTICES.md`

Do not commit:

- `bin/`, `obj/`, `dist/`
- `.vs/`, `.codegraph/`
- `ffmpeg.exe`, `ffprobe.exe`, `yt-dlp.exe`, `deno.exe`
- `models/*.onnx`
- Downloaded videos, generated clip folders, or local test data

### Third-Party Dependencies

The source code is licensed under the MIT License. Third-party tools and libraries are governed by their own licenses.

- FFmpeg / ffprobe: video processing, scene detection, thumbnail extraction, and export.
- yt-dlp: optional URL-based video downloads.
- OpenCvSharp: accelerated image analysis.
- Microsoft.ML.OnnxRuntime: reserved ONNX inference capability.

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for details.

### Contributing

Issues and pull requests are welcome. Before submitting:

- Make sure the Release build passes.
- Do not commit build output, third-party executables, ONNX models, or generated videos.
- Update README when changing user-facing behavior.

See [CONTRIBUTING.md](CONTRIBUTING.md) for details.

### License

This project is licensed under the [MIT License](LICENSE).
