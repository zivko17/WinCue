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

Write-Host "`nPublicado en: $out" -ForegroundColor Green
Get-ChildItem $out | Format-Table Name, @{L='MB';E={[math]::Round($_.Length/1MB,1)}}
