# =============================================================================
# generar-show-prueba.ps1 — Medias sintéticas para el show de prueba de fades
# =============================================================================
# Genera con ffmpeg (debe estar en PATH) los vídeos/audios/imágenes que usa
# tests\show-prueba\show-prueba.json. Cada media es visual/sonoramente
# distinguible para VER los crossfades: patrones distintos + tonos distintos.
# Uso:  powershell -File tools\generar-show-prueba.ps1
# =============================================================================

$out = Join-Path $PSScriptRoot "..\tests\show-prueba"
New-Item -ItemType Directory -Force $out | Out-Null

function V($name, $vf, $freq) {
    $dst = Join-Path $out $name
    if (Test-Path $dst) { return }
    ffmpeg -v error -f lavfi -i "testsrc2=duration=10:size=1280x720:rate=30,$vf" `
           -f lavfi -i "sine=frequency=${freq}:duration=10" `
           -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac -shortest -y $dst
    Write-Host "OK  $name"
}

# Vídeos de 10 s — paleta/patrón distinto + tono distinto (440/660/880/220 Hz)
V "video-A-colores.mp4"   "hue=h=0"        440
V "video-B-gris.mp4"      "hue=s=0"        660
V "video-C-invertido.mp4" "negate"         880
V "video-D-azul.mp4"      "hue=h=200:s=3"  220

# Audios de 8 s (tono puro con envolvente para distinguirlos)
foreach ($a in @(@("audio-440.wav", 440), @("audio-880.wav", 880))) {
    $dst = Join-Path $out $a[0]
    if (-not (Test-Path $dst)) {
        ffmpeg -v error -f lavfi -i "sine=frequency=$($a[1]):duration=8" -y $dst
        Write-Host "OK  $($a[0])"
    }
}

# Imágenes fijas
foreach ($i in @(@("imagen-roja.png", "red"), @("imagen-verde.png", "green"))) {
    $dst = Join-Path $out $i[0]
    if (-not (Test-Path $dst)) {
        ffmpeg -v error -f lavfi -i "color=c=$($i[1]):s=1280x720" -frames:v 1 -y $dst
        Write-Host "OK  $($i[0])"
    }
}

Write-Host "`nListo: abre tests\show-prueba\show-prueba.json en WinCue."
