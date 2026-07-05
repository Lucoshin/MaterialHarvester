# Material Harvester

**Language:** [中文](#中文) | [English](#english)

---

## 中文

Material Harvester 是一款面向视频创作者的 Windows 桌面工具，用于把长视频快速整理成可复用的素材片段。它支持基于画面变化的智能场景切分、固定时长切分、相似片段去重、批量处理和可选的网络视频下载。

> 当前项目名仍为 `VideoToMaterial`，应用对外名称为 `Material Harvester`。

### 功能特性

- **智能场景切分**：基于 FFmpeg 的 `scene` 画面变化检测识别镜头切点。
- **自适应补切**：`AdaptiveDetector` 会在默认检测基础上追加一轮更敏感的弱切点检测，降低漏切概率。
- **固定时长切分**：按设定秒数均匀切片，适合批量生产等长素材。
- **相似片段去重**：使用 pHash（感知哈希）和颜色直方图判断相邻或历史场景是否重复。
- **批量处理队列**：支持多视频拖拽导入、缩略图预览、单个视频移除和并发导出。
- **网络视频导入**：可选集成 `yt-dlp`，粘贴视频链接后下载并加入处理队列。
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

3. 将运行时工具放到输出目录：

```text
bin\Release\net8.0-windows\
  ffmpeg.exe
  ffprobe.exe
  yt-dlp.exe
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
   - `固定时长`：按固定秒数切成等长片段。
4. 调整灵敏度、最小时长、并发数和静音选项。
5. 点击“开始批量处理”。

### 项目结构

```text
.
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / MainWindow.xaml.cs
├── VideoProcessorService.cs
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

发布后将 `ffmpeg.exe`、`ffprobe.exe`、`yt-dlp.exe` 复制到 `dist\MaterialHarvester-Green`，再压缩该目录。

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
- `ffmpeg.exe`、`ffprobe.exe`、`yt-dlp.exe`
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
- **Fixed-duration slicing**: exports evenly timed clips for batch material production.
- **Similarity deduplication**: uses pHash and color histograms to merge or discard visually similar scenes.
- **Batch queue**: supports drag-and-drop import, thumbnail previews, per-item removal, and concurrent exports.
- **URL import**: optionally integrates with `yt-dlp` to download videos and enqueue them for processing.
- **Hardware encoding preference**: detects available encoders and prefers hardware acceleration such as NVIDIA NVENC when available.
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
   - `Fixed Duration`: slices videos into equal-length clips.
4. Adjust sensitivity, minimum duration, concurrency, and mute options.
5. Click "Start Batch Processing".

### Project Structure

```text
.
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / MainWindow.xaml.cs
├── VideoProcessorService.cs
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

After publishing, copy `ffmpeg.exe`, `ffprobe.exe`, and `yt-dlp.exe` into `dist\MaterialHarvester-Green`, then compress the folder.

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
- `ffmpeg.exe`, `ffprobe.exe`, `yt-dlp.exe`
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
