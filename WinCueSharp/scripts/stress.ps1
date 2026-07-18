# =============================================================================
# stress.ps1 — Sesión de estrés de WinCue (auditoría 04)
# =============================================================================
# Lanza la app en modo --stress con el show de tortura, y en paralelo:
#   · dotnet-counters  → counters.csv (GC heap, gen0/1/2, LOH, working set…)
#   · dotnet-gcdump    → snapshot al inicio, cada hora y al final
# La app escribe su propio CSV (proceso.csv) cada 30 s vía WINCUE_STRESS_CSV.
# Todo queda en artifacts\stress\<fecha>\.
#
# Uso:
#   .\scripts\stress.ps1                      # infinito (Ctrl+C para parar)
#   .\scripts\stress.ps1 -Hours 4             # 4 h y cierre ordenado con dump final
#   .\scripts\stress.ps1 -Loops 50            # 50 vueltas del show y sale sola
#
# Requisitos (una vez):
#   dotnet tool install -g dotnet-counters
#   dotnet tool install -g dotnet-gcdump
#   powershell -File tools\generar-show-prueba.ps1   (medias del show)
# =============================================================================

param(
    [double]$Hours = 0,          # 0 = sin límite de tiempo
    [int]$Loops = 0,             # 0 = sin límite de vueltas
    [string]$Show = "",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if ($Show -eq "") { $Show = Join-Path $root "tests\stress\show-estres.json" }

# ── Comprobaciones previas ──────────────────────────────────────────────
foreach ($tool in 'dotnet-counters', 'dotnet-gcdump') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        Write-Error "Falta $tool. Instalar con: dotnet tool install -g $tool"
    }
}
if (-not (Test-Path $Show)) { Write-Error "No existe el show: $Show" }
$media = Join-Path $root "tests\show-prueba\video-A-colores.mp4"
if (-not (Test-Path $media)) {
    Write-Error "Faltan las medias. Generar con: powershell -File tools\generar-show-prueba.ps1"
}

# ── Build + carpeta de resultados ───────────────────────────────────────
dotnet build WinCueSharp.sln -c $Configuration --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build falló" }
$exe = Join-Path $root "src\WinCue.App\bin\$Configuration\net8.0-windows\WinCue.App.exe"

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $root "artifacts\stress\$stamp"
New-Item -ItemType Directory -Force $out | Out-Null
$env:WINCUE_STRESS_CSV = Join-Path $out "proceso.csv"

# ── Lanzar app + dotnet-counters ────────────────────────────────────────
$app = Start-Process $exe -ArgumentList "--stress", "`"$Show`"", "--stress-loops", $Loops -PassThru
Write-Host "WinCue PID $($app.Id) — resultados en $out"

$counters = Start-Process dotnet-counters -PassThru -NoNewWindow -ArgumentList `
    "collect", "--process-id", $app.Id, "--refresh-interval", "10",
    "--counters", "System.Runtime[gc-heap-size,gen-0-gc-count,gen-1-gc-count,gen-2-gc-count,loh-size,working-set,assembly-count]",
    "--format", "csv", "-o", (Join-Path $out "counters.csv")

function Take-GcDump([string]$name) {
    if ($app.HasExited) { Write-Warning "app cerrada: sin gcdump '$name'"; return }
    & dotnet-gcdump collect -p $app.Id -o (Join-Path $out "$name.gcdump") | Out-Null
    Write-Host "gcdump: $name.gcdump"
}

# ── Muestreador de VRAM (nivel 1 de la auditoría 04, automatizado):
#    memoria de GPU dedicada del proceso vía contadores de Windows ─────────
$gpuCsv = Join-Path $out "gpu.csv"
"timestamp,vram_dedicada_mb,vram_compartida_mb" | Set-Content $gpuCsv
function Sample-Gpu {
    try {
        $ded = (Get-Counter '\GPU Process Memory(*)\Dedicated Usage' -ErrorAction Stop).CounterSamples |
            Where-Object { $_.InstanceName -match "pid_$($app.Id)_" } |
            Measure-Object CookedValue -Sum
        $sha = (Get-Counter '\GPU Process Memory(*)\Shared Usage' -ErrorAction Stop).CounterSamples |
            Where-Object { $_.InstanceName -match "pid_$($app.Id)_" } |
            Measure-Object CookedValue -Sum
        # Cultura invariante: una coma decimal regional rompería el CSV
        [string]::Format([Globalization.CultureInfo]::InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss},{1:0.0},{2:0.0}",
            (Get-Date), ($ded.Sum / 1MB), ($sha.Sum / 1MB)) | Add-Content $gpuCsv
    } catch { }   # contadores de GPU no disponibles: se sigue sin VRAM
}

# ── Cronograma: dump inicial (t+60 s), horario, y final ─────────────────
Start-Sleep -Seconds 60
Take-GcDump "00-inicio"
Sample-Gpu

$deadline = if ($Hours -gt 0) { (Get-Date).AddHours($Hours) } else { [datetime]::MaxValue }
$nextHourly = (Get-Date).AddHours(1)
while (-not $app.HasExited -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 30
    Sample-Gpu
    if ((Get-Date) -ge $nextHourly) {
        Take-GcDump ("{0:HH}h" -f (Get-Date))
        $nextHourly = $nextHourly.AddHours(1)
    }
}

# ── Cierre: dump final con la app AÚN viva, luego cierre ordenado ───────
Take-GcDump "99-final"
if (-not $app.HasExited) {
    $app.CloseMainWindow() | Out-Null
    if (-not $app.WaitForExit(15000)) { $app.Kill() }
}
# dotnet-counters vuelca counters.csv AL SALIR: darle tiempo a detectar la
# muerte del proceso objetivo y salir solo (matarlo a la fuerza pierde el CSV)
if (-not $counters.WaitForExit(30000)) {
    Write-Warning "dotnet-counters no salió solo; counters.csv puede quedar incompleto"
    Stop-Process -Id $counters.Id -Force -ErrorAction SilentlyContinue
}

Write-Host "`nSesión terminada. Analizar:"
Write-Host "  $out\proceso.csv    (CSV de la app, cada 30 s)"
Write-Host "  $out\counters.csv   (dotnet-counters, cada 10 s)"
Write-Host "  $out\*.gcdump       (comparar en Visual Studio o PerfView)"
