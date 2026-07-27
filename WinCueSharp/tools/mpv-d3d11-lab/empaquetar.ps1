# =============================================================================
# empaquetar.ps1 — Deja el lab listo para llevárselo a otra máquina
# =============================================================================
# Genera una carpeta AUTOCONTENIDA (no hace falta instalar .NET en el equipo
# de destino) con el ejecutable, libmpv-2.dll y un clip de prueba.
#
#   powershell -ExecutionPolicy Bypass -File empaquetar.ps1
#
# Luego se copia la carpeta 'paquete' al equipo a probar y se lanza el .exe.
# =============================================================================

$ErrorActionPreference = 'Stop'
$lab = $PSScriptRoot
$repo = Split-Path (Split-Path $lab -Parent) -Parent
$salida = Join-Path $lab 'paquete'

Write-Host "Publicando el lab (autocontenido, win-x64)..." -ForegroundColor Cyan
if (Test-Path $salida) { Remove-Item $salida -Recurse -Force }

dotnet publish (Join-Path $lab 'MpvD3D11Lab.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -o $salida
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló ($LASTEXITCODE)" }

# --- libmpv ------------------------------------------------------------------
$libmpv = Join-Path $repo 'publish\libmpv-2.dll'
if (-not (Test-Path $libmpv)) { throw "No encuentro libmpv-2.dll en $libmpv" }
Copy-Item $libmpv (Join-Path $salida 'libmpv-2.dll') -Force
Write-Host "  libmpv-2.dll incluida" -ForegroundColor Green

# --- clip de prueba ----------------------------------------------------------
$media = Join-Path $salida 'media'
New-Item -ItemType Directory -Path $media -Force | Out-Null
$clip = Join-Path $repo 'tests\hap-test\demo-h264-1080p30-noaudio.mp4'
if (-not (Test-Path $clip)) { throw "No encuentro el clip de prueba en $clip" }
Copy-Item $clip (Join-Path $media 'prueba.mp4') -Force
Write-Host "  clip de prueba incluido" -ForegroundColor Green

# --- instrucciones al lado ---------------------------------------------------
@'
LAB mpv -> D3D11 — cómo pasarlo
===============================

1) Doble clic en mpv-d3d11-lab.exe (o desde una consola, para ver la salida
   con calma). Prueba TODOS los adaptadores del equipo, uno tras otro.

2) Guarda la salida completa de la consola. Desde PowerShell:

       .\mpv-d3d11-lab.exe > resultado.txt 2>&1

3) Si en algún adaptador falla la prueba T3 ("OpenGL y D3D11 en el mismo
   adaptador"), NO es un fallo del driver: es que Windows le está dando a
   este proceso el OpenGL de otra GPU. Para probar esa GPU de verdad:

       Configuración -> Sistema -> Pantalla -> Gráficos
       Agregar aplicación de escritorio -> mpv-d3d11-lab.exe
       Opciones -> "Ahorro de energía"   (gráficos integrados)
                   "Alto rendimiento"    (gráfica dedicada)

   Y volver a lanzar:

       .\mpv-d3d11-lab.exe --adapter=low    (integrada)
       .\mpv-d3d11-lab.exe --adapter=high   (dedicada)

4) Devuelve resultado.txt y la carpeta "out" (lleva volcados .bmp de lo que
   ha pintado cada adaptador).

Lo que se está averiguando: si el driver de CADA GPU soporta
WGL_NV_DX_interop2. Si no lo soporta, el motor de vídeo nuevo no puede
funcionar en esa máquina y hace falta un camino alternativo.
'@ | Set-Content -Path (Join-Path $salida 'LEEME.txt') -Encoding UTF8

$mb = [math]::Round(((Get-ChildItem $salida -Recurse | Measure-Object Length -Sum).Sum / 1MB), 1)

# --- ZIP, que es lo que se copia de verdad -----------------------------------
$zip = Join-Path $lab 'mpv-d3d11-lab.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $salida '*') -DestinationPath $zip -CompressionLevel Optimal
$zipMb = [math]::Round(((Get-Item $zip).Length / 1MB), 1)

Write-Host ""
Write-Host "Paquete : $salida  ($mb MB)" -ForegroundColor Green
Write-Host "ZIP     : $zip  ($zipMb MB)   <- copia este" -ForegroundColor Green
Write-Host "Descomprimir en el equipo a probar y lanzar mpv-d3d11-lab.exe" -ForegroundColor Green
