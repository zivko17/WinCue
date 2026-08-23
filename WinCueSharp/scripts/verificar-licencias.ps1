# =============================================================================
# verificar-licencias.ps1 — el guardián que impide publicar una build GPL
# =============================================================================
# Salió de publish.ps1 al aparecer el segundo producto. El motivo es de fondo:
# publish-secuence.ps1 iba a nacer de copiar publish.ps1, y el recorte más
# probable de un copia-pega es justo esta parte, que es la lenta. Un guardián
# que se puede olvidar no es un guardián.
#
# Verifica el CONTENIDO DEL PAQUETE (la carpeta/DLL recién copiada), no el
# árbol de desarrollo: es lo que de verdad se le entrega a un cliente.
#
# Cubre las DOS nativas que WinCue enlaza en proceso: FFmpeg (LGPLv3, carpeta
# ffmpeg-lgpl\bin) y libmpv (LGPLv2.1, un único archivo). NO son simétricas —
# una es una carpeta con varias DLL, la otra un archivo suelto con su propio
# verificador y su propia variable de entorno— así que cada una tiene su
# función y se comprueba con su propio criterio; no se ha "armonizado" nada.
#
# CONTRATO, igual para las dos: si el veredicto no es LGPL, BORRA lo
# correspondiente del paquete y LANZA. El script que llama no tiene que
# comprobar nada; si vuelve, el paquete vale.
# =============================================================================

[CmdletBinding()]
param(
    # Carpeta bin del paquete con las DLL de FFmpeg a verificar.
    [Parameter(Mandatory)][string]$CarpetaFfmpegBin,
    # Raíz de WinCueSharp, para encontrar los verificadores.
    [Parameter(Mandatory)][string]$RaizRepo,
    # Ruta de la libmpv-2.dll DEL PAQUETE. Opcional: si no se pasa (o el
    # archivo no existe), no hay nada que verificar — igual que cuando falta
    # la carpeta de FFmpeg.
    [string]$RutaLibmpv
)

$ErrorActionPreference = 'Stop'

function Test-FFmpegLicense {
    param([string]$CarpetaBin, [string]$Raiz)

    if (-not (Test-Path $CarpetaBin)) {
        Write-Warning ("No hay ${CarpetaBin}: el paquete sale SIN el motor " +
            "ProRes propio. Nada que verificar.")
        return
    }

    Write-Host "`nVerificando la licencia de las DLL de FFmpeg del PAQUETE..." -ForegroundColor Cyan

    $verificador = Join-Path $Raiz 'tools\verificar-ffmpeg\verificar-ffmpeg.csproj'

    # La carpeta va por variable de entorno y NO como argumento: `dotnet run` no
    # conoce `--nologo` y reenvía a la aplicación todo lo que no entiende, así que
    # el verificador acababa mirando una carpeta equivocada y el guardián podía dar
    # por bueno un paquete sin haberlo mirado. Con la variable no hay ambigüedad —
    # y FFmpegLoader la trata de forma ESTRICTA: si esa carpeta no tiene avcodec,
    # falla en vez de buscar en otro sitio.
    $env:WINCUE_FFMPEG_DIR = $CarpetaBin
    try { & dotnet run --project $verificador -c Release } finally { $env:WINCUE_FFMPEG_DIR = $null }

    if ($LASTEXITCODE -ne 0) {
        Remove-Item $CarpetaBin -Recurse -Force
        throw ("LICENCIA RECHAZADA: las DLL de FFmpeg del paquete no son LGPL. " +
               "Se han borrado del paquete. NO se puede distribuir software cerrado " +
               "con ellas. Consigue una build 'lgpl-shared' con " +
               "tools\ffmpeg-lgpl\obtener-ffmpeg-lgpl.ps1.")
    }

    Write-Host "Licencia de FFmpeg verificada: LGPL." -ForegroundColor Green
}

function Test-LibmpvLicense {
    param([string]$RutaDll, [string]$Raiz)

    if ([string]::IsNullOrEmpty($RutaDll) -or -not (Test-Path $RutaDll)) {
        Write-Warning "No hay libmpv-2.dll en el paquete. Nada que verificar."
        return
    }

    Write-Host "`nVerificando la licencia de libmpv del PAQUETE..." -ForegroundColor Cyan

    $verificador = Join-Path $Raiz 'tools\libmpv-lgpl\verificar-licencia\verificar-licencia.csproj'

    # Ruta explícita por variable de entorno, igual que con FFmpeg: sin ella el
    # verificador podría cargar OTRA libmpv del PATH y dar por bueno el paquete
    # sin haber mirado la DLL que se entrega.
    $env:WINCUE_LIBMPV = $RutaDll
    try { & dotnet run --project $verificador -c Release } finally { $env:WINCUE_LIBMPV = $null }

    if ($LASTEXITCODE -ne 0) {
        $carpeta = Split-Path $RutaDll -Parent
        Remove-Item $RutaDll -Force
        Remove-Item (Join-Path $carpeta 'libmpv-BUILD.txt') -Force -ErrorAction SilentlyContinue
        throw ("LICENCIA RECHAZADA: la libmpv del paquete NO es LGPL. Se ha borrado del " +
               "paquete. Con una libmpv GPL no se puede distribuir WinCue cerrado. " +
               "Consigue la build LGPL con tools\libmpv-lgpl\obtener-libmpv-lgpl.ps1.")
    }

    Write-Host "Licencia de libmpv verificada: LGPL." -ForegroundColor Green
}

Test-FFmpegLicense -CarpetaBin $CarpetaFfmpegBin -Raiz $RaizRepo
Test-LibmpvLicense -RutaDll $RutaLibmpv -Raiz $RaizRepo
