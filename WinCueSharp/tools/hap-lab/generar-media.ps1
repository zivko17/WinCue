# Genera la media de benchmark del laboratorio HAP en tools/hap-lab/media/
# (no versionada). Los clips pequeños de test los genera la propia suite
# (MediaFixture) si no existen. Requiere ffmpeg en el PATH.
#   powershell -File generar-media.ps1
$dir = Join-Path $PSScriptRoot "media"
New-Item -ItemType Directory -Force $dir | Out-Null

function Gen($nombre, $ffargs) {
    $salida = Join-Path $dir $nombre
    if (Test-Path $salida) { Write-Host "ya existe: $nombre"; return }
    Write-Host "generando $nombre ..."
    $proc = Start-Process ffmpeg -ArgumentList "-hide_banner -loglevel error -y $ffargs `"$salida`"" -NoNewWindow -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "ffmpeg falló con $nombre" }
}

# 1080p30, 10 s — las tres variantes
Gen "bench-1080-hap1.mov" "-f lavfi -i testsrc2=size=1920x1080:rate=30:duration=10 -c:v hap -format hap"
Gen "bench-1080-hap5.mov" "-f lavfi -i testsrc2=size=1920x1080:rate=30:duration=10 -c:v hap -format hap_alpha"
Gen "bench-1080-hapy.mov" "-f lavfi -i testsrc2=size=1920x1080:rate=30:duration=10 -c:v hap -format hap_q"

# 1080p30 chunked (4 chunks: lo que usaría un encoder orientado a multihilo)
Gen "bench-1080-chunked.mov" "-f lavfi -i testsrc2=size=1920x1080:rate=30:duration=10 -c:v hap -format hap -chunks 4"

# 4K30, 10 s — las tres variantes
Gen "bench-2160-hap1.mov" "-f lavfi -i testsrc2=size=3840x2160:rate=30:duration=10 -c:v hap -format hap"
Gen "bench-2160-hap5.mov" "-f lavfi -i testsrc2=size=3840x2160:rate=30:duration=10 -c:v hap -format hap_alpha"
Gen "bench-2160-hapy.mov" "-f lavfi -i testsrc2=size=3840x2160:rate=30:duration=10 -c:v hap -format hap_q"
Gen "bench-2160-chunked.mov" "-f lavfi -i testsrc2=size=3840x2160:rate=30:duration=10 -c:v hap -format hap -chunks 4"

# Con pista de audio PCM (el demuxer debe enumerarla e ignorarla)
Gen "bench-1080-audio.mov" "-f lavfi -i testsrc2=size=1920x1080:rate=30:duration=10 -f lavfi -i sine=frequency=440:duration=10 -c:v hap -format hap -c:a pcm_s16le -shortest"

# 60 fps, 30 s, chunked (4) — media del PASO 2 (presentación GPU: 1080p60 y 4K60)
Gen "bench-1080-60-hap1.mov" "-f lavfi -i testsrc2=size=1920x1080:rate=60:duration=30 -c:v hap -format hap -chunks 4"
Gen "bench-1080-60-hapy.mov" "-f lavfi -i testsrc2=size=1920x1080:rate=60:duration=30 -c:v hap -format hap_q -chunks 4"
Gen "bench-2160-60-hap1.mov" "-f lavfi -i testsrc2=size=3840x2160:rate=60:duration=30 -c:v hap -format hap -chunks 4"
Gen "bench-2160-60-hapy.mov" "-f lavfi -i testsrc2=size=3840x2160:rate=60:duration=30 -c:v hap -format hap_q -chunks 4"

Write-Host "Media de benchmark generada en $dir"
