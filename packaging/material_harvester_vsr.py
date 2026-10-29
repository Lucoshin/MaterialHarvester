"""Material Harvester range-aware command wrapper for Video Subtitle Remover.

The upstream CLI always processes the whole video. This wrapper keeps the
upstream implementation intact while exposing its existing ``ab_sections``
capability so inference is limited to clips retained by Material Harvester.
"""

from __future__ import annotations

import argparse
import json
import math
import multiprocessing
import os
import shutil
import subprocess
import sys
import tempfile
import types
from typing import Iterable


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Range-aware Video Subtitle Remover wrapper")
    parser.add_argument("--input", "-i", required=True)
    parser.add_argument("--output", "-o", required=True)
    parser.add_argument(
        "--subtitle-area-coords",
        "-c",
        action="append",
        nargs=4,
        type=int,
        default=[],
        metavar=("YMIN", "YMAX", "XMIN", "XMAX"),
    )
    parser.add_argument(
        "--inpaint-mode",
        default="sttn-det",
        choices=("sttn-det", "sttn-auto", "lama", "opencv"),
    )
    parser.add_argument("--process-ranges-file")
    parser.add_argument("--output-segments-file")
    parser.add_argument("--video-codec", default="libx264")
    parser.add_argument("--video-arg", action="append", default=[])
    return parser.parse_args()


def _read_number(item: dict, *names: str) -> float:
    for name in names:
        if name in item:
            return float(item[name])
    raise ValueError(f"Missing range property: {names[0]}")


def _load_time_ranges(path: str | None) -> list[tuple[float, float]]:
    if not path:
        return []

    with open(path, "r", encoding="utf-8") as stream:
        payload: Iterable[dict] = json.load(stream)

    ranges: list[tuple[float, float]] = []
    for item in payload:
        start_seconds = _read_number(item, "StartSeconds", "startSeconds", "start")
        end_seconds = _read_number(item, "EndSeconds", "endSeconds", "end")
        if end_seconds > start_seconds:
            ranges.append((max(0.0, start_seconds), end_seconds))
    return ranges


def _load_frame_ranges(path: str | None, fps: float, frame_count: int) -> list[range] | None:
    time_ranges = _load_time_ranges(path)
    if not time_ranges:
        return None

    sections: list[range] = []
    for start_seconds, end_seconds in time_ranges:
        start_frame = max(0, int(math.floor(start_seconds * fps)))
        end_frame = min(frame_count, int(math.ceil(end_seconds * fps)))
        if end_frame > start_frame:
            sections.append(range(start_frame, end_frame))
    return sections


def _cpu_encoder_arguments() -> list[str]:
    return ["-preset", "fast", "-crf", "18", "-bf", "0"]


def _encoder_arguments(codec: str, requested: list[str]) -> tuple[str, list[str]]:
    # AMF does not expose a reliable forced-IDR control in the bundled FFmpeg.
    # Stream-copy clips must never start on an open-GOP I-frame, so keep AMF on
    # the correctness-first CPU path until its IDR behaviour can be verified.
    if codec not in {"libx264", "h264_nvenc", "h264_qsv"}:
        return "libx264", _cpu_encoder_arguments()

    selected = codec
    arguments = list(requested)
    if not arguments:
        if selected == "h264_nvenc":
            arguments = ["-preset", "p4", "-cq", "25"]
        elif selected == "h264_qsv":
            arguments = ["-global_quality", "25", "-preset", "medium"]
        else:
            arguments = _cpu_encoder_arguments()
    if selected == "h264_nvenc" and "-b:v" not in arguments:
        arguments.extend(["-b:v", "0"])
    if selected == "h264_nvenc" and "-forced-idr" not in arguments:
        arguments.extend(["-forced-idr", "1"])
    if selected == "h264_qsv" and "-forced_idr" not in arguments:
        arguments.extend(["-forced_idr", "1"])
    if "-bf" not in arguments:
        # B-frame reordering makes stream-copy clip durations drift at exact
        # boundaries.  P-only GOPs keep every forced IDR cut frame-accurate.
        arguments.extend(["-bf", "0"])
    return selected, arguments


