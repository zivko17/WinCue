# =============================================================================
#  obtener-ffmpeg-lgpl.ps1 — descarga la build LGPL (shared/DLL) de FFmpeg
#  que usa el laboratorio de ProRes.
# =============================================================================
#  POR QUÉ ESTE SCRIPT: WinCue se distribuye CERRADO, así que el único FFmpeg
#  admisible es LGPL con ENLACE DINÁMICO (DLL sustituible por el usuario).
#  El ffmpeg.exe del PATH de esta máquina es GPL (--enable-gpl): sirve para
#  GENERAR media de prueba por subproceso, pero NO para enlazar en proceso.
#
#  Fuente: BtbN/FFmpeg-Builds (CI pública y reproducible de GitHub Actions).
#  Variante "lgpl-shared": configure SIN --enable-gpl y SIN --enable-nonfree,
#  con las librerías como DLL. Verificado después por `--licencia`.
#
#  Uso:
#     powershell -File obtener-ffmpeg-lgpl.ps1            # rama por defecto (8.1)
#     powershell -File obtener-ffmpeg-lgpl.ps1 -Rama 7.1  # otra rama
#     powershell -File obtener-ffmpeg-lgpl.ps1 -Forzar    # rebajar/re-descargar
#
#  Deja: ffmpeg-lgpl\bin\*.dll  (GITIGNORADO: no se versiona el binario)
#        ffmpeg-lgpl\ffmpeg-BUILD.txt  (procedencia + SHA256: esto SÍ importa)
# =============================================================================
[CmdletBinding()]
param(
    [string]$Rama = "8.1",
    [switch]$Forzar
)

$ErrorActionPreference = "Stop"

$raiz    = Join-Path $PSScriptRoot "ffmpeg-lgpl"
$binDir  = Join-Path $raiz "bin"
$marca   = Join-Path $raiz "ffmpeg-BUILD.txt"
$nombre  = "ffmpeg-n$Rama-latest-win64-lgpl-shared-$Rama.zip"
$url     = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$nombre"

if ((Test-Path $marca) -and -not $Forzar) {
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

# Solo las DLL: el lab NO ejecuta ffmpeg.exe de esta build (para generar media
# ya está el del PATH); enlazamos contra las librerías compartidas.
Copy-Item (Join-Path $origen.FullName "bin\*.dll") $binDir

# La LGPL obliga a poder entregar el fuente EXACTO de la build distribuida:
# se guardan también los ficheros de licencia que trae el paquete.
$licSrc = Join-Path $origen.FullName "LICENSE.txt"
if (Test-Path $licSrc) { Copy-Item $licSrc (Join-Path $raiz "LICENSE.txt") }

$dlls = Get-ChildItem $binDir -Filter *.dll | Sort-Object Name
$lineas = @(
    "FFmpeg LGPL (shared) para el laboratorio de ProRes de WinCue"
    "============================================================"
    ""
    "Origen      : $url"
    "Rama        : n$Rama"
    "Descargado  : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')"
    "SHA256 zip  : $sha"
    "Receta CI   : https://github.com/BtbN/FFmpeg-Builds (variante 'lgpl-shared')"
    ""
    "OBLIGACIÓN LGPL: al distribuir estas DLL hay que ofrecer el fuente exacto"
    "de ESTA build (tarball + scripts de BtbN/FFmpeg-Builds del mismo commit) y"
    "permitir que el usuario las sustituya (enlace dinámico, sin firmar/sellar"
    "la carpeta). Verificación de que NO es GPL:"
    "    dotnet run --project ProResLab.csproj -c Release -- --licencia"
    ""
    "DLL incluidas:"
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
Write-Host "  dotnet run --project `"$PSScriptRoot\ProResLab.csproj`" -c Release -- --licencia"
