# Genera los clips de prueba HAP (no versionados: ~80 MB) y el control H.264.
# Requiere ffmpeg en el PATH. Ejecutar desde esta carpeta:
#   powershell -File generar-media.ps1
$dir = $PSScriptRoot
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=1920x1080:rate=30:duration=10" -c:v hap -format hap "$dir\prueba-hap.mov"
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=1920x1080:rate=30:duration=10" -c:v hap -format hap_q "$dir\prueba-hap-q.mov"
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=1920x1080:rate=30:duration=10" -c:v libx264 -pix_fmt yuv420p "$dir\control-h264.mp4"
Write-Host "Media de prueba HAP generada en $dir"
