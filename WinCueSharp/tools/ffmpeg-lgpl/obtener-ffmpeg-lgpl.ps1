# =============================================================================
#  obtener-ffmpeg-lgpl.ps1 — descarga la build LGPL (shared/DLL) de FFmpeg que
#  usa el MOTOR ProRes PROPIO de WinCue.
# =============================================================================
#  POR QUÉ ESTE SCRIPT: WinCue se distribuye CERRADO, así que el único FFmpeg
#  admisible es LGPL con ENLACE DINÁMICO (DLL sustituible por el usuario, LGPL
#  §6). El ffmpeg.exe del PATH de la máquina de desarrollo es GPL: sirve para
#  GENERAR media de prueba por subproceso, pero NUNCA para enlazar en proceso.
#  Por eso FFmpegLoader NO cae al PATH del sistema.
#
#  Fuente: BtbN/FFmpeg-Builds (CI pública y reproducible de GitHub Actions).
#  Variante "lgpl-shared": configure SIN --enable-gpl y SIN --enable-nonfree,
#  con las librerías como DLL. Se verifica después con:
#      dotnet run --project tools\verificar-ffmpeg\verificar-ffmpeg.csproj
#  (el mismo verificador que corre el smoke de publicación).
#
#  Uso:
#     powershell -File tools\ffmpeg-lgpl\obtener-ffmpeg-lgpl.ps1
#     powershell -File tools\ffmpeg-lgpl\obtener-ffmpeg-lgpl.ps1 -Rama 7.1
#     powershell -File tools\ffmpeg-lgpl\obtener-ffmpeg-lgpl.ps1 -Forzar
#
#  Deja: <repo>\ffmpeg-lgpl\bin\*.dll        (GITIGNORADO: no se versiona)
#        <repo>\ffmpeg-lgpl\ffmpeg-BUILD.txt (procedencia + SHA256: esto SÍ)
#  Esa carpeta es la que encuentra FFmpegLoader subiendo por los directorios
#  padre desde el ejecutable, y la que copia publish.ps1 junto al exe.
# =============================================================================
[CmdletBinding()]
param(
    [string]$Rama = "8.1",
    [switch]$Forzar
)

$ErrorActionPreference = "Stop"

$repo    = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$raiz    = Join-Path $repo "ffmpeg-lgpl"
$binDir  = Join-Path $raiz "bin"
$marca   = Join-Path $raiz "ffmpeg-BUILD.txt"
$nombre  = "ffmpeg-n$Rama-latest-win64-lgpl-shared-$Rama.zip"
$url     = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$nombre"

if ((Test-Path $marca) -and (Test-Path $binDir) -and -not $Forzar) {
    Write-Host "Ya hay una build en $raiz. Usa -Forzar para re-descargar."
    Get-Content $marca | Write-Host
    exit 0
}

New-Item -ItemType Directory -Force $raiz | Out-Null
$zip = Join-Path $raiz $nombre

Write-Host "Descargando $nombre ..."
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing

$sha = (Get-FileHash -Algorithm SHA256 $zip).Hash
Write-Host "SHA256 del zip = $sha"

$tmp = Join-Path $raiz "_extract"
if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
Expand-Archive -Path $zip -DestinationPath $tmp -Force

# El zip trae una única carpeta raíz: <nombre-sin-.zip>\{bin,include,lib,...}
$origen = Get-ChildItem $tmp -Directory | Select-Object -First 1
if (-not $origen) { throw "El zip no tiene la carpeta raíz esperada." }

if (Test-Path $binDir) { Remove-Item -Recurse -Force $binDir }
New-Item -ItemType Directory -Force $binDir | Out-Null

# Solo las DLL que WinCue ENLAZA de verdad. El zip trae 7 (avfilter, avdevice,
# avformat y swscale suman ~68 MB) y ninguna de esas cuatro se usa: el demuxer
# es NUESTRO (MovDemuxer), no hay filtros ni captura, y la conversión de color
# la hace el shader, no swscale. Distribuir lo que no se enlaza solo engorda el
# instalador y amplía la superficie nativa expuesta a ficheros ajenos.
# WinCue NO ejecuta el ffmpeg.exe de esta build: para convertir a HAP y sondear
# con ffprobe usa el del PATH, por SUBPROCESO (ahí la licencia es del usuario).
$necesarias = @("avcodec-*.dll", "avutil-*.dll", "swresample-*.dll")
foreach ($patron in $necesarias) {
    Copy-Item (Join-Path $origen.FullName "bin\$patron") $binDir
}

# La LGPL obliga a poder entregar el fuente EXACTO de la build distribuida:
# se guarda también el fichero de licencia que trae el paquete.
$licSrc = Join-Path $origen.FullName "LICENSE.txt"
if (Test-Path $licSrc) { Copy-Item $licSrc (Join-Path $raiz "LICENSE.txt") }

$dlls = Get-ChildItem $binDir -Filter *.dll | Sort-Object Name
$lineas = @(
    "FFmpeg LGPL (shared) — motor ProRes propio de WinCue"
    "===================================================="
    ""
    "Origen      : $url"
    "Rama        : n$Rama"
    "Descargado  : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')"
    "SHA256 zip  : $sha"
    "Receta CI   : https://github.com/BtbN/FFmpeg-Builds (variante 'lgpl-shared')"
    ""
    "OBLIGACIÓN LGPL: al distribuir estas DLL hay que ofrecer el fuente exacto"
    "de ESTA build (tarball + scripts de BtbN/FFmpeg-Builds del mismo commit) y"
    "permitir que el usuario las sustituya (enlace dinámico, sin firmar ni"
    "sellar la carpeta). Verificación de que NO es GPL:"
    "    dotnet run --project tools\verificar-ffmpeg\verificar-ffmpeg.csproj"
    ""
    "DLL incluidas (solo las que WinCue enlaza; avformat/avfilter/avdevice/"
    "swscale del zip original NO se distribuyen porque no se usan):"
)
foreach ($d in $dlls) {
    $h = (Get-FileHash -Algorithm SHA256 $d.FullName).Hash
    $lineas += ("  {0,-24} {1,12:N0} bytes  {2}" -f $d.Name, $d.Length, $h)
}
$lineas | Set-Content -Encoding UTF8 $marca

Remove-Item -Recurse -Force $tmp
Remove-Item -Force $zip

Write-Host ""
Write-Host "Listo: $($dlls.Count) DLL en $binDir"
Write-Host "Procedencia en $marca"
Write-Host ""
Write-Host "Verifica la licencia con:"
Write-Host "  dotnet run --project `"$repo\tools\verificar-ffmpeg\verificar-ffmpeg.csproj`""
