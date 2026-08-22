# Hito 0 de Secuence — informe del banco de pruebas WinUI 3

*Escrito 2026-08-22 sobre el commit `dac049f1`. Ver el diseño en
`docs/superpowers/specs/2026-08-22-secuence-winui3-design.md`.*

Máquina: Windows 11 Pro 10.0.26200, SDK de .NET `8.0.423`, sin Visual Studio
completo instalado (solo **Visual Studio 2022 Build Tools**, sin el
componente de empaquetado UWP/Appx — esto resulta relevante más abajo).

## Pregunta 2 — ¿cuánto ruido de analizadores mete WinUI 3?

Versión del Windows App SDK que compiló (con matices, ver más abajo):
**1.6.250602001** — es la versión de la caché local indicada en el encargo.
SDK de .NET usado: **8.0.423** (`dotnet --version`).

### Hallazgo previo: `dotnet build` normal (sin `TreatWarningsAsErrors`) no termina en esta máquina

Antes de poder medir analizadores hubo que resolver esto, porque afecta
también al Paso 2 (build + arranque). Con el `.csproj` **literal** del
encargo (1.6.\*, sin tocar nada):

```
dotnet build tools/winui3-lab/WinUi3Lab.csproj -c Release
```

falla, no por versión de paquete, sino por una pieza de MSBuild que **no
existe en esta máquina**:

```
...MrtCore.PriGen.targets(914,5): error MSB4062: La tarea
"Microsoft.Build.Packaging.Pri.Tasks.ExpandPriContent" no se pudo cargar
desde el ensamblado
C:\Program Files\dotnet\sdk\8.0.423\Microsoft\VisualStudio\v17.0\AppxPackage\Microsoft.Build.Packaging.Pri.Tasks.dll.
Could not load file or assembly [...] El sistema no puede encontrar la ruta
especificada.
```

Verificado con `find`: ese `.dll` no existe en ningún sitio del disco, ni
bajo el SDK de .NET ni bajo Visual Studio Build Tools. `Microsoft.Build.Packaging.Pri.Tasks.dll`
y `Microsoft.Build.AppxPackage.dll` los aporta el componente de **Visual
Studio para empaquetado UWP/Appx** (el que instala `MakePri.exe`,
`MakeAppx.exe` y sus tareas de MSBuild), y esta máquina solo tiene VS 2022
**Build Tools** sin ese componente — no es parte del SDK de .NET puro.

Siguiendo la instrucción del encargo para el caso "no compila", probé la
contingencia explícita — bajar a **1.5.\*** — para descartar que fuera un
problema de versión:

```
dotnet build tools/winui3-lab/WinUi3Lab.csproj -c Release   # con 1.5.*
```

Resultado: **falla por un motivo distinto y anterior**, `NETSDK1083` — el
paquete 1.5.x fija `RuntimeIdentifiers` con monikers antiguos
(`win10-x64`, `win10-arm`, etc.) que el SDK 8.0.423 ya no reconoce. Es decir,
**1.5.x está peor, no mejor**, en este SDK. Esto confirma que **1.6.\* es la
elección correcta** del encargo y que el problema de `Pri.Tasks.dll` no es
un problema de versión de paquete: es una **pieza de Visual Studio ausente
en esta máquina**, independiente de qué versión de WindowsAppSDK se use.

**No instalé nada para arreglarlo** — instalar un componente de Visual
Studio es una modificación del sistema que no me corresponde decidir sola.
Dejo esto marcado como hallazgo de entorno para hablarlo (ver Veredicto y el
epígrafe PENDIENTE).

### Cómo se obtuvo igualmente la medición de analizadores

Buena noticia: el paso donde MSBuild revienta (`MrtCore.PriGen`) ocurre
**después** de `CoreCompile` — la fase que ejecuta `csc` y los analizadores
Roslyn (CS/CA/IDE). Con `-p:TreatWarningsAsErrors=true`, si hay **algún**
diagnóstico de analizador en toda la superficie compilada (código propio +
XAML generado + código incluido por el SDK), `CoreCompile` falla ahí mismo
y MSBuild **aborta antes de llegar** al target roto de empaquetado. Es decir:
la medición de analizadores del Paso 3 **no depende** de tener el
componente de Visual Studio que falta. Lo comprobé con limpieza de
`bin/`/`obj/` antes de medir, y confirmando en `obj/.../win-x64/` que el
XAML sí se había generado (`App.g.cs`, `MainWindow.g.cs`, `XamlTypeInfo.g.cs`)
antes del corte.

