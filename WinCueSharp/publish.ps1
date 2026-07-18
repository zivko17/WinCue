# =============================================================================
# publish.ps1 — Genera el WinCue.exe distribuible
# =============================================================================
# Single-file autocontenido (no requiere .NET instalado en la máquina destino).
# Copia libmpv-2.dll junto al exe (mpv no puede ir embebido: se carga por P/Invoke).
# FFmpeg/ffprobe deben estar en el PATH de la máquina destino (o junto al exe).
#
# Uso:  .\publish.ps1        → carpeta .\publish\ con WinCue.App.exe + libmpv-2.dll
# =============================================================================

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'publish'

dotnet publish (Join-Path $root 'src/WinCue.App') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $out

# libmpv junto al exe (desde la carpeta del proyecto Python hermano)
$mpv = Join-Path $root '..\WinCue\libmpv-2.dll'
if (Test-Path $mpv) {
    Copy-Item $mpv $out -Force
    Write-Host "libmpv-2.dll copiada junto al exe." -ForegroundColor Green
} else {
    Write-Warning "libmpv-2.dll no encontrada en ..\WinCue — copiala a mano junto al exe."
}

# ── Cumplimiento de licencias (auditoría 05) ────────────────────────────
# THIRD-PARTY-NOTICES.md + licenses/ SIEMPRE acompañan al ejecutable.
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $out -Force
Write-Host "THIRD-PARTY-NOTICES.md incluido." -ForegroundColor Green

$licSrc = Join-Path $root 'licenses'
$licDst = Join-Path $out 'licenses'
if (Test-Path $licSrc) {
    Copy-Item $licSrc $licDst -Recurse -Force
}
if (-not (Test-Path (Join-Path $licDst 'LGPL-2.1.txt'))) {
    Write-Warning ("FALTA licenses\LGPL-2.1.txt (texto oficial: " +
        "https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt). " +
        "OBLIGATORIO antes de distribuir con libmpv (LGPL). " +
        "Ver docs\legal\checklist-distribucion.md.")
}

Write-Host "`nPublicado en: $out" -ForegroundColor Green
Get-ChildItem $out | Format-Table Name, @{L='MB';E={[math]::Round($_.Length/1MB,1)}}
