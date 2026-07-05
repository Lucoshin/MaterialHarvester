# Models

Place optional ONNX scene-detection models in this directory for local development.

The repository intentionally ignores `*.onnx` files because they are usually large and may have separate licenses. Verify model licensing before publishing or redistributing model files.

Current application status:

- The default `ContentDetector` and `AdaptiveDetector` modes use FFmpeg scene detection plus local similarity analysis.
- `TransNetV2DetectorService` exists as an experimental/reserved implementation path.
- The main UI does not enable high-precision ONNX detection by default.
