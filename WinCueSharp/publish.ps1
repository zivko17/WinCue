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

# ── libmpv LGPL junto al exe ────────────────────────────────────────────
# OJO: la fuente es libmpv-lgpl\, NO ..\WinCue\ (el proyecto Python hermano).
# Aquella DLL es GPL CONFIRMADO y con ella no se puede distribuir WinCue
# cerrado: la GPL obliga a entregar el fuente del conjunto. Va junto al exe,
# sin firmar ni sellar, para que el usuario pueda sustituirla (LGPL §6).
$mpvSrc = Join-Path $root 'libmpv-lgpl'
$mpv = Join-Path $mpvSrc 'libmpv-2.dll'
if (Test-Path $mpv) {
    Copy-Item $mpv $out -Force
    # La procedencia acompaña SIEMPRE al binario: la LGPL exige poder ofrecer
    # el fuente exacto de la build distribuida.
    $marca = Join-Path $mpvSrc 'libmpv-BUILD.txt'
    if (Test-Path $marca) { Copy-Item $marca $out -Force }
    else { Write-Warning "FALTA libmpv-lgpl\libmpv-BUILD.txt (procedencia obligatoria por la LGPL)." }
    # Sin adjetivos: aquí solo se ha copiado. Quien dice si es LGPL es el
    # guardián de más abajo, mirando la DLL ya dentro del paquete.
    Write-Host "libmpv-2.dll copiada junto al exe (licencia pendiente de verificar)." -ForegroundColor Green
} else {
    Write-Warning ("No hay libmpv-lgpl\libmpv-2.dll: el paquete saldrá SIN mpv " +
        "(los formatos que no cubre el motor propio no reproducirán). " +
        "Obtenla con tools\libmpv-lgpl\obtener-libmpv-lgpl.ps1. " +
        "NO copies la de ..\WinCue: es GPL y no se puede distribuir.")
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

# ── SMOKE DE LICENCIAS (FFmpeg + libmpv) ────────────────────────────────
# El guardián vive ENTERO en scripts\verificar-licencias.ps1 para que
# publish-secuence.ps1 use EL MISMO y no una copia recortada. Cubre las dos
# nativas: libmpv era el bloqueante histórico (la build de shinchiro / del
# proyecto Python es GPL y colarla en un paquete obligaría a abrir el código
# de WinCue). Se comprueba lo que hay DENTRO del paquete, no el árbol de
# desarrollo.
$mpvOut = Join-Path $out 'libmpv-2.dll'
if ($SinVerificarLicencias) {
    Write-Warning "Verificación de licencias SALTADA (-SinVerificarLicencias). NO distribuyas este paquete."
} else {
    & (Join-Path $root 'scripts\verificar-licencias.ps1') `
        -CarpetaFfmpegBin (Join-Path $ffDst 'bin') `
        -RaizRepo $root `
        -RutaLibmpv $mpvOut
}

Write-Host "`nPublicado en: $out" -ForegroundColor Green
Get-ChildItem $out | Format-Table Name, @{L='MB';E={[math]::Round($_.Length/1MB,1)}}
