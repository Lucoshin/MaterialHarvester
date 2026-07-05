# Contributing

Thanks for considering a contribution to Material Harvester.

## Development Setup

1. Install .NET 8 SDK with Windows Desktop support.
2. Clone the repository.
3. Restore and build:

```powershell
dotnet restore VideoToMaterial.sln
dotnet build VideoToMaterial.csproj -c Release
```

4. Place local runtime tools next to the built executable when needed:

```text
ffmpeg.exe
ffprobe.exe
yt-dlp.exe
```

These tools are not committed to the repository.

## Pull Request Guidelines

- Keep changes focused and describe the user-facing behavior.
- Do not commit `bin/`, `obj/`, `dist/`, downloaded videos, generated clips, third-party executables, or ONNX model files.
- Run a Release build before submitting:

```powershell
dotnet build VideoToMaterial.csproj -c Release
```

- Update `README.md` when changing user-facing behavior, supported dependencies, or release steps.

## Code Style

- Keep UI behavior explicit and avoid hidden state transitions.
- Prefer service-level logic in separate classes over adding large blocks to `MainWindow.xaml.cs`.
- Use `ProcessStartInfo.ArgumentList` for new process invocations when practical.
