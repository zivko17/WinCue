# =============================================================================
#  obtener-libmpv-lgpl.ps1 — descarga la build LGPL de libmpv que distribuye
#  WinCue, verifica su integridad y deja la procedencia.
# =============================================================================
#  POR QUÉ ESTE SCRIPT: WinCue se distribuye CERRADO. La libmpv de shinchiro /
#  del proyecto Python hermano es GPL CONFIRMADO, y la GPL obliga a entregar el
#  fuente del conjunto → con ella no se puede vender WinCue cerrado. La única
#  libmpv admisible es LGPL con ENLACE DINÁMICO (DLL sustituible, LGPL §6).
#
#  Fuente: zhongfly/mpv-winbuild (CI pública de GitHub Actions), variante
#  "mpv-dev-lgpl". Su parche compile-lgpl-libmpv.patch quita --enable-gpl del
#  configure de FFmpeg (mantiene --enable-version3 → LGPLv3), elimina x264,
#  x265, zvbi, dvdnav, dvdread, rubberband, avisynth y davs2, desactiva LZO en
#  libarchive, y compila mpv con -Dgpl=false. Nada de eso lo usa WinCue: solo
#  se pierden ENCODERS y DVD; decodificación, hwdec, render API, keep-open y
#  geometría son núcleo LGPL y siguen intactos (verificado, ver INFORME abajo).
#
#  Verificación DESPUÉS de descargar (dos fuentes independientes):
#      $env:WINCUE_LIBMPV = "<repo>\libmpv-lgpl\libmpv-2.dll"
#      dotnet run --project tools\libmpv-lgpl\verificar-licencia
#  Debe decir: VEREDICTO: LGPL (gpl=false).
#  El mismo verificador lo corre publish.ps1 sobre la DLL YA COPIADA AL PAQUETE.
#
#  Uso:
#     powershell -File tools\libmpv-lgpl\obtener-libmpv-lgpl.ps1
#     powershell -File tools\libmpv-lgpl\obtener-libmpv-lgpl.ps1 -Forzar
#     powershell -File tools\libmpv-lgpl\obtener-libmpv-lgpl.ps1 -Tag 2026-08-01-abc123 -Sha256 "..."
#
#  Deja: <repo>\libmpv-lgpl\libmpv-2.dll     (GITIGNORADO: no se versiona)
#        <repo>\libmpv-lgpl\libmpv-BUILD.txt (procedencia + SHA256: esto SÍ)
#  MpvNative la encuentra subiendo por los directorios padre desde el
#  ejecutable, y publish.ps1 la copia junto al exe.
# =============================================================================
[CmdletBinding()]
param(
    # Release FIJADA a propósito: zhongfly publica una build al día sobre git
    # master de mpv. Perseguir la nightly significaría distribuir cada vez un
    # binario distinto y sin verificar. Se sube de versión a mano, verificando.
    [string]$Tag      = "2026-07-27-48e6c35c0e",
    [string]$Archivo  = "mpv-dev-lgpl-x86_64-20260727-git-48e6c35c0e.7z",
    [string]$Sha256   = "558470F135D061D1409D17333D94E53D8EDE5A4F58B409F71750577B3152D087",
    # Commit del repo de la receta que produjo esa build. La LGPL obliga a poder
    # ofrecer el fuente EXACTO de lo distribuido: sin esto, el enlace de descarga
    # no basta (el repo avanza y el parche puede cambiar).
    [string]$RecetaCommit = "85c1c19692ad369698824fd2994cbfb970751767",
    [switch]$Forzar
)

$ErrorActionPreference = "Stop"

$repo  = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$raiz  = Join-Path $repo "libmpv-lgpl"
$dll   = Join-Path $raiz "libmpv-2.dll"
$marca = Join-Path $raiz "libmpv-BUILD.txt"
$url   = "https://github.com/zhongfly/mpv-winbuild/releases/download/$Tag/$Archivo"

if ((Test-Path $marca) -and (Test-Path $dll) -and -not $Forzar) {
    Write-Host "Ya hay una build en $raiz. Usa -Forzar para re-descargar."
    Get-Content $marca | Write-Host
    exit 0
}

# El paquete viene en .7z: sin 7z no se puede extraer (Expand-Archive solo hace
# zip). Se aborta con instrucción en vez de dejar una carpeta a medias.
$sevenZip = (Get-Command 7z -ErrorAction SilentlyContinue).Source
if (-not $sevenZip) {
    foreach ($c in @("$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe")) {
        if (Test-Path $c) { $sevenZip = $c; break }
    }
}
if (-not $sevenZip) {
    throw ("Hace falta 7-Zip para extraer el paquete .7z y no se ha encontrado. " +
           "Instalalo (winget install 7zip.7zip / scoop install 7zip) y repite.")
}

