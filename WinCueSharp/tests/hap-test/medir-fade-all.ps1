# =============================================================================
# medir-fade-all.ps1 — mide el FADE ALL (y el PANIC) EN LA SALIDA REAL
# =============================================================================
# Hermano de medir-crossfade.ps1, mismo método (INFORME § "Fallo C"): media de
# LUMA PLANA, captura por Desktop Duplication a 60 fps y luma por fotograma con
# signalstats. Aquí lo que se mide no es un cruce sino una BAJADA A NEGRO:
#
#   FADE ALL correcto  = rampa de la meseta (≈197) a negro (≈16) que dura los
#                        segundos configurados en fade_all_seconds.
#   FADE ALL roto      = la luma cae de la meseta a negro en 1-2 fotogramas
#                        (la imagen se corta) aunque el audio sí funda.
#
# El disparo va por OSC (/wincue/fadeall o /wincue/panic) mientras la app corre
# desatendida con --stress: el show es UN vídeo en LOOP, así que la salida está
# en la meseta hasta que llega el mensaje.
#
#   powershell -File medir-fade-all.ps1 -Show show-fadeall-hap.json
#   powershell -File medir-fade-all.ps1 -Show show-fadeall-h264.json -Accion panic
# =============================================================================
param(
    [Parameter(Mandatory = $true)][string]$Show,
    [ValidateSet("fadeall", "panic")][string]$Accion = "fadeall",
    [string]$Exe = "",
    [int]$Monitor = 1,          # índice de Screen.AllScreens = output_idx de ddagrab
    [double]$Fade = 5.0,        # fade_all_seconds / panic_fade a probar
    [int]$EsperaGo = 6,         # s de meseta antes de disparar
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
$tag = "{0}-{1}" -f [IO.Path]::GetFileNameWithoutExtension($Show), $Accion
$mkv = Join-Path $OutDir "$tag.mkv"
$csv = Join-Path $OutDir "$tag-luma.csv"
$seconds = $EsperaGo + [int][Math]::Ceiling($Fade) + 8

# --- settings.json: compositor, salida, OSC y tiempo del fundido -------------
$settings = Join-Path (Split-Path $Exe) "settings.json"
if (-not (Test-Path $settings)) { Start-Process $Exe -ArgumentList "--smoke" -Wait }
$s = Get-Content $settings -Raw | ConvertFrom-Json
$deseado = @{
    video_compositor        = $true
    video_output_screen     = $Monitor
    video_fullscreen_output = $true
    osc_enabled             = $true
    osc_listen_port         = 8000
    fade_all_seconds        = $Fade
    panic_fade              = $Fade
    panic_action            = "black"
    panic_double_hard       = $false
}
foreach ($kv in $deseado.GetEnumerator()) {
    if ($s.PSObject.Properties.Name -contains $kv.Key) { $s.$($kv.Key) = $kv.Value }
    else { $s | Add-Member -NotePropertyName $kv.Key -NotePropertyValue $kv.Value }
}
$s | ConvertTo-Json -Depth 8 | Set-Content $settings -Encoding UTF8
Write-Host "settings.json: compositor ON, salida -> monitor $Monitor, OSC 8000, fundido $Fade s"

Get-Process WinCue.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

# --- 1) captura por Desktop Duplication -------------------------------------
Write-Host "Capturando monitor $Monitor a 60 fps (ffv1), $seconds s..."
$capArgs = @(
    "-hide_banner", "-loglevel", "error", "-y",
    "-init_hw_device", "d3d11va",
    "-filter_complex", "ddagrab=output_idx=${Monitor}:framerate=60,hwdownload,format=bgra",
    "-t", "$seconds", "-c:v", "ffv1", $mkv
)
$cap = Start-Process ffmpeg -ArgumentList $capArgs -PassThru -NoNewWindow
Start-Sleep -Seconds 2

# --- 2) show desatendido (vídeo en LOOP: la meseta se sostiene) -------------
$env:WINCUE_MPV_LOG = $OutDir
Write-Host "Reproduciendo $Show ..."
$app = Start-Process $Exe -ArgumentList @("--stress", $showPath, "--stress-loops", "0") -PassThru

# --- 3) disparo del FADE ALL / PANIC por OSC --------------------------------
function Send-Osc([string]$address) {
    # Mensaje OSC mínimo: dirección + typetag ",", ambos rellenados a múltiplo
    # de 4 con NUL. Sin argumentos (el router trata "sin args" como pulsación).
    function Pad([string]$t) {
        $b = [Text.Encoding]::ASCII.GetBytes($t) + [byte]0
        while ($b.Count % 4 -ne 0) { $b += [byte]0 }
        return $b
    }
    $dg = (Pad $address) + (Pad ",")
    $udp = New-Object Net.Sockets.UdpClient
    [void]$udp.Send($dg, $dg.Count, "127.0.0.1", 8000)
    $udp.Close()
}

Start-Sleep -Seconds $EsperaGo
Write-Host "OSC -> /wincue/$Accion"
Send-Osc "/wincue/$Accion"

$cap.WaitForExit(($seconds + 20) * 1000) | Out-Null
if (-not $cap.HasExited) { $cap.Kill() }
if (-not $app.HasExited) { $app.Kill() }
Write-Host "Captura: $mkv"

# --- 4) luma por fotograma ---------------------------------------------------
$raw = & ffmpeg -hide_banner -loglevel error -i $mkv `
    -vf "signalstats,metadata=print:file=-" -f null - 2>&1
$yavg = @()
foreach ($line in $raw) {
    if ($line -match "lavfi\.signalstats\.YAVG=([0-9.]+)") { $yavg += [double]$Matches[1] }
}
if ($yavg.Count -eq 0) { throw "signalstats no devolvió ningún fotograma" }
$i = 0
$yavg | ForEach-Object { "{0},{1:F2}" -f ($i++), $_ } | Set-Content $csv
Write-Host "Luma por fotograma: $csv  ($($yavg.Count) fotogramas)"

# --- 5) resumen: ¿RAMPA o CORTE? --------------------------------------------
# La meseta es el máximo sostenido; el negro de la salida ronda 16. La rampa va
# del ÚLTIMO fotograma aún en meseta al PRIMERO ya en negro.
$alto = ($yavg | Measure-Object -Maximum).Maximum
if ($alto -lt 100) { throw "no se vio la meseta del vídeo (max $alto): ¿salida en el monitor $Monitor?" }
$umbralAlto = $alto - 8
$umbralNegro = 25

# OJO: el arnés desatendido RELANZA el show unos segundos después (3 s sin
# nada sonando), así que la meseta reaparece más adelante. La ventana buena es
# la PRIMERA: del primer fotograma en meseta al primer negro posterior, y de
# ahí hacia atrás al último fotograma que aún estaba en meseta.
$iPrimera = -1
for ($k = 0; $k -lt $yavg.Count; $k++) {
    if ($yavg[$k] -ge $umbralAlto) { $iPrimera = $k; break }
}
$iNegro = -1
for ($k = $iPrimera; $k -lt $yavg.Count; $k++) {
    if ($yavg[$k] -le $umbralNegro) { $iNegro = $k; break }
}
$iMeseta = $iPrimera
for ($k = $iPrimera; $k -lt $iNegro; $k++) { if ($yavg[$k] -ge $umbralAlto) { $iMeseta = $k } }
Write-Host ""
Write-Host "=== RESUMEN $tag ==="
Write-Host ("  fotogramas capturados : {0}" -f $yavg.Count)
Write-Host ("  meseta                : {0:F1}" -f $alto)
if ($iNegro -lt 0) {
    Write-Host "  la imagen NUNCA llega a negro: el fundido no terminó dentro de la captura"
    return
}
$frames = $iNegro - $iMeseta
$segundos = $frames / 60.0
Write-Host ("  rampa meseta->negro   : {0} fotogramas = {1:F2} s (esperado ~{2:F1} s)" -f $frames, $segundos, $Fade)
Write-Host ("  luma en la rampa      : {0}" -f (($yavg[$iMeseta..$iNegro] | ForEach-Object { "{0:F0}" -f $_ }) -join " "))
if ($segundos -ge $Fade * 0.75) {
    Write-Host "  -> FUNDIDO DE IMAGEN CORRECTO"
} else {
    Write-Host "  -> CORTE: la imagen desaparece en $frames fotograma(s), no funde"
}
