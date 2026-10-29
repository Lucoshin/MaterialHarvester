# 参与开发

开发环境：Windows 10 / 11、.NET 8 SDK（含 Windows 桌面开发支持）。

## 构建与运行

```powershell
git clone https://github.com/Lucoshin/MaterialHarvester.git
cd MaterialHarvester
dotnet build VideoToMaterial.sln -c Release
```

将 `ffmpeg.exe`、`ffprobe.exe`、`yt-dlp.exe` 和 `deno.exe` 放到 `bin\Release\net8.0-windows`，然后运行该目录的 `MaterialHarvester.exe`。内置扫码登录需要 Microsoft Edge WebView2 Runtime。

解决方案包含 WPF 桌面程序和 `MaterialHarvester.Cli` 命令行项目。命令行项目通过 JSON 任务调用，支持 `split`（切片）与 `compose`（成片）；参数和请求格式见 [命令行参数](MaterialHarvester.Cli/CliOptions.cs) 与 [请求结构](MaterialHarvester.Cli/Contracts/JobRequest.cs)。

## 打包绿色版

```powershell
dotnet publish VideoToMaterial.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o .\dist\MaterialHarvester-Green
```

在全新输出目录打包，将配套工具、`LICENSE` 和 `THIRD_PARTY_NOTICES.md` 复制进去。默认发布 NoAI 版；不附带 AI 引擎、模型或去字幕包装器。

使用仓库中的验证脚本检查工具版本和自包含依赖：

```powershell
.\packaging\verify-green-package.ps1 -PackageDirectory .\dist\MaterialHarvester-Green
```

验证脚本要求配套的 FFmpeg / ffprobe 9.0.1、yt-dlp 2026.07.04 或更新版本、Deno 2.3.0 或更新版本。必须保留 `packaging\licenses` 下的分发说明。

## 提交前

- 运行 `dotnet build VideoToMaterial.sln -c Release`。
- 说明修改解决的问题；用户可见行为变化应同步更新 README。
- 不提交内部计划、个人配置、本地测试资料、构建产物、第三方可执行文件、模型或视频。
- 新的子进程调用优先使用 `ProcessStartInfo.ArgumentList` 传递参数。
