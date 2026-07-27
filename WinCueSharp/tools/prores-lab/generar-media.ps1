# =============================================================================
#  generar-media.ps1 — media de benchmark del laboratorio de ProRes.
# =============================================================================
#  Requiere ffmpeg en el PATH (el del sistema; da igual que sea GPL: aquí actúa
#  como HERRAMIENTA por SUBPROCESO, no se enlaza nada). Las DLL que el
#  laboratorio enlaza en proceso son las LGPL de obtener-ffmpeg-lgpl.ps1.
#
#  OJO CON EL TAMAÑO: ProRes no es HAP. Un 4K 4444 son ~1,4 Gbit/s. El set
#  completo ronda los 6-7 GB y NO se versiona (ver .gitignore).
#
#      powershell -File generar-media.ps1
#      powershell -File generar-media.ps1 -Solo 1080     # solo los de 1080p
# =============================================================================
[CmdletBinding()]
param([string]$Solo = "")

$ErrorActionPreference = "Stop"

$dir = Join-Path $PSScriptRoot "media"
New-Item -ItemType Directory -Force $dir | Out-Null

function Gen($nombre, $ffargs) {
    if ($Solo -and $nombre -notlike "*$Solo*") { return }
    $salida = Join-Path $dir $nombre
    if (Test-Path $salida) {
        $mb = [math]::Round((Get-Item $salida).Length / 1MB, 1)
        Write-Host ("ya existe: {0} ({1} MB)" -f $nombre, $mb)
        return
    }
    Write-Host "generando $nombre ..."
    $proc = Start-Process ffmpeg -ArgumentList "-hide_banner -loglevel error -y $ffargs `"$salida`"" `
        -NoNewWindow -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "ffmpeg falló con $nombre" }
    $mb = [math]::Round((Get-Item $salida).Length / 1MB, 1)
    Write-Host ("  -> {0} MB" -f $mb)
}

# Señalización de color: BT.709 rango limitado, lo normal en HD/UHD SDR.
$col709 = "-color_primaries bt709 -color_trc bt709 -colorspace bt709 -color_range tv"

# prores_ks es el encoder que cubre TODOS los perfiles (incluido 4444 con alfa).
#   -profile:v  0=proxy 1=lt 2=422 3=422HQ 4=4444 5=4444XQ
function Fuente($w, $h, $rate, $dur) {
    return "-f lavfi -i testsrc2=size=${w}x${h}:rate=${rate}:duration=${dur}"
}

# 4444 con alfa REAL: la luma de un degradado se usa como canal alfa
function FuenteAlfa($w, $h, $rate, $dur) {
    return "-f lavfi -i testsrc2=size=${w}x${h}:rate=${rate}:duration=${dur} " +
           "-f lavfi -i gradients=size=${w}x${h}:rate=${rate}:duration=${dur} " +
           "-filter_complex [0][1]alphamerge[v] -map [v]"
}

# --- Benchmark de decode (30 fps) -------------------------------------------
# 1080p: 10 s = 300 frames.  4K: 5 s = 150 frames (por tamaño en disco).
Gen "bench-1080-422.mov"    "$(Fuente 1920 1080 30 10) -c:v prores_ks -profile:v 2 $col709"
Gen "bench-1080-422hq.mov"  "$(Fuente 1920 1080 30 10) -c:v prores_ks -profile:v 3 $col709"
Gen "bench-1080-4444.mov"   "$(FuenteAlfa 1920 1080 30 10) -c:v prores_ks -profile:v 4 -pix_fmt yuva444p10le $col709"

Gen "bench-2160-422.mov"    "$(Fuente 3840 2160 30 5) -c:v prores_ks -profile:v 2 $col709"
Gen "bench-2160-422hq.mov"  "$(Fuente 3840 2160 30 5) -c:v prores_ks -profile:v 3 $col709"
Gen "bench-2160-4444.mov"   "$(FuenteAlfa 3840 2160 30 5) -c:v prores_ks -profile:v 4 -pix_fmt yuva444p10le $col709"

# --- Perfiles ligeros (tabla de variantes; no entran en el bench principal) ---
Gen "bench-1080-proxy.mov"  "$(Fuente 1920 1080 30 5) -c:v prores_ks -profile:v 0 $col709"
Gen "bench-1080-lt.mov"     "$(Fuente 1920 1080 30 5) -c:v prores_ks -profile:v 1 $col709"
Gen "bench-1080-4444xq.mov" "$(FuenteAlfa 1920 1080 30 5) -c:v prores_ks -profile:v 5 -pix_fmt yuva444p10le $col709"

# --- Presentación GPU (60 fps, el caso de directo) ---------------------------
Gen "bench-1080-60-422hq.mov" "$(Fuente 1920 1080 60 15) -c:v prores_ks -profile:v 3 $col709"
Gen "bench-2160-60-422hq.mov" "$(Fuente 3840 2160 60 8) -c:v prores_ks -profile:v 3 $col709"
Gen "bench-1080-60-4444.mov"  "$(FuenteAlfa 1920 1080 60 15) -c:v prores_ks -profile:v 4 -pix_fmt yuva444p10le $col709"

# --- Contenido DURO (alta entropía) -----------------------------------------
# testsrc2 es sintético y plano: ProRes lo comprime mucho y su decodificación
# entrópica toca pocos coeficientes. El ruido es el PEOR caso realista (grano
# de película, material de cámara): mismo perfil, mucho más trabajo por frame.
Gen "bench-1080-422hq-ruido.mov" ("-f lavfi -i testsrc2=size=1920x1080:rate=30:duration=10 " +
                                  "-vf noise=alls=90:allf=t+u -c:v prores_ks -profile:v 3 $col709")
Gen "bench-2160-422hq-ruido.mov" ("-f lavfi -i testsrc2=size=3840x2160:rate=30:duration=5 " +
                                  "-vf noise=alls=90:allf=t+u -c:v prores_ks -profile:v 3 $col709")

# --- Con pista de audio (el demuxer debe enumerarla e ignorarla) -------------
Gen "bench-1080-audio.mov" ("$(Fuente 1920 1080 30 5) -f lavfi -i sine=frequency=440:duration=5 " +
                            "-c:v prores_ks -profile:v 2 -c:a pcm_s16le -shortest $col709")

# --- BT.2020 (comprobación del camino de matriz alternativa) -----------------
Gen "bench-1080-2020.mov" ("$(Fuente 1920 1080 30 3) -c:v prores_ks -profile:v 3 " +
                           "-color_primaries bt2020 -color_trc bt2020-10 -colorspace bt2020nc -color_range tv")

$total = (Get-ChildItem $dir -Filter *.mov | Measure-Object -Property Length -Sum).Sum / 1GB
Write-Host ""
Write-Host ("Media generada en {0} ({1:N2} GB)" -f $dir, $total)