New-Item -ItemType Directory -Force $raiz | Out-Null
$paquete = Join-Path $raiz $Archivo

Write-Host "Descargando $Archivo ..."
Invoke-WebRequest -Uri $url -OutFile $paquete -UseBasicParsing

# INTEGRIDAD antes que nada: el SHA256 esperado va fijado arriba, así que un
# binario cambiado o una descarga a medias se detectan aquí y no en el cliente.
$sha = (Get-FileHash -Algorithm SHA256 $paquete).Hash
Write-Host "SHA256 del paquete = $sha"
if ($sha -ne $Sha256.ToUpperInvariant()) {
    Remove-Item -Force $paquete
    throw ("SHA256 NO COINCIDE con el fijado en este script.`n" +
           "  esperado : $($Sha256.ToUpperInvariant())`n" +
           "  obtenido : $sha`n" +
           "El paquete se ha borrado. Si has cambiado -Tag/-Archivo a proposito, " +
           "pasa tambien el -Sha256 nuevo tras comprobarlo a mano.")
}

$tmp = Join-Path $raiz "_extract"
if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
& $sevenZip x $paquete "-o$tmp" -y | Out-Null
if ($LASTEXITCODE -ne 0) { throw "7z no pudo extraer $Archivo (codigo $LASTEXITCODE)." }

$origen = Get-ChildItem $tmp -Recurse -Filter "libmpv-2.dll" | Select-Object -First 1
if (-not $origen) { throw "El paquete no contiene libmpv-2.dll." }
Copy-Item $origen.FullName $dll -Force

$hDll = (Get-FileHash -Algorithm SHA256 $dll).Hash
$tam  = (Get-Item $dll).Length

# Los .h y el .dll.a del paquete son para compilar contra libmpv; WinCue usa
# P/Invoke y no los necesita. No se copian: menos superficie y menos peso.

$lineas = @(
    "libmpv LGPL — la libmpv que DISTRIBUYE WinCue"
    "============================================="
    ""
    "Origen        : $url"
    "Release       : $Tag"
    "Descargado    : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')"
    "SHA256 paquete: $sha"
    "SHA256 DLL    : $hDll"
    "Tamano DLL    : $('{0:N0}' -f $tam) bytes"
    ""
    "Receta CI     : https://github.com/zhongfly/mpv-winbuild"
    "Commit receta : $RecetaCommit"
    "Parche LGPL   : compile-lgpl-libmpv.patch (de ese commit)"
    ""
    "LICENCIA: mpv compilado con -Dgpl=false -> LGPLv2.1+. Lleva FFmpeg ENLAZADO"
    "ESTATICAMENTE dentro, compilado SIN --enable-gpl y CON --enable-version3 ->"
    "LGPLv3. Por eso en licenses/ van los tres textos: LGPL-2.1, LGPL-3.0 y"
    "GPL-3.0 (la LGPLv3 son permisos adicionales sobre la GPLv3)."
    ""
    "OBLIGACION LGPL: al distribuir esta DLL hay que poder ofrecer el fuente"
    "EXACTO de ESTA build (repo de la receta en el commit de arriba + el mpv y"
    "el FFmpeg que ese commit fija) y permitir que el usuario la sustituya: va"
    "junto al exe, sin firmar ni sellar, y WINCUE_LIBMPV permite apuntar a otra."
    ""
    "VERIFICACION (la corre tambien publish.ps1 sobre el paquete):"
    "    `$env:WINCUE_LIBMPV = `"<repo>\libmpv-lgpl\libmpv-2.dll`""
    "    dotnet run --project tools\libmpv-lgpl\verificar-licencia"
    "  -> VEREDICTO: LGPL (gpl=false)"
    ""
    "NO se pierde nada que WinCue use: fuera quedan encoders (x264/x265/xvid),"
    "DVD (dvdnav/dvdread), teletexto (zvbi), rubberband y avisynth. Decodificacion"
    "H.264/HEVC/AV1/VP9/HAP, hwdec, render API, keep-open y geometria son nucleo"
    "LGPL. Comprobado con los medios de tests/: paridad de codec, dimensiones y"
    "audio frente a la build GPL."
)
$lineas | Set-Content -Encoding UTF8 $marca

Remove-Item -Recurse -Force $tmp
Remove-Item -Force $paquete

Write-Host ""
Write-Host "Listo: $dll ($('{0:N0}' -f $tam) bytes)"
Write-Host "Procedencia en $marca"
Write-Host ""
Write-Host "Verifica la licencia con:"
Write-Host "  `$env:WINCUE_LIBMPV = `"$dll`""
Write-Host "  dotnet run --project `"$repo\tools\libmpv-lgpl\verificar-licencia`""
