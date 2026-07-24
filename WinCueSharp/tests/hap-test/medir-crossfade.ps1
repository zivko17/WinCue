# =============================================================================
# medir-crossfade.ps1 — mide un crossfade EN LA SALIDA REAL, por fotograma
# =============================================================================
# Automatiza el método del INFORME (§ "Fallo C"): con la media de luma PLANA
# que genera generar-crossfade.ps1 (A oscuro ≈66, B claro ≈197), un crossfade
# correcto es una RAMPA LIMPIA entre las dos mesetas. Cualquier fotograma POR
# DEBAJO de la meseta baja es negro colándose en la mezcla — el fallo, con
# número.
#
#   1. Captura la salida por Desktop Duplication (ddagrab) a 60 fps sin
#      pérdidas (ffv1) mientras corre el show.
#   2. Reproduce el show desatendido con --stress <show> --stress-loops 1.
#   3. Saca lavfi.signalstats.YAVG de CADA fotograma y resume:
#      mesetas, rampa, y cuántos fotogramas caen por debajo de la meseta baja.
#
# Requisitos: ffmpeg en el PATH, la salida 1 de WinCue configurada en el
# monitor <-Monitor> a pantalla completa y el compositor GPU activo
# (Settings → Vídeo). El script comprueba y ajusta settings.json.
#
#   powershell -File medir-crossfade.ps1 -Show show-congelado-hap.json
# =============================================================================
param(
    [Parameter(Mandatory = $true)][string]$Show,
    [string]$Exe = "",
    [int]$Monitor = 1,          # índice de Screen.AllScreens = output_idx de ddagrab
    [int]$Seconds = 25,         # tope de captura
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
if ($OutDir -eq "") { $OutDir = Join-Path $env:TEMP "wincue-medida" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

if ($Exe -eq "") {
    $Exe = Join-Path $here "..\..\src\WinCue.App\bin\Release\net8.0-windows\WinCue.App.exe"
}
$Exe = (Resolve-Path $Exe).Path
$showPath = (Resolve-Path (Join-Path $here $Show)).Path
$tag = [IO.Path]::GetFileNameWithoutExtension($Show)
$mkv = Join-Path $OutDir "$tag.mkv"
$csv = Join-Path $OutDir "$tag-luma.csv"

# --- settings.json de la app: compositor + salida al monitor pedido ---------
$settings = Join-Path (Split-Path $Exe) "settings.json"
if (-not (Test-Path $settings)) {
    # Primer arranque de esta copia: que se escriba con los valores por defecto
    Start-Process $Exe -ArgumentList "--smoke" -Wait
}
if (Test-Path $settings) {
    $s = Get-Content $settings -Raw | ConvertFrom-Json
    foreach ($kv in @{
        video_compositor        = $true
        video_output_screen     = $Monitor
        video_fullscreen_output = $true
    }.GetEnumerator()) {
        if ($s.PSObject.Properties.Name -contains $kv.Key) { $s.$($kv.Key) = $kv.Value }
        else { $s | Add-Member -NotePropertyName $kv.Key -NotePropertyValue $kv.Value }
    }
    $s | ConvertTo-Json -Depth 8 | Set-Content $settings -Encoding UTF8
    Write-Host "settings.json: compositor ON, salida 1 -> monitor $Monitor, fullscreen"
}

Get-Process WinCue.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

# --- 1) captura por Desktop Duplication ------------------------------------
Write-Host "Capturando monitor $Monitor a 60 fps (ffv1)..."
$capArgs = @(
    "-hide_banner", "-loglevel", "error", "-y",
    "-init_hw_device", "d3d11va",
    "-filter_complex", "ddagrab=output_idx=${Monitor}:framerate=60,hwdownload,format=bgra",
    "-t", "$Seconds", "-c:v", "ffv1", $mkv
)
$cap = Start-Process ffmpeg -ArgumentList $capArgs -PassThru -NoNewWindow
Start-Sleep -Seconds 2   # que la captura esté rodando antes del primer GO

# --- 2) show desatendido ---------------------------------------------------
$env:WINCUE_MPV_LOG = $OutDir
Write-Host "Reproduciendo $Show ..."
$app = Start-Process $Exe -ArgumentList @("--stress", $showPath, "--stress-loops", "1") -PassThru
$app.WaitForExit(($Seconds + 20) * 1000) | Out-Null
if (-not $app.HasExited) { $app.Kill() }

$cap.WaitForExit(20000) | Out-Null
if (-not $cap.HasExited) { $cap.Kill() }
Write-Host "Captura: $mkv"

# --- 3) luma por fotograma -------------------------------------------------
$raw = & ffmpeg -hide_banner -loglevel error -i $mkv `
    -vf "signalstats,metadata=print:file=-" -f null - 2>&1
$yavg = @()
foreach ($line in $raw) {
    if ($line -match "lavfi\.signalstats\.YAVG=([0-9.]+)") {
        $yavg += [double]$Matches[1]
    }
}
if ($yavg.Count -eq 0) { throw "signalstats no devolvió ningún fotograma" }
$i = 0
$yavg | ForEach-Object { "{0},{1:F2}" -f ($i++), $_ } | Set-Content $csv
Write-Host "Luma por fotograma: $csv  ($($yavg.Count) fotogramas)"

# --- resumen ---------------------------------------------------------------
# La captura incluye escritorio (≈45) y negro puro (16) antes y después del
# show: la VENTANA DE ANÁLISIS va del primer fotograma en la meseta alta al
# último en la meseta baja — dentro de ella, un cruce correcto es monótono y
# JAMÁS baja de la meseta baja.
$i0 = -1; $i1 = -1
for ($k = 0; $k -lt $yavg.Count; $k++) {
    if ($i0 -lt 0 -and $yavg[$k] -ge 190) { $i0 = $k }
    if ($i0 -ge 0 -and $yavg[$k] -ge 60) { $i1 = $k }
}
Write-Host ""
Write-Host "=== RESUMEN $tag ==="
Write-Host ("  fotogramas capturados : {0}" -f $yavg.Count)
if ($i0 -lt 0 -or $i1 -le $i0) {
    Write-Host "  no se encontró la meseta alta: ¿la salida no estaba en el monitor $Monitor?"
    return
}
$win = $yavg[$i0..$i1]
$cola = $win[[int]($win.Count * 0.85)..($win.Count - 1)]
$bajo = ($cola | Measure-Object -Average).Average
$alto = ($win | Measure-Object -Maximum).Maximum
$minW = ($win | Measure-Object -Minimum).Minimum
$dips = @($win | Where-Object { $_ -lt $bajo - 3 })
Write-Host ("  ventana del cruce     : {0} fotogramas ({1:F2} s a 60 fps)" -f $win.Count, ($win.Count / 60.0))
Write-Host ("  meseta alta           : {0:F1}" -f $alto)
Write-Host ("  meseta baja           : {0:F1}" -f $bajo)
Write-Host ("  MÍNIMO en la ventana  : {0:F1}" -f $minW)
Write-Host ("  bajo la meseta baja   : {0} fotogramas" -f $dips.Count)
if ($dips.Count -eq 0) {
    Write-Host "  -> CRUCE LIMPIO: la rampa nunca cae por debajo de las mesetas"
} else {
    Write-Host ("  -> BAJÓN: la mezcla se hunde hasta {0:F1} (negro en la mezcla)" -f $minW)
}
