# =============================================================================
# generar-crossfade.ps1 — media de MEDIDA para el glitch del crossfade
# =============================================================================
# Clips de LUMINANCIA CONSTANTE y MUY DISTINTA entre sí: fondo gris plano
# (A oscuro 0x3C = 60, B claro 0xDC = 220) + un cuadrado pequeño en movimiento
# (área y color fijos → la media de luma NO cambia con el tiempo, pero se ve si
# el vídeo avanza o está congelado).
#
# Con dos clips así, un crossfade IDEAL dibuja una RAMPA LIMPIA de luma entre
# las dos mesetas. Si el entrante mezcla NEGRO (superficie sin primer frame),
# la luma se hunde POR DEBAJO de las dos mesetas: eso es el glitch, y es una
# medida objetiva del antes/después.
#
# Requiere ffmpeg en el PATH. Ejecutar desde esta carpeta:
#   powershell -File generar-crossfade.ps1
# =============================================================================
$dir = $PSScriptRoot
$dur = 12

function Clip($name, $bg, $boxColor, $boxExpr, $codec, $fmt, $ext) {
    $vf = "drawbox=$($boxExpr):w=90:h=90:color=$($boxColor)@1:t=fill"
    $out = Join-Path $dir "$name.$ext"
    $args = @(
        "-hide_banner", "-loglevel", "error", "-y",
        "-f", "lavfi", "-i", "color=c=$($bg):s=1920x1080:r=30:d=$dur",
        "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
        "-vf", $vf, "-shortest"
    )
    if ($codec -eq "hap") { $args += @("-c:v", "hap", "-format", $fmt, "-c:a", "pcm_s16le") }
    else                  { $args += @("-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac") }
    $args += $out
    & ffmpeg @args
    Write-Host "  $out"
}

# A OSCURO con cuadrado claro que barre en horizontal · B CLARO con cuadrado
# oscuro que barre en vertical (el cuadrado da MOVIMIENTO sin mover la media)
$exprA = "x='mod(t*300\,1830)':y=495"
$exprB = "x=915:y='mod(t*160\,990)'"

Clip "xf-A-hap"  "0x3C3C3C" "0xC8C8C8" $exprA "hap"  "hap" "mov"
Clip "xf-B-hap"  "0xDCDCDC" "0x141414" $exprB "hap"  "hap" "mov"
Clip "xf-A-h264" "0x3C3C3C" "0xC8C8C8" $exprA "h264" ""    "mp4"
Clip "xf-B-h264" "0xDCDCDC" "0x141414" $exprB "h264" ""    "mp4"

Write-Host "Media de medida del crossfade generada en $dir"
