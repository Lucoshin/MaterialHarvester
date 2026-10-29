param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,

    [switch]$IncludeAi,

    [switch]$RequireCuda
)

$ErrorActionPreference = 'Stop'
if ($RequireCuda -and -not $IncludeAi) {
    throw 'RequireCuda requires IncludeAi.'
}
$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
$requiredFiles = @(
    'MaterialHarvester.exe',
    'ffmpeg.exe',
    'ffprobe.exe',
    'yt-dlp.exe',
    'deno.exe',
    'MaterialHarvester.dll',
    'coreclr.dll',
    'hostfxr.dll',
    'hostpolicy.dll',
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.Wpf.dll',
    'LICENSE',
    'THIRD_PARTY_NOTICES.md',
    'packaging\licenses\7-Zip-LICENSE.txt',
    'packaging\licenses\DENO-LICENSE.txt',
    'packaging\licenses\FFmpeg-GPL-3.0.txt',
    'packaging\licenses\FFmpeg-Gyan-9.0.1-README.txt',
    'packaging\licenses\FFmpeg-Gyan-9.0.1-SOURCE.txt',
    'packaging\licenses\yt-dlp-UNLICENSE.txt',
    'packaging\licenses\yt-dlp-THIRD_PARTY_LICENSES.txt',
    'packaging\licenses\WebView2-LICENSE.txt',
    'packaging\licenses\WebView2-NOTICE.txt'
)
if ($IncludeAi) {
    $requiredFiles += @(
        'packaging\material_harvester_vsr.py',
        'packaging\sanitize-ai-sidecar.ps1',
        'ai\video-subtitle-remover\python.exe',
        'ai\video-subtitle-remover\backend\main.py'
    )
}

foreach ($relativePath in $requiredFiles) {
    $fullPath = Join-Path $packageRoot $relativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Missing packaged dependency: $relativePath"
    }
}

$savedPath = $env:PATH
$savedCudaPath = $env:CUDA_PATH
$savedCudaPathV13 = $env:CUDA_PATH_V13_0
try {
    $env:PATH = Join-Path $env:SystemRoot 'System32'
    Remove-Item Env:CUDA_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:CUDA_PATH_V13_0 -ErrorAction SilentlyContinue

    $ffmpeg = Join-Path $packageRoot 'ffmpeg.exe'
    $ffprobe = Join-Path $packageRoot 'ffprobe.exe'
    $ytDlp = Join-Path $packageRoot 'yt-dlp.exe'
    $deno = Join-Path $packageRoot 'deno.exe'
    $python = Join-Path $packageRoot 'ai\video-subtitle-remover\python.exe'
    $wrapper = Join-Path $packageRoot 'packaging\material_harvester_vsr.py'

    $ffmpegOutput = & $ffmpeg -version 2>&1
    $ffmpegExitCode = $LASTEXITCODE
    $ffmpegVersion = $ffmpegOutput | Select-Object -First 1
    if ($ffmpegExitCode -ne 0 -or $ffmpegVersion -notmatch 'ffmpeg version') {
        throw 'Packaged ffmpeg failed with an isolated PATH.'
    }

    $ffprobeOutput = & $ffprobe -version 2>&1
    $ffprobeExitCode = $LASTEXITCODE
    $ffprobeVersion = $ffprobeOutput | Select-Object -First 1
    if ($ffprobeExitCode -ne 0 -or $ffprobeVersion -notmatch 'ffprobe version') {
        throw 'Packaged ffprobe failed with an isolated PATH.'
    }
    $ffmpegRelease = [regex]::Match($ffmpegVersion, 'version\s+(?<version>\d+\.\d+(?:\.\d+)?)').Groups['version'].Value
    $ffprobeRelease = [regex]::Match($ffprobeVersion, 'version\s+(?<version>\d+\.\d+(?:\.\d+)?)').Groups['version'].Value
    if ($ffmpegRelease -ne '9.0.1' -or $ffprobeRelease -ne $ffmpegRelease) {
        throw "Packaged FFmpeg tools must be the matched 9.0.1 build, got ffmpeg=$ffmpegRelease ffprobe=$ffprobeRelease."
    }

    $ytDlpOutput = & $ytDlp --version 2>&1
    $ytDlpExitCode = $LASTEXITCODE
    $ytDlpVersion = ($ytDlpOutput | Select-Object -First 1).Trim()
    if ($ytDlpExitCode -ne 0 -or [Version]$ytDlpVersion -lt [Version]'2026.7.4') {
        throw "Packaged yt-dlp is missing or stale: $ytDlpVersion"
    }

    $denoOutput = & $deno --version 2>&1
    $denoExitCode = $LASTEXITCODE
    $denoVersionText = $denoOutput | Select-Object -First 1
    $denoVersion = [regex]::Match($denoVersionText, '\d+\.\d+\.\d+').Value
    if ($denoExitCode -ne 0 -or [Version]$denoVersion -lt [Version]'2.3.0') {
        throw "Packaged Deno is missing or stale: $denoVersionText"
    }

    if (-not $IncludeAi) {
        foreach ($forbiddenPath in @('ai', 'packaging\material_harvester_vsr.py', 'packaging\sanitize-ai-sidecar.ps1')) {
            if (Test-Path -LiteralPath (Join-Path $packageRoot $forbiddenPath)) {
                throw "NoAI package contains an AI component: $forbiddenPath"
            }
        }
        $models = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
            Where-Object { $_.Extension -in '.onnx', '.pth', '.pt', '.safetensors' })
        if ($models.Count -gt 0) { throw 'NoAI package contains model weights.' }
        [PSCustomObject]@{
            Package = $packageRoot
            Edition = 'NoAI'
            FFmpeg = $ffmpegVersion
            FFprobe = $ffprobeVersion
            YtDlp = $ytDlpVersion
            Deno = $denoVersion
        }
        return
    }

    & $python $wrapper --help *> $null
    $wrapperExitCode = $LASTEXITCODE
    if ($wrapperExitCode -ne 0) {
        throw 'Packaged subtitle-removal wrapper did not start.'
    }

    $restrictedFiles = Get-ChildItem (Join-Path $packageRoot 'ai\video-subtitle-remover') -Recurse -File |
        Where-Object {
            $_.FullName -match '(?i)propainter_inpaint|backend[\\/]inpaint[\\/]video[\\/]' -or
            ($_.FullName -match '(?i)backend[\\/]models[\\/]propainter[\\/]' -and
             $_.Name -ne 'ProPainter.pth') -or
            ($_.Name -eq 'ProPainter.pth' -and $_.Length -ne 0)
        }
    if ($restrictedFiles) {
        throw "Non-commercial ProPainter files must not be distributed: $($restrictedFiles.FullName -join ', ')"
    }

    $importProbeScript = @'
