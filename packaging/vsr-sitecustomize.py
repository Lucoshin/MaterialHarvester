"""Runtime path bootstrap for the portable Video Subtitle Remover sidecar."""

from __future__ import annotations

import os
from pathlib import Path


_ROOT = Path(__file__).resolve().parent
_DLL_DIRECTORIES = (
    _ROOT,
    _ROOT / "pywin32_system32",
    _ROOT / "win32",
    _ROOT / "torch" / "lib",
    _ROOT / "paddle" / "libs",
)

for _directory in _DLL_DIRECTORIES:
    if _directory.is_dir():
        os.add_dll_directory(str(_directory))
