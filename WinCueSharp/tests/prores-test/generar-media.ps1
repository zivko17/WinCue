# =============================================================================
# generar-media.ps1 — Media ProRes de TORTURA para el soak del motor propio
# =============================================================================
# Hermano de tests/hap-test/generar-tortura.ps1. Genera clips ProRes (no
# versionados: los cubre .gitignore) para martillear WinCue.Video.ProRes, más
# los clips H.264 que hacen falta para el show MIXTO: lo que de verdad hay que
# probar no es ProRes solo, sino ProRes conviviendo con HAP y con mpv en el
# mismo show y en el mismo pool de superficies.
#
# Qué se cubre y por qué:
#   · Los CUATRO perfiles que cambian la spec de las texturas: 422 (apcn),
#     422 HQ (apch), Proxy (apco) y 4444 (ap4h). El 4444 sale a 12 bits y con
#     CUATRO planos: es el que fuerza reconstruir la sesión del pool.
#   · Con y SIN pista de audio: el audio espejo es el reloj maestro; sin él el
#     vídeo corre con su Stopwatch y se prueba el envolvido por tiempo.
#   · Resoluciones distintas (720p / 1080p / 4K) → cambios de spec del pool.
#   · 30 y 60 fps.
#   · Un clip de RUIDO: testsrc2 es sintético y ProRes lo comprime muy por
#     debajo de su tasa nominal. Con noise el bitrate sube a lo que pesa un
#     ProRes de verdad (1080p 422 HQ: 232 Mbit/s frente a los 75 de testsrc2),
#     que es el único caso honesto para medir.
#   · Un clip con pcm a 96 kHz: el espejo de audio tarda más en arrancar y se
#     reproduce el arranque tardío del reloj maestro.
#
# Requiere ffmpeg en el PATH (el GPL de desarrollo vale: aquí se GENERA por
# subproceso, no se enlaza nada).
#   powershell -File tests\prores-test\generar-media.ps1
# =============================================================================
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot

# Perfiles de prores_ks: 0=Proxy(apco) 1=LT(apcs) 2=422(apcn) 3=422HQ(apch)
#                        4=4444(ap4h)  5=4444XQ(ap4x)
function ProRes {
    param(
        [string]$Name, [string]$Size, [int]$Rate, [double]$Dur,
        [int]$Profile = 2, [string]$Audio = 'none', [switch]$Noise, [switch]$Alpha
    )
    $out = Join-Path $dir $Name
    $a = @('-hide_banner', '-loglevel', 'error', '-y')
    if ($Alpha) {
        $a += @(
            '-f', 'lavfi', '-i', "testsrc2=size=${Size}:rate=${Rate}:duration=${Dur}",
            '-f', 'lavfi', '-i', "gradients=size=${Size}:rate=${Rate}:duration=${Dur}",
            '-filter_complex', '[0][1]alphamerge[v]', '-map', '[v]')
    } else {
        $a += @('-f', 'lavfi', '-i', "testsrc2=size=${Size}:rate=${Rate}:duration=${Dur}")
    }
    switch ($Audio) {
        'aac'    { $a += @('-f', 'lavfi', '-i', "sine=frequency=440:duration=${Dur}", '-c:a', 'aac', '-b:a', '128k') }
        'pcm'    { $a += @('-f', 'lavfi', '-i', "sine=frequency=330:sample_rate=48000:duration=${Dur}", '-c:a', 'pcm_s16le') }
        'pcmbig' { $a += @('-f', 'lavfi', '-i', "sine=frequency=220:sample_rate=96000:duration=${Dur}", '-c:a', 'pcm_s16le', '-ar', '96000') }
        'none'   { $a += @('-an') }
    }
    if ($Noise -and -not $Alpha) { $a += @('-vf', 'noise=alls=90:allf=t+u') }
    $a += @('-c:v', 'prores_ks', '-profile:v', "$Profile")
    if ($Alpha) { $a += @('-pix_fmt', 'yuva444p10le') }
    $a += @('-colorspace', 'bt709', '-color_range', 'tv', $out)
    & ffmpeg @a
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg falló generando $Name" }
    $mb = [math]::Round((Get-Item $out).Length / 1MB, 1)
    Write-Host "  $Name  ($mb MB)"
}

function H264 {
    param([string]$Name, [string]$Size, [int]$Rate, [double]$Dur, [string]$Audio = 'aac')
    $out = Join-Path $dir $Name
    $a = @('-hide_banner', '-loglevel', 'error', '-y',
           '-f', 'lavfi', '-i', "testsrc2=size=${Size}:rate=${Rate}:duration=${Dur}")
    if ($Audio -eq 'aac') {
        $a += @('-f', 'lavfi', '-i', "sine=frequency=550:duration=${Dur}", '-c:a', 'aac', '-b:a', '128k')
    } else { $a += @('-an') }
    $a += @('-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-preset', 'veryfast', $out)
    & ffmpeg @a
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg falló generando $Name" }
    Write-Host "  $Name"
}

Write-Host "Generando media ProRes de tortura en $dir ..."

# 1080p30 422 HQ con audio — el cue base, con espejo de audio "normal"
ProRes 'pr-1080p30-422hq-audio.mov'  '1920x1080' 30 5.0 3 'aac'
# 1080p30 422 SIN audio — el vídeo corre con su Stopwatch (sin reloj maestro)
ProRes 'pr-1080p30-422-noaudio.mov'  '1920x1080' 30 4.0 2 'none'
# 1080p60 422 HQ con audio — cadencia alta
ProRes 'pr-1080p60-422hq-audio.mov'  '1920x1080' 60 3.0 3 'aac'
# 1080p30 4444 CON ALFA — 12 bits y CUATRO planos: reconstruye la sesión del pool
ProRes 'pr-1080p30-4444-alfa.mov'    '1920x1080' 30 4.0 4 'none' -Alpha
# 720p30 Proxy con pcm 96 kHz — espejo de audio GRANDE, arranque tardío
ProRes 'pr-720p30-proxy-bigpcm.mov'  '1280x720'  30 6.0 0 'pcmbig'
# 4K30 422 con audio — cambio de resolución + subida pesada (31,6 MB/frame)
ProRes 'pr-4k30-422-audio.mov'       '3840x2160' 30 4.0 2 'aac'
# 1080p30 422 HQ de RUIDO — bitrate REAL (~232 Mbit/s), el caso honesto
ProRes 'pr-1080p30-422hq-ruido.mov'  '1920x1080' 30 4.0 3 'aac' -Noise

Write-Host "Generando media H.264 para el show mixto ..."
H264 'h264-1080p30-audio.mp4' '1920x1080' 30 4.0 'aac'

Write-Host ""
Write-Host "Listo. Para el show mixto hace falta ADEMAS la media HAP:"
Write-Host "  powershell -File tests\hap-test\generar-tortura.ps1"
