# =============================================================================
# generar-tortura.ps1 — Media HAP de TORTURA para el soak del motor propio
# =============================================================================
# Genera un abanico VARIADO de clips HAP (no versionados: los cubre .gitignore
# con tests/hap-test/*.mov) para martillear el motor WinCue.Video.Hap:
#   · CON y SIN pista de audio (el audio espejo decodifica el .mov a RAM y es
#     el reloj maestro del vídeo → probar arranque tardío del espejo).
#   · Resoluciones distintas (720p / 1080p / 4K) → fuerza cambios de spec en el
#     pool de pipelines HAP (AcquireHapSlot) entre cues encadenados.
#   · fps 30 y 60.
#   · Duraciones CORTAS (3–6 s) → muchas vueltas rápidas en el bucle de estrés.
#   · Una con pcm_s16le a 96 kHz (audio GRANDE) → el espejo tarda más en
#     decodificar a RAM: reproduce el arranque tardío que disparaba el bug de
#     la Dur negativa.
# Requiere ffmpeg en el PATH. Ejecutar desde esta carpeta:
#   powershell -File generar-tortura.ps1
# =============================================================================
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot

function Hap {
    param(
        [string]$Name, [string]$Size, [int]$Rate, [double]$Dur,
        [string]$Format = 'hap', [string]$Audio = 'none', [string]$Pattern = 'testsrc2'
    )
    $out = Join-Path $dir $Name
    $args = @(
        '-hide_banner', '-loglevel', 'error', '-y',
        '-f', 'lavfi', '-i', "${Pattern}=size=${Size}:rate=${Rate}:duration=${Dur}"
    )
    switch ($Audio) {
        'aac'    { $args += @('-f', 'lavfi', '-i', "sine=frequency=440:duration=${Dur}", '-c:a', 'aac', '-b:a', '128k') }
        'pcm'    { $args += @('-f', 'lavfi', '-i', "sine=frequency=330:sample_rate=48000:duration=${Dur}", '-c:a', 'pcm_s16le') }
        'pcmbig' { $args += @('-f', 'lavfi', '-i', "sine=frequency=220:sample_rate=96000:duration=${Dur}", '-c:a', 'pcm_s16le', '-ar', '96000') }
        'none'   { $args += @('-an') }
    }
    $args += @('-c:v', 'hap', '-format', $Format, '-chunks', '4', $out)
    & ffmpeg @args
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg falló generando $Name" }
    Write-Host "  $Name"
}

Write-Host "Generando media HAP de tortura en $dir ..."

# 1080p30 con audio AAC (cue base con espejo de audio "normal")
Hap 'demo-hap-1080p30-audio.mov'   '1920x1080' 30 5.0 'hap'   'aac'
# 1080p30 SIN audio (el vídeo corre con su Stopwatch; sin reloj maestro)
Hap 'demo-hap-1080p30-noaudio.mov' '1920x1080' 30 4.0 'hap'   'none'
# 1080p60 con audio (cadencia alta, 60 fps)
Hap 'demo-hap-1080p60-audio.mov'   '1920x1080' 60 3.0 'hap'   'aac'
# 4K30 con audio (cambio de resolución + subida BC pesada)
Hap 'demo-hap-4k30-audio.mov'      '3840x2160' 30 4.0 'hap'   'aac' 'testsrc'
# 720p30 con pcm_s16le a 96 kHz (audio GRANDE → arranque tardío del espejo)
Hap 'demo-hap-720p30-bigpcm.mov'   '1280x720'  30 6.0 'hap'   'pcmbig'
# HAP Q 1080p30 sin audio (otra ruta de textura: DXT5-YCoCg)
Hap 'demo-hap-q-1080p30.mov'       '1920x1080' 30 5.0 'hap_q' 'none'

Write-Host "Media HAP de tortura generada."
