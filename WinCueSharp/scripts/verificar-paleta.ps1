# =============================================================================
# verificar-paleta.ps1 — el guardián de que Theme sigue derivando de Palette
# =============================================================================
# El hito 1 dejó los 94 colores duplicados en dos ficheros y dos proyectos:
# Theme.cs (WPF) los expone como Color, Palette.cs (sin framework) guarda el
# número. La correspondencia entre los dos es por NOMBRE, no por tipo, así que
# el compilador no la vigila: `Hex(Palette.Warn)` donde debía ir
# `Hex(Palette.WarnDeep)` compila, pasa la suite entera y pinta mal en directo.
# Este script es el ojo que falta, comprobado desde fuera y sin necesidad de un
# proyecto de pruebas con WPF (la correspondencia es puramente textual).
# =============================================================================

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$themePath = Join-Path $root 'src\WinCue.App\Theme.cs'
$palettePath = Join-Path $root 'src\WinCue.Presentation\Palette.cs'
$fallos = @()

# 1) Cada `public static readonly Color X = Hex(Palette.Y)` en Theme.cs debe
#    tener X == Y: el nombre del campo de Theme y el de Palette al que apunta.
$themeSrc = Get-Content $themePath -Raw
$asignaciones = [regex]::Matches($themeSrc, 'public static readonly Color (\w+) = Hex\(Palette\.(\w+)\)')
if ($asignaciones.Count -eq 0) {
    $fallos += "No se encontró ninguna asignación 'Color X = Hex(Palette.Y)' en Theme.cs — ¿cambió el patrón?"
}
foreach ($m in $asignaciones) {
    $nombreTheme = $m.Groups[1].Value
    $nombrePalette = $m.Groups[2].Value
    if ($nombreTheme -ne $nombrePalette) {
        $fallos += "Cruce en Theme.cs: Theme.$nombreTheme = Hex(Palette.$nombrePalette) — deberían llamarse igual."
    }
}

# 2) Palette.cs debe seguir teniendo exactamente 94 `public const uint`: la
#    cuenta exacta de colores que tenía Theme antes del traslado.
$paletteSrc = Get-Content $palettePath -Raw
$constantes = [regex]::Matches($paletteSrc, 'public const uint (\w+) =')
if ($constantes.Count -ne 94) {
    $fallos += "Palette.cs tiene $($constantes.Count) 'public const uint', se esperaban 94."
}

# 3) Los 20 alias (valor = otra constante, NO un literal hexadecimal) siguen
#    siendo alias. Si alguno se resolvió a su número, el traslado empezó a
#    derivar otra vez sin que nada lo avise. Ojo: un literal como "0x2ecc71"
#    también hace match con \w+, así que hay que excluir explícitamente los
#    valores que empiezan por "0x" — un identificador de C# nunca puede.
$alias = [regex]::Matches($paletteSrc, 'public const uint \w+ = (?!0x)(\w+);')
$numAlias = $alias.Count
if ($numAlias -ne 20) {
    $fallos += "Palette.cs tiene $numAlias alias (valor = identificador), se esperaban 20."
}

if ($fallos.Count -gt 0) {
    $fallos | ForEach-Object { Write-Host "FALLO: $_" -ForegroundColor Red }
    exit 1
}

Write-Host ("OK: {0} asignaciones Theme<->Palette coinciden, 94 constantes, {1} alias intactos." -f $asignaciones.Count, $numAlias) -ForegroundColor Green
exit 0
