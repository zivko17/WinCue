# =============================================================================
# soak-hibrido.ps1 — Soak de la RUTA HÍBRIDA de audio (paso 3b)
# =============================================================================
# Igual que soak-hap.ps1 pero con el show de TORTURA HÍBRIDA (tests/stress/
# estres-hibrido.json): cues H.264 por la ruta híbrida (espejo de audio +
# mpv mudo + PLL) intercalados con cues HAP del motor propio, con:
#   · el compositor GPU ACTIVADO (video_compositor=true en el settings.json
#     del bin) — necesario para que los HAP intercalados usen el motor propio;
#   · el WATCHDOG de salud encendido (WINCUE_STRESS_WATCH=1);
#   · las trazas del motor (WINCUE_MPV_LOG → engine-trace.log: pll speed/seeks).
# Todo queda en artifacts\soak-hibrido\<fecha>\.
#
# Uso:
#   .\scripts\soak-hibrido.ps1                 # 15 vueltas (~10 min de reloj)
#   .\scripts\soak-hibrido.ps1 -Loops 25       # 25 vueltas
# =============================================================================
param(
    [int]$Loops = 15,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$show = Join-Path $root "tests\stress\estres-hibrido.json"
if (-not (Test-Path $show)) { Write-Error "No existe el show: $show" }

# ── Media: HAP (tortura) + H.264 (híbrida) — generar la que falte ─────────
$hapMedia = Join-Path $root "tests\hap-test\demo-hap-1080p30-audio.mov"
if (-not (Test-Path $hapMedia)) {
    Write-Host "Generando media HAP de tortura..."
    powershell -NoProfile -File (Join-Path $root "tests\hap-test\generar-tortura.ps1")
    if ($LASTEXITCODE -ne 0) { throw "generar-tortura.ps1 falló" }
}
$h264Media = Join-Path $root "tests\hap-test\demo-h264-1080p30-audio.mp4"
if (-not (Test-Path $h264Media)) {
    Write-Host "Generando media H.264 híbrida..."
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
$out = Join-Path $root "artifacts\soak-hibrido\$stamp"
New-Item -ItemType Directory -Force $out | Out-Null
$env:WINCUE_STRESS_CSV        = Join-Path $out "proceso.csv"
$env:WINCUE_STRESS_WATCH      = "1"
$env:WINCUE_STRESS_WATCH_LOG  = Join-Path $out "stress-watch.log"
$env:WINCUE_MPV_LOG           = $out          # engine-trace.log (pll incluido)

# ── Lanzar el soak ─────────────────────────────────────────────────────────
Write-Host "Soak híbrido: $Loops vueltas de $(Split-Path $show -Leaf) → $out"
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
