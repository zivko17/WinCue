# =============================================================================
# verificar-licencias.ps1 — el guardián que impide publicar una build GPL
# =============================================================================
# Salió de publish.ps1 al aparecer el segundo producto. El motivo es de fondo:
# publish-secuence.ps1 iba a nacer de copiar publish.ps1, y el recorte más
# probable de un copia-pega es justo esta parte, que es la lenta. Un guardián
# que se puede olvidar no es un guardián.
#
# Verifica el CONTENIDO DEL PAQUETE (la carpeta recién copiada), no el árbol de
# desarrollo: es lo que de verdad se le entrega a un cliente.
#
# CONTRATO: si el veredicto no es LGPL, BORRA las DLL del paquete y LANZA. El
# script que lo llama no tiene que comprobar nada; si vuelve, el paquete vale.
# =============================================================================

[CmdletBinding()]
param(
    # Carpeta bin del paquete con las DLL de FFmpeg a verificar.
    [Parameter(Mandatory)][string]$CarpetaFfmpegBin,
    # Raíz de WinCueSharp, para encontrar el verificador.
    [Parameter(Mandatory)][string]$RaizRepo
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $CarpetaFfmpegBin)) {
    Write-Warning ("No hay ${CarpetaFfmpegBin}: el paquete sale SIN el motor " +
        "ProRes propio. Nada que verificar.")
    return
}

Write-Host "`nVerificando la licencia de las DLL del PAQUETE..." -ForegroundColor Cyan

$verificador = Join-Path $RaizRepo 'tools\verificar-ffmpeg\verificar-ffmpeg.csproj'

# La carpeta va por variable de entorno y NO como argumento: `dotnet run` no
# conoce `--nologo` y reenvía a la aplicación todo lo que no entiende, así que
# el verificador acababa mirando una carpeta equivocada y el guardián podía dar
# por bueno un paquete sin haberlo mirado. Con la variable no hay ambigüedad —
# y FFmpegLoader la trata de forma ESTRICTA: si esa carpeta no tiene avcodec,
# falla en vez de buscar en otro sitio.
$env:WINCUE_FFMPEG_DIR = $CarpetaFfmpegBin
try { & dotnet run --project $verificador -c Release } finally { $env:WINCUE_FFMPEG_DIR = $null }

if ($LASTEXITCODE -ne 0) {
    Remove-Item $CarpetaFfmpegBin -Recurse -Force
    throw ("LICENCIA RECHAZADA: las DLL de FFmpeg del paquete no son LGPL. " +
           "Se han borrado del paquete. NO se puede distribuir software cerrado " +
           "con ellas. Consigue una build 'lgpl-shared' con " +
           "tools\ffmpeg-lgpl\obtener-ffmpeg-lgpl.ps1.")
}

Write-Host "Licencia de FFmpeg verificada: LGPL." -ForegroundColor Green
