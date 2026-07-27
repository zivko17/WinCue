# =============================================================================
# soak-prores.ps1 — Soak del motor ProRes propio, MEZCLADO con HAP y H.264
# =============================================================================
# Hermano de soak-hap.ps1. Lanza la app en modo --stress con el show de tortura
# MIXTO (tests/stress/estres-prores.json), con:
#   · el compositor GPU ACTIVADO y el motor ProRes ACTIVADO en el settings.json
#     del bin — sin las dos cosas el show no cae por la ruta del motor propio;
#   · el WATCHDOG encendido (WINCUE_STRESS_WATCH=1). Aquí no es un lujo: un
#     frame 4K 4444 sin liberar son 63 MB, así que una fuga de frames nativos
#     se ve en el detector (d) en pocas vueltas;
#   · las trazas verbosas del motor (WINCUE_MPV_LOG → engine-trace.log), donde
#     salen las líneas "prores: play/terminate/sesión nueva" que demuestran por
#     qué motor entró cada cue.
# Todo queda en artifacts\soak-prores\<fecha>\.
#
# Uso:
#   .\scripts\soak-prores.ps1                 # 25 vueltas (~22 min de reloj)
#   .\scripts\soak-prores.ps1 -Loops 40
#   .\scripts\soak-prores.ps1 -SinMotorPropio # control: todo por mpv
# =============================================================================
param(
    [int]$Loops = 25,
    [string]$Configuration = "Release",
    [switch]$SinMotorPropio
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$show = Join-Path $root "tests\stress\estres-prores.json"
if (-not (Test-Path $show)) { Write-Error "No existe el show: $show" }
$media = Join-Path $root "tests\prores-test\pr-1080p30-422hq-audio.mov"
if (-not (Test-Path $media)) {
    Write-Error "Falta la media ProRes de tortura. Generar con: powershell -File tests\prores-test\generar-media.ps1"
}
$mediaHap = Join-Path $root "tests\hap-test\demo-hap-1080p30-audio.mov"
if (-not (Test-Path $mediaHap)) {
    Write-Error "Falta la media HAP (el show es MIXTO). Generar con: powershell -File tests\hap-test\generar-tortura.ps1"
}
if (-not (Test-Path (Join-Path $root "ffmpeg-lgpl\bin"))) {
    Write-Warning ("No hay ffmpeg-lgpl\bin: los ProRes caerán a mpv y el soak no probará " +
        "el motor propio. Obtén las DLL con tools\ffmpeg-lgpl\obtener-ffmpeg-lgpl.ps1.")
}

# ── Build ────────────────────────────────────────────────────────────────
Get-Process WinCue.App -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build WinCueSharp.sln -c $Configuration --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build falló" }
$binDir = Join-Path $root "src\WinCue.App\bin\$Configuration\net8.0-windows"
$exe = Join-Path $binDir "WinCue.App.exe"

# ── Compositor GPU ON + motor ProRes según el modo ───────────────────────
$settingsPath = Join-Path $binDir "settings.json"
$settings = if (Test-Path $settingsPath) {
    Get-Content $settingsPath -Raw | ConvertFrom-Json
} else { [pscustomobject]@{} }
$settings | Add-Member -NotePropertyName video_compositor -NotePropertyValue $true -Force
$settings | Add-Member -NotePropertyName video_prores_engine -NotePropertyValue (-not $SinMotorPropio) -Force
$settings | ConvertTo-Json -Depth 20 | Set-Content $settingsPath -Encoding UTF8
Write-Host "Compositor GPU ON; motor ProRes $(if ($SinMotorPropio) { 'OFF (control por mpv)' } else { 'ON' }) en $settingsPath"

# ── Carpeta de resultados + entorno de diagnóstico ───────────────────────
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $root "artifacts\soak-prores\$stamp"
New-Item -ItemType Directory -Force $out | Out-Null
$env:WINCUE_STRESS_CSV        = Join-Path $out "proceso.csv"
$env:WINCUE_STRESS_WATCH      = "1"
$env:WINCUE_STRESS_WATCH_LOG  = Join-Path $out "stress-watch.log"
$env:WINCUE_MPV_LOG           = $out          # engine-trace.log en la misma carpeta

# ── Lanzar el soak ───────────────────────────────────────────────────────
Write-Host "Soak ProRes mixto: $Loops vueltas de $(Split-Path $show -Leaf) → $out"
$app = Start-Process $exe -ArgumentList "--stress", "`"$show`"", "--stress-loops", $Loops -PassThru
Write-Host "WinCue PID $($app.Id) — esperando el cierre solo (exit 0 a las $Loops vueltas)..."

# Tope de seguridad: ~110 s por vuelta + 5 min de margen
$timeoutMs = ($Loops * 110 + 300) * 1000
if (-not $app.WaitForExit($timeoutMs)) {
    Write-Warning "El soak no cerró solo; matando WinCue.App"
    $app.Kill()
}

Write-Host "`nSoak terminado (exit $($app.ExitCode))."

# ── Resumen rápido de la traza: ¿entró de verdad por el motor propio? ────
$trace = Join-Path $out "engine-trace.log"
if (Test-Path $trace) {
    $pr = (Select-String -Path $trace -Pattern 'prores: play' -SimpleMatch).Count
    $hap = (Select-String -Path $trace -Pattern 'hap: play' -SimpleMatch).Count
    $fall = (Select-String -Path $trace -Pattern 'prores: demux/carga falló','prores: arranque falló').Count
    Write-Host ""
    Write-Host "  cues por el motor ProRes : $pr"
    Write-Host "  cues por el motor HAP    : $hap"
    Write-Host "  fallbacks de ProRes a mpv: $fall"
}
Write-Host ""
Write-Host "Analizar:"
Write-Host "  $out\proceso.csv"
Write-Host "  $out\stress-watch.log"
Write-Host "  $out\engine-trace.log"
