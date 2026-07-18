# =============================================================================
# coverage.ps1 — Cobertura de tests de WinCueSharp
# =============================================================================
# Ejecuta la suite completa con Coverlet (formato Cobertura), fusiona los
# resultados de los 4 proyectos de test con ReportGenerator (herramienta
# local, ver .config/dotnet-tools.json) y deja el informe HTML + resumen
# en artifacts/coverage/.
#
# Uso:  .\scripts\coverage.ps1
# Requisitos: dotnet 8 SDK. Primera vez: `dotnet tool restore`.
# =============================================================================

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot   # raíz de WinCueSharp
Set-Location $root

$results = Join-Path $root 'artifacts/coverage-raw'
$report  = Join-Path $root 'artifacts/coverage'

# Limpiar resultados anteriores (los TestResults acumulan GUIDs)
if (Test-Path $results) { Remove-Item $results -Recurse -Force }
if (Test-Path $report)  { Remove-Item $report  -Recurse -Force }

# 1) Tests + cobertura (Cobertura XML por proyecto de test)
dotnet test WinCueSharp.sln -c Debug --nologo `
    --collect:"XPlat Code Coverage" `
    --results-directory $results
if ($LASTEXITCODE -ne 0) { throw "dotnet test falló ($LASTEXITCODE)" }

# 2) Restaurar la herramienta local si hace falta y generar el informe
dotnet tool restore | Out-Null
dotnet reportgenerator `
    -reports:"$results/**/coverage.cobertura.xml" `
    -targetdir:$report `
    -reporttypes:"Html;TextSummary;MarkdownSummaryGithub" `
    -assemblyfilters:"+WinCue.*;-*.Tests"
if ($LASTEXITCODE -ne 0) { throw "reportgenerator falló ($LASTEXITCODE)" }

# 3) Resumen por consola
Get-Content (Join-Path $report 'Summary.txt')
Write-Host "`nInforme HTML: $report\index.html"
