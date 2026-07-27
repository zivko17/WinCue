# =============================================================================
# publish.ps1 — Genera el WinCue.exe distribuible
# =============================================================================
# Single-file autocontenido (no requiere .NET instalado en la máquina destino).
# Copia libmpv-2.dll junto al exe (mpv no puede ir embebido: se carga por P/Invoke).
# Copia también las DLL LGPL de FFmpeg (ffmpeg-lgpl\bin) que enlaza EN PROCESO el
# motor ProRes propio, y VERIFICA que son LGPL antes de dar el paquete por bueno:
# WinCue se distribuye cerrado y una avcodec GPL colada en un instalador no es un
# bug de software, es un problema legal. El smoke falla si el veredicto no es LGPL.
#
# El ffmpeg.exe/ffprobe del PATH sigue siendo cosa del usuario: se invocan por
# SUBPROCESO (conversión a HAP, sondeos, formas de onda) y eso no contamina.
#
# Uso:  .\publish.ps1        → carpeta .\publish\ con WinCue.App.exe + nativas
#       .\publish.ps1 -SinVerificarLicencias   → salta el guardián (NO publicar así)
# =============================================================================
param(
    [switch]$SinVerificarLicencias
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'publish'

dotnet publish (Join-Path $root 'src/WinCue.App') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $out

# El settings.json del dev NO se distribuye (configuración personal; la app
# regenera los defaults en el primer arranque)
Remove-Item (Join-Path $out 'settings.json') -ErrorAction SilentlyContinue

# El README de la beta y la EULA de evaluación acompañan al paquete
$readme = Join-Path $root 'LEEME-BETA.md'
if (Test-Path $readme) { Copy-Item $readme $out -Force }
$eula = Join-Path $root 'docs\legal\EULA-EVALUACION.md'
if (Test-Path $eula) { Copy-Item $eula $out -Force }

# libmpv junto al exe (desde la carpeta del proyecto Python hermano)
$mpv = Join-Path $root '..\WinCue\libmpv-2.dll'
if (Test-Path $mpv) {
    Copy-Item $mpv $out -Force
    Write-Host "libmpv-2.dll copiada junto al exe." -ForegroundColor Green
} else {
    Write-Warning "libmpv-2.dll no encontrada en ..\WinCue — copiala a mano junto al exe."
}

# ── Cumplimiento de licencias (auditoría 05) ────────────────────────────
# THIRD-PARTY-NOTICES.md + licenses/ SIEMPRE acompañan al ejecutable.
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $out -Force
Write-Host "THIRD-PARTY-NOTICES.md incluido." -ForegroundColor Green

$licSrc = Join-Path $root 'licenses'
$licDst = Join-Path $out 'licenses'
if (Test-Path $licSrc) {
    Copy-Item $licSrc $licDst -Recurse -Force
}
if (-not (Test-Path (Join-Path $licDst 'LGPL-2.1.txt'))) {
    Write-Warning ("FALTA licenses\LGPL-2.1.txt (texto oficial: " +
        "https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt). " +
        "OBLIGATORIO antes de distribuir con libmpv (LGPL). " +
        "Ver docs\legal\checklist-distribucion.md.")
}

# ── FFmpeg LGPL (motor ProRes propio, ENLACE DINÁMICO EN PROCESO) ───────
# Va en su propia carpeta ffmpeg-lgpl\bin junto al exe: es donde la busca
# FFmpegLoader y —lo importante para la LGPL §6— es una carpeta que el usuario
# puede abrir y sustituir por su propia build. NO se firma ni se sella.
$ffSrc = Join-Path $root 'ffmpeg-lgpl'
$ffDst = Join-Path $out 'ffmpeg-lgpl'
if (Test-Path (Join-Path $ffSrc 'bin')) {
    New-Item -ItemType Directory -Force (Join-Path $ffDst 'bin') | Out-Null
    Copy-Item (Join-Path $ffSrc 'bin\*.dll') (Join-Path $ffDst 'bin') -Force
    # La procedencia (URL + SHA256 de cada DLL) es OBLIGATORIA: la LGPL exige
    # poder ofrecer el fuente EXACTO de la build distribuida.
    foreach ($f in @('ffmpeg-BUILD.txt', 'LICENSE.txt')) {
        $p = Join-Path $ffSrc $f
        if (Test-Path $p) { Copy-Item $p $ffDst -Force }
    }
    $n = (Get-ChildItem (Join-Path $ffDst 'bin') -Filter *.dll).Count
    Write-Host "FFmpeg LGPL: $n DLL copiadas a ffmpeg-lgpl\bin." -ForegroundColor Green
} else {
    Write-Warning ("No hay ffmpeg-lgpl\bin: el paquete saldrá SIN el motor ProRes " +
        "propio (los ProRes irán por mpv). Obtenlas con " +
        "tools\ffmpeg-lgpl\obtener-ffmpeg-lgpl.ps1.")
}

# La build de FFmpeg lleva --enable-version3 → LGPLv3, que se define COMO
# PERMISOS ADICIONALES SOBRE LA GPLv3: hay que entregar los DOS textos.
foreach ($t in @('LGPL-3.0.txt', 'GPL-3.0.txt')) {
    if (-not (Test-Path (Join-Path $licDst $t))) {
        Write-Warning ("FALTA licenses\$t (texto oficial en https://www.gnu.org/licenses/). " +
            "OBLIGATORIO antes de distribuir con FFmpeg (LGPLv3 por --enable-version3; " +
            "la LGPLv3 son permisos adicionales sobre la GPLv3, así que van los dos). " +
            "Ver docs\legal\checklist-distribucion.md.")
    }
}

# ── SMOKE DE LICENCIAS: el guardián que impide publicar una build GPL ────
# Se verifica el CONTENIDO DEL PAQUETE (la carpeta recién copiada), no el árbol
# de desarrollo: es lo que de verdad se le entrega a un cliente.
if ($SinVerificarLicencias) {
    Write-Warning "Verificación de licencias SALTADA (-SinVerificarLicencias). NO distribuyas este paquete."
} elseif (Test-Path (Join-Path $ffDst 'bin')) {
    Write-Host "`nVerificando la licencia de las DLL del PAQUETE..." -ForegroundColor Cyan
    $verificador = Join-Path $root 'tools\verificar-ffmpeg\verificar-ffmpeg.csproj'
    # La carpeta se pasa por la variable de entorno y NO como argumento: `dotnet
    # run` no conoce `--nologo` y reenvía a la aplicación todo lo que no
    # entiende, así que el verificador acababa mirando una carpeta equivocada y
    # el guardián podía dar por bueno un paquete sin haberlo mirado. Con la
    # variable no hay ambigüedad — y FFmpegLoader la trata de forma ESTRICTA:
    # si esa carpeta no tiene avcodec, falla en vez de buscar en otro sitio.
    $env:WINCUE_FFMPEG_DIR = Join-Path $ffDst 'bin'
    try { & dotnet run --project $verificador -c Release } finally { $env:WINCUE_FFMPEG_DIR = $null }
    if ($LASTEXITCODE -ne 0) {
        Remove-Item (Join-Path $ffDst 'bin') -Recurse -Force
        throw ("LICENCIA RECHAZADA: las DLL de FFmpeg del paquete no son LGPL. " +
               "Se han borrado del paquete. NO se puede distribuir WinCue cerrado con ellas. " +
               "Consigue una build 'lgpl-shared' con tools\ffmpeg-lgpl\obtener-ffmpeg-lgpl.ps1.")
    }
    Write-Host "Licencia de FFmpeg verificada: LGPL." -ForegroundColor Green
}

Write-Host "`nPublicado en: $out" -ForegroundColor Green
Get-ChildItem $out | Format-Table Name, @{L='MB';E={[math]::Round($_.Length/1MB,1)}}
