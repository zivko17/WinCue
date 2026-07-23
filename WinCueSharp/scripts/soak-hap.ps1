# =============================================================================
# soak-hap.ps1 — Soak DE VERDAD del motor HAP propio (endurecimiento, Fase 0)
# =============================================================================
# Lanza la app en modo --stress con el show de TORTURA HAP (tests/stress/
# estres-hap.json), con:
#   · el compositor GPU ACTIVADO (video_compositor=true en el settings.json del
#     bin) — sin él el show no cae por la ruta del motor HAP propio;
#   · el WATCHDOG de salud HAP encendido (WINCUE_STRESS_WATCH=1) — diagnóstico
#     autónomo: cuentas negativas, cues colgados, cadena estancada, fugas;
#   · las trazas verbosas del motor (WINCUE_MPV_LOG → engine-trace.log).
# Todo queda en artifacts\soak-hap\<fecha>\:
#   proceso.csv        CSV de la app (memoria/handles/GC cada 30 s)
#   stress-watch.log   log del watchdog (anomalías + resumen)
#   engine-trace.log   trazas del motor HAP (play/end/pasadas/drops)
#
# Uso:
#   .\scripts\soak-hap.ps1                 # 25 vueltas (~17 min de reloj)
#   .\scripts\soak-hap.ps1 -Loops 40       # 40 vueltas
# =============================================================================
param(
    [int]$Loops = 25,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$show = Join-Path $root "tests\stress\estres-hap.json"
if (-not (Test-Path $show)) { Write-Error "No existe el show: $show" }
$media = Join-Path $root "tests\hap-test\demo-hap-1080p30-audio.mov"
if (-not (Test-Path $media)) {
    Write-Error "Falta la media HAP de tortura. Generar con: powershell -File tests\hap-test\generar-tortura.ps1"
}

# ── Build ────────────────────────────────────────────────────────────────
Get-Process WinCue.App -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build WinCueSharp.sln -c $Configuration --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build falló" }
$binDir = Join-Path $root "src\WinCue.App\bin\$Configuration\net8.0-windows"
$exe = Join-Path $binDir "WinCue.App.exe"

# ── Compositor GPU ON en el settings.json del bin (ruta del motor HAP) ─────
$settingsPath = Join-Path $binDir "settings.json"
$settings = if (Test-Path $settingsPath) {
    Get-Content $settingsPath -Raw | ConvertFrom-Json
} else { [pscustomobject]@{} }
$settings | Add-Member -NotePropertyName video_compositor -NotePropertyValue $true -Force
$settings | ConvertTo-Json -Depth 20 | Set-Content $settingsPath -Encoding UTF8
Write-Host "Compositor GPU activado en $settingsPath"

# ── Carpeta de resultados + entorno de diagnóstico ─────────────────────────
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $root "artifacts\soak-hap\$stamp"
New-Item -ItemType Directory -Force $out | Out-Null
$env:WINCUE_STRESS_CSV        = Join-Path $out "proceso.csv"
$env:WINCUE_STRESS_WATCH      = "1"
$env:WINCUE_STRESS_WATCH_LOG  = Join-Path $out "stress-watch.log"
$env:WINCUE_MPV_LOG           = $out          # engine-trace.log en la misma carpeta

# ── Lanzar el soak ─────────────────────────────────────────────────────────
Write-Host "Soak HAP: $Loops vueltas de $(Split-Path $show -Leaf) → $out"
$app = Start-Process $exe -ArgumentList "--stress", "`"$show`"", "--stress-loops", $Loops -PassThru
Write-Host "WinCue PID $($app.Id) — esperando el cierre solo (exit 0 a las $Loops vueltas)..."

# Tope de seguridad: ~90 s por vuelta + 5 min de margen
$timeoutMs = ($Loops * 90 + 300) * 1000
if (-not $app.WaitForExit($timeoutMs)) {
    Write-Warning "El soak no cerró solo; matando WinCue.App"
    $app.Kill()
}
Write-Host "`nSoak terminado (exit $($app.ExitCode)). Analizar:"
Write-Host "  $out\proceso.csv"
Write-Host "  $out\stress-watch.log"
Write-Host "  $out\engine-trace.log"
