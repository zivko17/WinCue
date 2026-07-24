# =============================================================================
# generar-hibrido.ps1 — Media H.264 para el soak de la RUTA HÍBRIDA (paso 3b)
# =============================================================================
# Genera clips H.264 (no versionados: tests/hap-test/*.mp4 y *.mov van en el
# .gitignore) que el show tests/stress/estres-hibrido.json intercala con los
# HAP de generar-tortura.ps1 para martillear la ruta híbrida:
#   · CON audio AAC (el caso normal: espejo por el AudioEngine + mpv mudo).
#   · SIN audio (el espejo emite error → fallback RestoreInternalAudio en
#     caliente: el cue no debe quedarse colgado ni doblar audio).
#   · Con pcm 96 kHz GRANDE en .mov (espejo lento en decodificar → arranque
#     tardío: la frenada del PLL, no un rebobinado en pantalla).
#   · 30 y 60 fps y resoluciones variadas (spec real de mpv variando).
# Requiere ffmpeg en el PATH. Ejecutar desde esta carpeta:
#   powershell -File generar-hibrido.ps1
# =============================================================================
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot

function H264 {
    param(
        [string]$Name, [string]$Size, [int]$Rate, [double]$Dur,
        [string]$Audio = 'aac', [string]$Pattern = 'testsrc2'
    )
    $out = Join-Path $dir $Name
    $args = @(
        '-hide_banner', '-loglevel', 'error', '-y',
        '-f', 'lavfi', '-i', "${Pattern}=size=${Size}:rate=${Rate}:duration=${Dur}"
    )
    switch ($Audio) {
        'aac'    { $args += @('-f', 'lavfi', '-i', "sine=frequency=440:duration=${Dur}", '-c:a', 'aac', '-b:a', '128k') }
        'pcmbig' { $args += @('-f', 'lavfi', '-i', "sine=frequency=220:sample_rate=96000:duration=${Dur}", '-c:a', 'pcm_s16le', '-ar', '96000') }
        'none'   { $args += @('-an') }
    }
    $args += @('-c:v', 'libx264', '-preset', 'veryfast', '-pix_fmt', 'yuv420p', $out)
    & ffmpeg @args
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg falló generando $Name" }
    Write-Host "  $Name"
}

Write-Host "Generando media H.264 híbrida en $dir ..."

# 1080p30 con audio AAC — el cue híbrido "normal"
H264 'demo-h264-1080p30-audio.mp4'   '1920x1080' 30 5.0 'aac'
# 1080p30 SIN audio — el espejo falla → fallback RestoreInternalAudio
H264 'demo-h264-1080p30-noaudio.mp4' '1920x1080' 30 4.0 'none'
# 1080p60 con audio — cadencia alta
H264 'demo-h264-1080p60-audio.mp4'   '1920x1080' 60 3.0 'aac'
# 720p30 con pcm 96 kHz GRANDE en .mov — espejo tardío (frenada del PLL);
# el contenedor .mov además ejercita el sondeo de códec (no-HAP → híbrido)
H264 'demo-h264-720p30-bigpcm.mov'   '1280x720'  30 6.0 'pcmbig'
# 360p30 de 6 MINUTOS con audio — el cue LARGO del paso 4 (streaming del
# espejo): antes el pre-decode a RAM lo vetaba (límite 20 min por defecto y
# ~140 MB de float); ahora el ring acotado debe dejar el working set PLANO
H264 'demo-h264-360s-audio.mp4'      '640x360'   30 360.0 'aac'

Write-Host "Media H.264 híbrida generada."
