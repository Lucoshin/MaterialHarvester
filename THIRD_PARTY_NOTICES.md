# Third-Party Notices

Material Harvester integrates with several third-party projects and tools. The source repository does not vendor large runtime binaries by default.

## Runtime Tools

- **FFmpeg / ffprobe**: used for video probing, scene detection, thumbnail extraction, and clip export. FFmpeg builds may be licensed under LGPL or GPL depending on build options. Download and distribute FFmpeg only under the license terms of the build you choose.
- **yt-dlp**: optional command-line downloader used for URL-based video import. Follow the yt-dlp project license and the terms of service of the video platforms you access.

## NuGet Dependencies

- **OpenCvSharp4** and **OpenCvSharp4.runtime.win**: used for image analysis acceleration.
- **Microsoft.ML.OnnxRuntime**: used by the reserved TransNetV2 detector implementation.
- **System.Drawing.Common**: used for bitmap and icon processing in the Windows desktop application.

## Model Files

`models/*.onnx` files are intentionally excluded from version control. If you use TransNetV2 or another ONNX model, verify that the model license allows your intended use and distribution.

## Distribution Guidance

Release archives may include FFmpeg, ffprobe, yt-dlp, and runtime libraries for user convenience. Keep their licenses and source links available in release notes or bundled notices when distributing binaries.