Comando ejecutado, literal del encargo, sobre el `.csproj` sin modificar
(1.6.\*):

```
dotnet build tools/winui3-lab/WinUi3Lab.csproj -c Release -p:TreatWarningsAsErrors=true 2>&1 | grep -E "error (CA|IDE|CS)" | sed -E 's/.*(error [A-Z]+[0-9]+).*/\1/' | sort | uniq -c | sort -rn
```

Salida:

```
      2 error CA5392
```

**Matiz necesario, no maquillado:** `dotnet build` imprime cada error **dos
veces** en consola (una vez in situ, otra vez en el resumen final "ERROR al
compilar"), así que el `grep` cuenta el mismo diagnóstico dos veces. El
propio resumen de MSBuild lo deja claro:

```
C:\Users\andre\.nuget\packages\microsoft.windowsappsdk\1.6.250602001\include\UndockedRegFreeWinRT-AutoInitializer.cs(18,36):
error CA5392: El método WindowsAppRuntime_EnsureIsLoaded no ha usado el
atributo DefaultDllImportSearchPaths para P/Invoke.
    0 Advertencia(s)
    1 Errores
```

**El recuento real es 1 diagnóstico, de 1 regla, no 2.**

| Regla | Cuenta | ¿Código generado? | Decisión |
|---|---|---|---|
| CA5392 | 1 | sí — archivo `include/UndockedRegFreeWinRT-AutoInitializer.cs` que trae el propio paquete NuGet `Microsoft.WindowsAppSDK` (no es `.g.cs` de XAML, es código fuente de terceros que el `.props` del paquete inyecta como `<Compile Include>` en cualquier proyecto que lo referencia) | relajar por proyecto, con motivo: "diagnóstico en código fuente del SDK, no nuestro; no se puede arreglar sin tocar el paquete" |

**Total: 1 error de 1 regla distinta.**

Sobre nuestro propio código (`App.xaml.cs`, `MainWindow.xaml.cs`) y sobre el
XAML generado (`App.g.cs`, `MainWindow.g.cs`, `XamlTypeInfo.g.cs`,
`App.g.i.cs`, `MainWindow.g.i.cs`) **no saltó ningún diagnóstico** con
`TreatWarningsAsErrors=true`. `csc` procesa toda la unidad de compilación en
una sola pasada y reporta todos los diagnósticos que encuentra antes de
fallar, así que esto no es "no llegamos a mirar" — es que no hay nada que
mirar ahí.

**Veredicto:** el ruido de analizadores de WinUI 3 en un proyecto trivial
como este es **mínimo**: una sola regla, un solo diagnóstico, y viene de
código fuente que trae el propio SDK de Microsoft, no de código escrito a
mano ni de un lab más grande. Se relaja por proyecto (`NoWarn` o
`.editorconfig` con motivo documentado apuntando a este informe) y **no
hace falta tocar el listón del repo**. Dicho esto, esta muestra es de un
proyecto de una ventana con un botón — no dice nada sobre lo que aparecerá
al crecer el código de Secuence de verdad; conviene repetir esta medición
cuando haya más superficie (tareas 2–4 y en adelante).

**Aparte, y más urgente que el recuento de analizadores:** el hallazgo del
Paso 2 (la máquina no tiene el componente de Visual Studio para
empaquetado UWP/Appx) sí es un obstáculo real para "compila y arranca" y
hay que resolverlo antes de dar por bueno el hito 0 completo — ver el
epígrafe PENDIENTE.

## Pregunta 1 — forma del paquete

(tarea 2)

## Pregunta 3 — mpv en un HWND hijo

(tarea 3)

## Notas del Paso 2 (build + arranque) — para que la tarea 2/3 no repita la investigación

- El `.csproj` restaura sin problema (caché local: `microsoft.windowsappsdk`
  1.6.250602001, `microsoft.windows.sdk.buildtools` 10.0.22621.756).
- `dotnet build ... -c Release` (sin `TreatWarningsAsErrors`) **no termina
  en verde** en esta máquina: falla en el target
  `MrtCore.PriGen.targets` con `MSB4062` al no encontrar
  `Microsoft.Build.Packaging.Pri.Tasks.dll`. Causa: falta el componente de
  Visual Studio de empaquetado UWP/Appx (no viene con VS Build Tools por
  defecto ni con el SDK de .NET solo).
- Por eso `dotnet run --project tools/winui3-lab/WinUi3Lab.csproj -c Release`
  **tampoco arranca**: `dotnet run` se niega a ejecutar porque el build
  previo falla, y lo dice explícitamente ("No se pudo llevar a cabo la
  compilación. Corrija los errores de compilación y vuelva a ejecutar el
  proyecto."). El proceso **no llega a arrancar**, ni siquiera de forma
  observable sin pantalla: no hay ningún PID vivo tras el intento.
- Como investigación aparte (no forma parte del `.csproj` que se ha
  *commiteado*, que sigue siendo el literal del encargo), probé forzar el
  build con `AppxGeneratePriEnabled=false` para ver hasta dónde llegaba: la
  compilación (`csc` + XAML) sí terminó y produjo un
  `WinUi3Lab.exe` autocontenido con todas las DLL de WindowsAppSDK, pero el
  build seguía sin terminar en verde (fallaba después en otra tarea también
  ausente, `Microsoft.Build.AppxPackage.RemovePayloadDuplicates`), y al
  lanzar ese `.exe` **a mano, directamente**, el proceso **terminó con
  código de salida `-1073741189`** sin quedar vivo ni escribir nada en
  stdout/stderr. Esto descarta que el paso de empaquetado que falta sea
  cosmético: sin él, el ejecutable no arranca de verdad. No dejo ese
  workaround en el `.csproj` final porque no resuelve el problema, solo lo
  esconde peor.
- **No sé si esto también afecta al runner de CI** (GitHub Actions
  `windows-latest` normalmente trae Visual Studio Enterprise completo con
  el workload de UWP, así que es posible que allí sí compile). No lo he
  probado — está fuera del alcance de esta tarea y de esta máquina.

## Autorrevisión

- El proyecto **no** se añadió a `WinCueSharp.sln` — verificado con
  `grep -i tools WinCueSharp.sln`, sin resultados.
- El informe distingue explícitamente lo medido (recuento de analizadores,
  vía `CoreCompile` que sí completa) de lo no verificado (ventana visible en
  pantalla, comportamiento del paquete completo en runtime) y de lo
  bloqueado (build "en verde" completo, por el componente de VS ausente).
- El commit no incluye `bin/` ni `obj/` de `tools/winui3-lab/` — cubiertos
  por las reglas globales `bin/` y `obj/` de `.gitignore`; confirmado que el
  árbol de trabajo estaba limpio de esas carpetas antes del `git add`.
- El `.csproj`, `App.xaml`, `App.xaml.cs`, `MainWindow.xaml` y
  `MainWindow.xaml.cs` commiteados son **literales** al encargo — no llevan
  ninguno de los ajustes de la investigación (`AppxGeneratePriEnabled`,
  versión 1.5.x), que solo se probaron para diagnosticar y no dejaron
  rastro en el árbol final.

## Dudas para Andrés

1. La máquina de desarrollo no tiene el componente de Visual Studio para
   empaquetado UWP/Appx (`Microsoft.Build.Packaging.Pri.Tasks.dll` /
   `Microsoft.Build.AppxPackage.dll`, bajo
   `...\Microsoft\VisualStudio\v17.0\AppxPackage\`). Sin él, ningún proyecto
   WinUI 3 no empaquetado compila en verde aquí, independientemente de la
   versión de WindowsAppSDK. ¿Instalamos ese componente (vía el instalador
   de Visual Studio Build Tools, workload "Universal Windows Platform
   build tools") antes de seguir con las tareas 2–4, que también necesitan
   compilar y arrancar este mismo tipo de proyecto? Es una decisión de
   entorno, no de código, y prefiero preguntarlo a instalarlo por mi
   cuenta sin permiso.
2. Si el runner de CI **sí** trae ese componente (lo normal en
   `windows-latest` de GitHub Actions), este hallazgo afectaría solo al
   desarrollo local, no al hito de "¿compila en CI?" — pero no lo he podido
   comprobar desde aquí.

## PENDIENTE — verificación en pantalla (Andrés)

- [ ] Confirmar que, una vez resuelto el punto 1 de arriba (componente de
      Visual Studio para Appx), `dotnet build tools/winui3-lab/WinUi3Lab.csproj -c Release`
      termina en verde de verdad (0 errores).
- [ ] Ejecutar `dotnet run --project tools/winui3-lab/WinUi3Lab.csproj -c Release`
      y confirmar **a ojo** que aparece una ventana real, con el texto
      "WinUi3Lab" y el botón "Probar mpv en HWND hijo" visibles y legibles
      (tamaño, fuente, que no esté en blanco ni recortada).
