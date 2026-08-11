# Third-Party Notices

Material Harvester integrates with several third-party projects and tools. The source repository does not vendor large runtime binaries by default.

## Runtime Tools

- **FFmpeg / ffprobe**: used for video probing, scene detection, thumbnail extraction, and clip export. FFmpeg builds may be licensed under LGPL or GPL depending on build options. Download and distribute FFmpeg only under the license terms of the build you choose.
- **yt-dlp**: optional command-line downloader used for URL-based video import. Follow the yt-dlp project license and the terms of service of the video platforms you access.
- **7-Zip SFX module**: used only to wrap the complete green directory as the non-elevated self-extracting `MaterialHarvester-win-x64.exe` Release asset. 7-Zip is licensed under the GNU LGPL; source and license information are available at https://www.7-zip.org/.
- **Video Subtitle Remover**: optional AI sidecar used for hard-subtitle detection and inpainting. It is included only in the Full green package; the Lite package omits it. The upstream project is Apache-2.0. The Full package uses the CPU `sttn-auto` path and includes the upstream license alongside the sidecar. Individual engines and model weights may use different terms; in particular, official ProPainter code and weights are non-commercial unless separately licensed.
- **Python Embedded Distribution**: the Full green package includes the official CPython 3.13 embedded runtime under the Python Software Foundation License; its `LICENSE.txt` is kept beside the runtime.
- **PyTorch / torchvision, PaddlePaddle / PaddleOCR, OpenCV, NumPy, SciPy and supporting Python packages**: runtime dependencies redistributed only in the Full green package. Their project licenses and bundled package metadata remain applicable.

## NuGet Dependencies

- **OpenCvSharp4** and **OpenCvSharp4.runtime.win**: used for image analysis acceleration.
- **Microsoft.ML.OnnxRuntime**: used by the reserved TransNetV2 detector implementation.
- **System.Drawing.Common**: used for bitmap and icon processing in the Windows desktop application.

## Model Files

`models/*.onnx` files are intentionally excluded from version control. If you use TransNetV2 or another ONNX model, verify that the model license allows your intended use and distribution.

## Distribution Guidance

Release archives may include FFmpeg, ffprobe, yt-dlp, and runtime libraries for user convenience. Keep their licenses and source links available in release notes or bundled notices when distributing binaries.