import importlib.util
import os
import sys
wrapper_path, sidecar_root = sys.argv[1:3]
os.chdir(sidecar_root)
sys.path.insert(0, sidecar_root)
spec = importlib.util.spec_from_file_location('mh_wrapper', wrapper_path)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
module._install_setuptools_config_compat()
module._install_unsupported_propainter_stub()
import backend.main
backend.main.ModelConfig()
print('SIDECAR_IMPORT_OK')
'@
    $savedErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $importProbeOutput = & $python -c $importProbeScript $wrapper (Split-Path $python -Parent) 2>&1
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    if ($LASTEXITCODE -ne 0 -or ($importProbeOutput | Select-Object -Last 1) -ne 'SIDECAR_IMPORT_OK') {
        throw "Sanitized AI sidecar import probe failed: $($importProbeOutput | Select-Object -Last 1)"
    }

    $maskProbeScript = @'
import importlib.util
import sys
import numpy as np
spec = importlib.util.spec_from_file_location('mh_wrapper', sys.argv[1])
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
source = np.arange(27, dtype=np.uint8).reshape(3, 3, 3)
repaired = np.full((3, 3, 3), 255, dtype=np.uint8)
mask = np.zeros((3, 3), dtype=np.uint8)
mask[1, 1] = 1
result = module._blend_repaired_frames([source], [repaired], mask)[0]
assert np.array_equal(result[mask == 0], source[mask == 0])
assert np.array_equal(result[1, 1], repaired[1, 1])
print('MASK_OK')
'@
    $savedErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $maskProbeOutput = & $python -c $maskProbeScript $wrapper 2>&1
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    if ($LASTEXITCODE -ne 0 -or ($maskProbeOutput | Select-Object -Last 1) -ne 'MASK_OK') {
        throw "Packaged subtitle mask-isolation probe failed: $($maskProbeOutput | Select-Object -Last 1)"
    }

    $probeScript = @'
import json
import torch
device = 'CUDA' if torch.cuda.is_available() else 'CPU'
result = 8.0
if device == 'CUDA':
    tensor = torch.ones(4, device='cuda') + 1
    torch.cuda.synchronize()
    result = float(tensor.sum().cpu())
print(json.dumps({'torch': torch.__version__, 'runtime': torch.version.cuda, 'device': device, 'result': result}))
'@
    $savedErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $probeOutput = & $python -c $probeScript 2>&1
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    $probeExitCode = $LASTEXITCODE
    $probeJson = $probeOutput | Select-Object -Last 1
    if ($probeExitCode -ne 0) {
        throw "Packaged PyTorch probe failed: $probeJson"
    }
    $probe = $probeJson | ConvertFrom-Json
    if ($RequireCuda -and $probe.device -ne 'CUDA') {
        throw "CUDA was required but the packaged runtime reported $($probe.device)."
    }
    if ([double]$probe.result -ne 8.0) {
        throw "Packaged tensor computation returned an unexpected result: $($probe.result)"
    }

    [PSCustomObject]@{
        Package = $packageRoot
        FFmpeg = $ffmpegVersion
        FFprobe = $ffprobeVersion
        YtDlp = $ytDlpVersion
        Deno = $denoVersion
        Torch = $probe.torch
        CudaRuntime = $probe.runtime
        AiDevice = $probe.device
    }
}
finally {
    $env:PATH = $savedPath
    if ($null -eq $savedCudaPath) {
        Remove-Item Env:CUDA_PATH -ErrorAction SilentlyContinue
    }
    else {
        $env:CUDA_PATH = $savedCudaPath
    }
    if ($null -eq $savedCudaPathV13) {
        Remove-Item Env:CUDA_PATH_V13_0 -ErrorAction SilentlyContinue
    }
    else {
        $env:CUDA_PATH_V13_0 = $savedCudaPathV13
    }
}
