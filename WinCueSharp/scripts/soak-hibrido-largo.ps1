# =============================================================================
# soak-hibrido-largo.ps1 — Soak del STREAMING del audio espejo (paso 4)
# =============================================================================
# Igual que soak-hibrido.ps1 pero con el show del CUE LARGO (tests/stress/
# estres-hibrido-largo.json): un vídeo H.264 de 6 minutos por la ruta híbrida
# cuyo audio espejo va en STREAMING (ffmpeg → ring acotado). LA evidencia del
# paso 4 es el proceso.csv: el working set debe quedarse PLANO durante el cue
# largo (antes el pre-decode a RAM crecía con la duración y el límite
# video_audio_max_minutes vetaba estos cues), y las trazas del PLL en
# engine-trace.log deben enseñar un arranque casi instantáneo del espejo.
#
# Uso:
#   .\scripts\soak-hibrido-largo.ps1              # 2 vueltas (~12 min de reloj)
#   .\scripts\soak-hibrido-largo.ps1 -Loops 4     # 4 vueltas
# =============================================================================
param(
    [int]$Loops = 2,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$show = Join-Path $root "tests\stress\estres-hibrido-largo.json"
if (-not (Test-Path $show)) { Write-Error "No existe el show: $show" }

# ── Media: el clip largo H.264 (generar si falta) ──────────────────────────
$longMedia = Join-Path $root "tests\hap-test\demo-h264-360s-audio.mp4"
if (-not (Test-Path $longMedia)) {
    Write-Host "Generando media H.264 híbrida (incluye el clip largo)..."
    powershell -NoProfile -File (Join-Path $root "tests\hap-test\generar-hibrido.ps1")
    if ($LASTEXITCODE -ne 0) { throw "generar-hibrido.ps1 falló" }
}

# ── Build ────────────────────────────────────────────────────────────────
Get-Process WinCue.App -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build WinCueSharp.sln -c $Configuration --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build falló" }
$binDir = Join-Path $root "src\WinCue.App\bin\$Configuration\net8.0-windows"
$exe = Join-Path $binDir "WinCue.App.exe"

# ── Compositor GPU ON en el settings.json del bin ─────────────────────────
$settingsPath = Join-Path $binDir "settings.json"
$settings = if (Test-Path $settingsPath) {
    Get-Content $settingsPath -Raw | ConvertFrom-Json
} else { [pscustomobject]@{} }
$settings | Add-Member -NotePropertyName video_compositor -NotePropertyValue $true -Force
$settings | ConvertTo-Json -Depth 20 | Set-Content $settingsPath -Encoding UTF8
Write-Host "Compositor GPU activado en $settingsPath"

# ── Carpeta de resultados + entorno de diagnóstico ─────────────────────────
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $root "artifacts\soak-hibrido-largo\$stamp"
New-Item -ItemType Directory -Force $out | Out-Null
$env:WINCUE_STRESS_CSV        = Join-Path $out "proceso.csv"
$env:WINCUE_STRESS_WATCH      = "1"
$env:WINCUE_STRESS_WATCH_LOG  = Join-Path $out "stress-watch.log"
$env:WINCUE_MPV_LOG           = $out          # engine-trace.log (pll incluido)

# ── Lanzar el soak ─────────────────────────────────────────────────────────
Write-Host "Soak largo: $Loops vueltas de $(Split-Path $show -Leaf) → $out"
$app = Start-Process $exe -ArgumentList "--stress", "`"$show`"", "--stress-loops", $Loops -PassThru
Write-Host "WinCue PID $($app.Id) — esperando el cierre solo (exit 0 a las $Loops vueltas)..."

# Tope de seguridad: ~380 s por vuelta + 5 min de margen
$timeoutMs = ($Loops * 380 + 300) * 1000
if (-not $app.WaitForExit($timeoutMs)) {
    Write-Warning "El soak no cerró solo; matando WinCue.App"
    $app.Kill()
}
Write-Host "`nSoak terminado (exit $($app.ExitCode)). Analizar:"
Write-Host "  $out\proceso.csv"
Write-Host "  $out\stress-watch.log"
Write-Host "  $out\engine-trace.log"
