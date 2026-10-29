param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
$sidecarRoot = Join-Path $packageRoot 'ai\video-subtitle-remover'
if (-not (Test-Path -LiteralPath (Join-Path $sidecarRoot 'backend\main.py') -PathType Leaf)) {
    throw "Refusing to sanitize an unexpected directory: $sidecarRoot"
}

$targets = @(
    'backend\inpaint\propainter_inpaint.py',
    'backend\inpaint\__pycache__\propainter_inpaint.cpython-313.pyc',
    'backend\inpaint\video',
    'backend\models\propainter'
)

foreach ($relativePath in $targets) {
    $target = [IO.Path]::GetFullPath((Join-Path $sidecarRoot $relativePath))
    $expectedPrefix = $sidecarRoot.TrimEnd('\') + '\'
    if (-not $target.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Resolved removal target escaped the AI sidecar: $target"
    }
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

# Upstream ModelConfig checks only for this filename during initialization.
# The runtime wrapper rejects ProPainter and provides a fail-closed import
# placeholder, so the zero-byte sentinel prevents an attempted shard merge.
$placeholderDirectory = Join-Path $sidecarRoot 'backend\models\propainter'
New-Item -ItemType Directory -Path $placeholderDirectory -Force | Out-Null
$placeholder = Join-Path $placeholderDirectory 'ProPainter.pth'
[IO.File]::WriteAllBytes($placeholder, [byte[]]::new(0))

$forbidden = Get-ChildItem -LiteralPath $sidecarRoot -Recurse -File |
    Where-Object {
        $_.FullName -match '(?i)propainter_inpaint|backend[\\/]inpaint[\\/]video[\\/]' -or
        ($_.FullName -match '(?i)backend[\\/]models[\\/]propainter[\\/]' -and
         $_.FullName -ne $placeholder)
    }
if ($forbidden) {
    throw "Restricted ProPainter files remain: $($forbidden.FullName -join ', ')"
}

Write-Output "Removed unused non-commercial ProPainter code and weights from $packageRoot"