def _probe_encoder(ffmpeg_path: str, codec: str, video_args: list[str]) -> tuple[bool, str]:
    command = [
        ffmpeg_path,
        "-hide_banner", "-loglevel", "error",
        # Several hardware encoders reject tiny frames even when the device
        # and requested profile are otherwise fully usable.
        "-f", "lavfi", "-i", "color=c=black:s=256x256:r=1:d=1",
        "-frames:v", "1", "-an",
        "-c:v", codec,
        *video_args,
        "-force_key_frames", "0",
        "-pix_fmt", "yuv420p",
        "-f", "null", "-",
    ]
    try:
        completed = subprocess.run(
            command,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            check=False,
            timeout=30,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        return False, str(error)

    stderr = completed.stderr or b""
    if isinstance(stderr, bytes):
        detail = stderr.decode("utf-8", errors="replace")
    else:
        detail = str(stderr)
    return completed.returncode == 0, detail.strip()


def _resolve_encoder(ffmpeg_path: str, codec: str,
                     requested: list[str]) -> tuple[str, list[str]]:
    selected, arguments = _encoder_arguments(codec, requested)
    if selected == "libx264":
        if codec == "h264_amf":
            print(
                "AMF single-pass IDR safety is not verified; using libx264.",
                file=sys.stderr,
            )
        return selected, arguments

    available, detail = _probe_encoder(ffmpeg_path, selected, arguments)
    if available:
        return selected, arguments

    suffix = f": {detail}" if detail else ""
    print(
        f"{selected} probe failed; falling back to libx264{suffix}",
        file=sys.stderr,
    )
    return "libx264", _cpu_encoder_arguments()


def _keyframe_expression(segments: list[tuple[float, float]],
                         fps: float | None = None) -> str:
    frame_rate = float(fps) if fps is not None else 0.0
    use_frame_grid = math.isfinite(frame_rate) and frame_rate > 0.0
    boundaries: set[float] = set()
    for segment in segments:
        for value in segment:
            if not math.isfinite(value):
                continue
            boundary = max(0.0, value)
            if use_frame_grid:
                # FFmpeg normally forces the first frame *after* a requested
                # time.  Seeking to that time would then fall back to the
                # previous GOP.  Force the frame at-or-before the boundary.
                boundary = math.floor(boundary * frame_rate + 1e-9) / frame_rate
            boundaries.add(round(boundary, 9))
    boundaries = sorted(boundaries)
    return ",".join(f"{value:.6f}" for value in boundaries)


def _install_keyframed_writer(upstream: object, codec: str, video_args: list[str],
                              segments: list[tuple[float, float]]) -> None:
    if not segments:
        return

    import numpy as np
    from backend.tools.ffmpeg_cli import FFmpegCLI

    class MaterialHarvesterVideoWriter:
        def __init__(self, output_path: str, fps: float, size: tuple[int, int]):
            width, height = size
            ffmpeg_path = FFmpegCLI.instance().ffmpeg_path
            selected_codec, selected_args = _resolve_encoder(
                ffmpeg_path,
                codec,
                video_args,
            )
            keyframes = _keyframe_expression(segments, fps)
            command = [
                ffmpeg_path,
                "-y",
                "-f", "rawvideo",
                "-vcodec", "rawvideo",
                "-s", f"{width}x{height}",
                "-pix_fmt", "bgr24",
                "-r", str(fps),
                "-i", "-",
                "-c:v", selected_codec,
                *selected_args,
                "-force_key_frames", keyframes,
                "-pix_fmt", "yuv420p",
                "-loglevel", "error",
                output_path,
            ]
            self._stderr = tempfile.TemporaryFile(mode="w+b")
            try:
                self._process = subprocess.Popen(
                    command,
                    stdin=subprocess.PIPE,
                    stdout=subprocess.DEVNULL,
                    stderr=self._stderr,
                )
            except Exception:
                self._stderr.close()
                raise

        def _stderr_tail(self) -> str:
            try:
                self._stderr.flush()
                self._stderr.seek(0, os.SEEK_END)
                end = self._stderr.tell()
                self._stderr.seek(max(0, end - 8192), os.SEEK_SET)
                return self._stderr.read().decode("utf-8", errors="replace").strip()
            except Exception:
                return ""

        def write(self, frame: object) -> None:
            if frame.dtype != np.uint8:
                frame = np.clip(frame, 0, 255).astype(np.uint8)
            try:
                self._process.stdin.write(frame.tobytes())
            except BrokenPipeError as error:
                detail = self._stderr_tail()
                suffix = f": {detail}" if detail else ""
                raise RuntimeError(f"FFmpeg video writer pipe closed unexpectedly{suffix}") from error

        def release(self) -> None:
            try:
                self._process.stdin.close()
            except (BrokenPipeError, AttributeError):
                pass
            try:
                try:
                    return_code = self._process.wait(timeout=600)
                except subprocess.TimeoutExpired:
                    self._process.terminate()
                    try:
                        return_code = self._process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        self._process.kill()
                        return_code = self._process.wait(timeout=10)
                detail = self._stderr_tail()
            finally:
                self._stderr.close()
            if return_code != 0:
                suffix = f": {detail}" if detail else ""
                raise RuntimeError(
                    f"FFmpeg video writer failed with code {return_code}{suffix}"
                )

    upstream.FFmpegVideoWriter = MaterialHarvesterVideoWriter


def _install_safe_audio_merger(upstream: object) -> None:
    """Replace the upstream Windows command-shell FFmpeg calls.

    Video paths can contain command-shell metacharacters such as ``&`` (in
    particular when they originate from a downloaded title).  Passing an
    argument list through ``cmd.exe`` is both incorrect for those paths and a
    command-injection risk.  A single direct FFmpeg process can mux the source
    audio into the repaired video without an intermediate AAC file.
    """
    from backend.tools.ffmpeg_cli import FFmpegCLI

    def merge_audio_to_video(remover: object) -> None:
        command = [
            FFmpegCLI.instance().ffmpeg_path,
            "-y",
            "-i", remover.video_temp_file.name,
            "-i", remover.video_path,
            "-map", "0:v:0",
            "-map", "1:a?",
            "-c:v", "copy",
            "-c:a", "aac",
            "-loglevel", "error",
            remover.video_out_path,
        ]
        try:
            with open(os.devnull, "rb") as null_input:
                subprocess.run(
                    command,
                    stdin=null_input,
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.PIPE,
                    check=True,
                    timeout=600,
                )
            remover.is_successful_merged = True
        except Exception as error:
            raise RuntimeError(
                f"Audio merge failed; repaired video was not finalized: {error}"
            ) from error
        finally:
            remover.video_temp_file.close()

    upstream.SubtitleRemover.merge_audio_to_video = merge_audio_to_video


def _blend_repaired_frames(input_frames: list[object], repaired_frames: list[object],
                           input_mask: object) -> list[object]:
    """Keep every pixel outside the detected text mask byte-for-byte intact."""
    import numpy as np

    mask = np.asarray(input_mask)
    if mask.ndim == 3:
        mask = mask[:, :, 0]
    active = mask > 0
    blended: list[object] = []
    for original, repaired in zip(input_frames, repaired_frames):
        source = np.asarray(original)
        result = source.copy()
        if repaired is not None:
            repaired_array = np.asarray(repaired).astype(source.dtype, copy=False)
            result[active] = repaired_array[active]
        blended.append(result)
    return blended


def _install_mask_preserving_sttn() -> None:
    """Constrain upstream STTN's full horizontal-strip writeback to its mask."""
    from backend.inpaint.sttn_det_inpaint import STTNDetInpaint

    original_call = STTNDetInpaint.__call__

    def masked_call(inpaint: object, input_frames: list[object], input_mask: object) -> list[object]:
        repaired_frames = original_call(inpaint, input_frames, input_mask)
        return _blend_repaired_frames(input_frames, repaired_frames, input_mask)

    STTNDetInpaint.__call__ = masked_call


def _transcode_with_keyframes(input_path: str, output_path: str, codec: str,
                              video_args: list[str], segments: list[tuple[float, float]],
                              fps: float | None = None) -> None:
    from backend.tools.ffmpeg_cli import FFmpegCLI

    ffmpeg_path = FFmpegCLI.instance().ffmpeg_path
    selected_codec, selected_args = _resolve_encoder(ffmpeg_path, codec, video_args)

    def build_command(video_codec: str, arguments: list[str]) -> list[str]:
        return [
            ffmpeg_path,
            "-y", "-i", input_path,
            "-map", "0:v:0", "-map", "0:a?",
            "-c:v", video_codec,
            *arguments,
            "-force_key_frames", _keyframe_expression(segments, fps),
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-movflags", "+faststart",
            "-loglevel", "error",
            output_path,
        ]

    command = build_command(selected_codec, selected_args)
    try:
        subprocess.run(command, check=True)
    except subprocess.CalledProcessError:
        if selected_codec not in {"h264_nvenc", "h264_qsv"}:
            raise
        print(
            f"{selected_codec} transcode failed after probing; retrying with libx264.",
            file=sys.stderr,
        )
        try:
            if os.path.exists(output_path):
                os.remove(output_path)
        except OSError:
            pass
        subprocess.run(
            build_command("libx264", _cpu_encoder_arguments()),
            check=True,
        )


def _install_setuptools_config_compat() -> None:
    """Repair an omission in the current frozen CPU sidecar.

    Paddle imports setuptools only to expose its C++ extension helpers.  The
    one-folder VSR build contains setuptools itself but its ``config`` package
    was omitted by PyInstaller.  The missing modules are not used during
    inference, but setuptools imports them eagerly.  Lightweight placeholders
    let that import complete without adding a build-time package manager to the
    portable application.
    """
    try:
        import setuptools.config  # noqa: F401
        return
    except ModuleNotFoundError as error:
        if error.name != "setuptools.config":
            raise

    config_module = types.ModuleType("setuptools.config")
    config_module.__path__ = []
    pyproject_module = types.ModuleType("setuptools.config.pyprojecttoml")
    setupcfg_module = types.ModuleType("setuptools.config.setupcfg")
    config_module.pyprojecttoml = pyproject_module
    config_module.setupcfg = setupcfg_module
    sys.modules["setuptools.config"] = config_module
    sys.modules["setuptools.config.pyprojecttoml"] = pyproject_module
    sys.modules["setuptools.config.setupcfg"] = setupcfg_module


def _install_unsupported_propainter_stub() -> None:
    """Keep the upstream dispatcher importable without non-commercial assets.

    Material Harvester never selects ProPainter.  Its code and weights are
    intentionally removed from redistributable packages, so the static import
    in the upstream dispatcher is satisfied by a fail-closed placeholder.
    """
    module_name = "backend.inpaint.propainter_inpaint"
    stub_module = types.ModuleType(module_name)

    class UnsupportedPropainter:
        def __init__(self, *_args: object, **_kwargs: object) -> None:
            raise RuntimeError(
                "ProPainter is not included because its official code and weights "
                "are restricted to non-commercial use."
            )

    stub_module.PropainterInpaint = UnsupportedPropainter
    sys.modules[module_name] = stub_module


def _resolve_sidecar_root() -> str:
    script_directory = os.path.dirname(os.path.abspath(__file__))
    candidates = (
        os.getcwd(),
        os.path.dirname(os.path.abspath(sys.executable)),
        script_directory,
        os.path.dirname(script_directory),
    )
    for candidate in candidates:
        if os.path.isfile(os.path.join(candidate, "backend", "main.py")):
            return candidate
    raise FileNotFoundError("Video Subtitle Remover backend/main.py was not found")


def _release_partial_remover(remover: object) -> None:
    for name in ("video_cap", "video_writer"):
        resource = getattr(remover, name, None)
        try:
            if resource is not None:
                resource.release()
        except Exception:
            pass

    temp_file = getattr(remover, "video_temp_file", None)
    temp_path = getattr(temp_file, "name", None)
    try:
        if temp_file is not None:
            temp_file.close()
    except Exception:
        pass
    try:
        if temp_path and os.path.exists(temp_path):
            os.remove(temp_path)
    except Exception:
        pass


def main() -> int:
    args = _parse_args()
    sidecar_root = _resolve_sidecar_root()
    os.chdir(sidecar_root)
    os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")
    if sidecar_root not in sys.path:
        sys.path.insert(0, sidecar_root)
    _install_setuptools_config_compat()
    _install_unsupported_propainter_stub()

    import backend.main as upstream
    from backend.tools.common_tools import is_video_or_image
    from backend.tools.constant import InpaintMode, SubtitleDetectMode

    upstream.config.set(upstream.config.interface, "en")
    upstream.config.set(
        upstream.config.subtitleDetectMode,
        SubtitleDetectMode.PP_OCRv5_MOBILE,
    )
    translation_file = os.path.join(sidecar_root, "backend", "interface", "en.ini")
    upstream.tr.read(translation_file, encoding="utf-8")

    output_segments = _load_time_ranges(args.output_segments_file)
    _install_keyframed_writer(upstream, args.video_codec, args.video_arg, output_segments)
    _install_safe_audio_merger(upstream)
    _install_mask_preserving_sttn()

    remover = upstream.SubtitleRemover(args.input)
    if not is_video_or_image(args.input):
        print(f"Error: {args.input} is unsupported or corrupted.", file=sys.stderr)
        return 2

    remover.sub_areas = args.subtitle_area_coords
    remover.video_out_path = args.output
    remover.ab_sections = _load_frame_ranges(
        args.process_ranges_file,
        remover.fps,
        remover.frame_count,
    )
    requested_mode = InpaintMode[args.inpaint_mode.replace("-", "_").upper()]
    upstream.config.inpaintMode.value = requested_mode
    if remover.ab_sections:
        selected_frames = sum(len(section) for section in remover.ab_sections)
        print(
            f"Material Harvester optimized ranges: {len(remover.ab_sections)} "
            f"section(s), {selected_frames}/{remover.frame_count} frame(s)"
        )
    try:
        remover.run()
    except Exception as error:
        if "no subtitles detected" not in str(error).lower():
            raise
        _release_partial_remover(remover)
        os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
        if output_segments:
            _transcode_with_keyframes(
                args.input,
                args.output,
                args.video_codec,
                args.video_arg,
                output_segments,
                remover.fps,
            )
            print("No subtitles detected; original video was encoded once at final clip boundaries.")
        else:
            shutil.copy2(args.input, args.output)
            print("No subtitles detected; original video was kept unchanged.")
    return 0


if __name__ == "__main__":
    multiprocessing.freeze_support()
    try:
        multiprocessing.set_start_method("spawn")
    except RuntimeError:
        pass
    raise SystemExit(main())
