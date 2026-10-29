# Third-Party Notices

Material Harvester integrates with several third-party projects and tools. The source repository does not vendor large runtime binaries by default.

## Runtime Tools

- **FFmpeg / ffprobe**: the public green packages redistribute the matching Gyan FFmpeg 9.0.1 Essentials static build under GPLv3. The package keeps `FFmpeg-GPL-3.0.txt`, the distributor README/build configuration, and an exact corresponding-source notice under `packaging/licenses/`.
- **yt-dlp**: the public green packages redistribute yt-dlp 2026.07.04 for URL-based video import. Its Unlicense text and generated third-party license notice are kept under `packaging/licenses/`. Users remain responsible for the terms of service of the video platforms they access.
- **Deno**: JavaScript/TypeScript runtime bundled so yt-dlp can execute its official EJS YouTube challenge solver without a separate user installation. Deno is distributed under the MIT License; its project notices and source are available at https://github.com/denoland/deno.
- **7-Zip SFX module**: used only to wrap complete green directories as non-elevated self-extracting Release assets. 7-Zip is licensed under the GNU LGPL. The SFX binary's redistribution notice is kept as `packaging/licenses/7-Zip-LICENSE.txt`; source information is available at https://www.7-zip.org/.
- **Video Subtitle Remover**: optional AI sidecar used for hard-subtitle detection and inpainting. The upstream project is Apache-2.0 and its license is kept alongside the sidecar. Material Harvester uses PaddleOCR mobile detection with dynamic masks, the LaMa engine for the CPU fallback, and STTN detection/inpainting for CUDA. Official ProPainter code and weights are restricted to non-commercial use and are deliberately removed from redistributable Material Harvester packages.
- **Python Embedded Distribution**: the Full green package includes the official CPython 3.13 embedded runtime under the Python Software Foundation License; its `LICENSE.txt` is kept beside the runtime.
- **PyTorch / torchvision, PaddlePaddle / PaddleOCR, OpenCV, NumPy, SciPy and supporting Python packages**: runtime dependencies redistributed in AI-enabled green packages. The CUDA package also redistributes the CUDA runtime libraries bundled by the official PyTorch wheel (including cuDNN and cuBLAS); their bundled licenses, notices, and package metadata remain applicable. End users do not need a separate CUDA Toolkit installation, but NVIDIA hardware acceleration requires a compatible driver.

## NuGet Dependencies

- **OpenCvSharp4** and **OpenCvSharp4.runtime.win**: used for image analysis acceleration.
- **Microsoft.ML.OnnxRuntime**: used by the reserved TransNetV2 detector implementation.
- **System.Drawing.Common**: used for bitmap and icon processing in the Windows desktop application.
- **Microsoft.Web.WebView2**: embeds isolated Microsoft Edge (Chromium) login surfaces for Douyin, Xiaohongshu, and Bilibili QR authentication. The application stores per-platform profiles separately from the user's Edge/Chrome profiles and exports only temporary platform cookies to yt-dlp.

## Model Files

`models/*.onnx` files are intentionally excluded from version control. If you use TransNetV2 or another ONNX model, verify that the model license allows your intended use and distribution.

## Distribution Guidance

Release archives may include FFmpeg, ffprobe, yt-dlp, Deno, AI runtime libraries, and a 7-Zip SFX stub for user convenience. Their exact license files and source/build references are bundled under `packaging/licenses/`; those notices must remain with redistributed packages.
