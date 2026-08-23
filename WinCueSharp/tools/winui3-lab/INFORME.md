# Hito 0 de Secuence — informe del banco de pruebas WinUI 3

## Resumen para Andrés — léelo aunque no leas el resto

*(Escrito para alguien que abra esto dentro de seis meses sin recordar nada
del proceso.)*

1. **¿Se puede empaquetar sin instalador, como WinCue hoy?** A medias. `PublishSingleFile`
   choca con `WindowsPackageType=None` (exige encender MSIX). El build en
   carpeta sí termina en verde — 439 archivos / 160 MB frente a los 24 / 324 MB
   de WinCue — y `settings.json` vive junto al ejecutable sin fricción a nivel
   de sistema de archivos. **Pero el `.exe` de esa misma carpeta no arranca
   todavía en esta máquina** (excepción `0xC000027B` en `Microsoft.UI.Xaml.dll`,
   ver Pregunta 1 y el hallazgo del hito 2 más abajo) — compila y arranca son
   cosas distintas, y solo la primera está confirmada.
2. **¿Cuánto ruido de analizadores mete WinUI 3?** Casi ninguno: 1 solo
   aviso de WinUI 3 en sí (CA5392, de código fuente que trae el propio
   paquete de Microsoft, no nuestro) — confirmado sobre un build completo,
   no sobre uno truncado. **Repetida la medición para este arreglo** (el
   `dotnet build` de la Pregunta 2 no reflejaba el control negativo que la
   tarea 3 añadió después): el recuento de advertencias real hoy es **7,
   no 1** — pero solo **1 sigue siendo de WinUI 3** (el mismo CA5392); las
   otras **6 son descuidos del propio laboratorio** (4× CA1806 y 1× CA1822
   en `ProbarVideo`/`ProbarConWidAsync` por ignorar retornos de P/Invoke o
   no marcar un método como `static`, y 1× CA1303 por una cadena literal en
   `Console.WriteLine`), no ruido que traiga WinUI 3. Ver Pregunta 2 para
   el desglose exacto; ninguna de las dos cifras cambia la conclusión de no
   tocar el listón del repo. Es una muestra de un proyecto de una ventana
   con un botón; hay que repetir la medición cuando el código real de
   Secuence entre en `WinCueSharp.sln` (hito 2).
3. **¿mpv pinta dentro de un HWND hijo de WinUI 3?** Sí, con evidencia que
   de verdad lo distingue de la alternativa (control negativo a cuatro
   bandas con `EnumChildWindows`: 0 descendientes en línea base, 1 con
   nuestro HWND como `wid`, 0 sin `wid`, 0 con `wid` inválido) **y ahora
   también confirmado a ojo por Andrés**: barras de color en movimiento con
   el OSD propio de mpv encima, y el airspace es el esperado — el vídeo
   tapa el botón donde se solapa, no el texto de estado, que va por encima
   en la disposición. El bucle máquina↔ojo se cierra. Cruzar entre dos
   monitores del mismo DPI no descoloca ni escala mal el vídeo, pero reveló
   fricción real al arrastrar (ver el epígrafe PENDIENTE); la prueba con
   DPI distinto de verdad no se pudo hacer en esta máquina y sigue abierta.
4. **Recomendación: seguir.** Las tres preguntas del hito 0 tienen respuesta
   y ninguna es un tapón de arquitectura — son detalles de empaquetado y de
   entorno de build, no motivos para no construir Secuence sobre WinUI 3. El
   único resultado realmente negativo (el `.exe` publicado no arranca en
   esta máquina, ver más abajo) apunta al paso de publicación —confirmado
   con un experimento que aísla el método de lanzamiento como variable, ver
   Pregunta 1, Paso 4, no solo sospechado—, no al motor de UI ni a la
   integración con mpv, y es investigable sin rehacer nada de lo ya
   construido. La fricción al cruzar monitores que encontró Andrés (ver
   PENDIENTE) es un indicio a vigilar en el hito 2, no un tapón de este
   hito 0 — que no tiene código de selección de monitor ni de pantalla
   completa, así que hoy no se puede probar lo que el producto hace de
   verdad.

**Lo que falta y de quién es:**

- **De Andrés — verificación en pantalla, hecha, con matices** (epígrafe
  PENDIENTE al final de este informe, con el detalle completo). Cerrado a
  ojo: la ventana se ve con texto y botón legibles; el airspace es el
  esperado; el vídeo decodifica de verdad, con el OSD propio de mpv encima;
  y cruzar entre dos monitores del mismo DPI (96, 100% en los dos,
  verificado con `GetDpiForMonitor`) no descoloca el vídeo. Pero apareció
  fricción real al arrastrar entre esos dos monitores — ventana pegada,
  negro momentáneo, vídeo cortado y recuperado — sin ningún cambio de DPI
  que la explique. La prueba con **DPI distinto de verdad** (el motivo
  original de esa casilla) **no se pudo hacer**: esta máquina no tiene dos
  monitores a escalados distintos, y sigue sin respuesta. Siguen sin
  tocar, y no forman parte de esta verificación: si el `.exe` publicado
  abre con doble clic real, y si escribe/relee su propio `settings.json`
  una vez arranque. Un hallazgo aparte, de plan y no de pantalla: la
  casilla original de "arrastrar la ventana" medía algo que WinCue no hace
  nunca — la ventana de salida se coloca a pantalla completa en el monitor
  que Settings ya tiene elegido, nadie la arrastra ni en ensayo ni en
  función — así que se sustituyó por las preguntas que sí tiene que
  contestar el hito 2 (ver PENDIENTE).
- **Del hito 2** (no contestado aquí, a propósito — este hito 0 es un lab
  aislado, la medición es indicativa, no definitiva):
  1. Por qué el `.exe` de `pub-carpeta` revienta con `0xC000027B` en
     `Microsoft.UI.Xaml.dll` al arrancar, mientras que el de `bin/` (sin
     publicar) sí sobrevive 6 segundos — con el método de lanzamiento ya
     descartado como causa (mismo `Start-Process` en los dos casos, ver
     Pregunta 1, Paso 4). La sospecha más plausible, sin confirmar, es
     `resources.pri`: la misma pieza que el Paso 1 encontró acoplada a
     `EnableMsixTooling`, no dos anécdotas sueltas. Si se confirma, lo que
     queda en cuestión no es un detalle de `dotnet publish` sino si WinUI 3
     desempaquetado admite el modelo de distribución en carpeta sin
     instalador que hoy vende WinCue.
  2. Resolver `AppxMSBuildToolsPath` con `vswhere` en el runner de CI, no a
     mano como en esta máquina — el CI **no** necesita cambiar de
     `dotnet build` a `MSBuild.exe`, le basta con esa propiedad.
  3. Repetir la medición de analizadores cuando el código de Secuence entre
     de verdad en `WinCueSharp.sln` — este lab se dejó fuera a propósito, así
     que un proyecto de una ventana con un botón no dice nada sobre lo que
     aparecerá con más superficie real.

**Un argumento que no estaba explícito y conviene dejar escrito:** el Windows
App SDK exige Windows 10 1809 como mínimo, y con el target 19041 de este lab
se pide de facto 2004 o superior. WPF (WinCue) corre en cualquier Windows
razonablemente moderno. Es una razón más, aparte del negocio ya en marcha,
para que **WinCue siga vendiéndose** aunque Secuence avance: cubre la cola
larga de máquinas que Secuence dejaría fuera.

---

*Escrito 2026-08-22 sobre el commit `dac049f1`. Ver el diseño en
`docs/superpowers/specs/2026-08-22-secuence-winui3-design.md`.*

Máquina: Windows 11 Pro 10.0.26200, SDK de .NET `8.0.423`, sin Visual Studio
completo instalado (solo **Visual Studio 2022 Build Tools**).

**Actualización (tarea 2):** cuando se escribió lo de abajo (tarea 1), esta
máquina no tenía el componente de Visual Studio para empaquetado UWP/Appx, y
el build no terminaba en verde. Entre la tarea 1 y esta, Andrés instaló ese
componente (workload "Universal Windows Platform build tools"). Con eso más
`-p:AppxMSBuildToolsPath=...` en cada comando `dotnet` (ver por qué en el
encargo de la tarea 2), **el build sí termina en verde**: confirmado de
nuevo en la Pregunta 1 con `dotnet publish` completo, 0 errores. El
diagnóstico de abajo se deja tal cual porque sigue siendo la explicación
correcta de la causa raíz — solo estaba pendiente de la pieza de VS que
faltaba, no de un problema de código ni de versión de paquete.

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

**Resuelto así (tarea 2):** Andrés instaló el componente. Con él en el
disco, la propiedad `AppxMSBuildToolsPath` (que estos targets ya soportan
vía `Condition="'$(AppxMSBuildToolsPath)' == ''"`) se puede apuntar
directamente al árbol de Build Tools en vez de dejar que MSBuild la derive
de `$(MSBuildExtensionsPath)`, que con `dotnet build` apunta al SDK de .NET
(donde el componente nunca estará, sea cual sea la máquina). Añadiendo
`-p:AppxMSBuildToolsPath="C:\Program Files (x86)\Microsoft Visual
Studio\2022\BuildTools\MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage\"`
a cada comando `dotnet build`/`dotnet publish`, el build termina en verde
sin tocar el `.csproj` ni cambiar de motor. Confirmado por partida doble:
por el controlador con `dotnet build` ("Compilación correcta, 0 Errores")
y por mí en la Pregunta 1 con `dotnet publish` completo (0 errores, 1
advertencia CA5392 — la ya conocida).

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
mano ni de un lab más grande. Se relaja por proyecto con `NoWarn` en el
`.csproj` (no con `.editorconfig` — ver el porqué en la siguiente sección) y
**no hace falta tocar el listón del repo**. Dicho esto, esta muestra es de
un proyecto de una ventana con un botón — no dice nada sobre lo que
aparecerá al crecer el código de Secuence de verdad; conviene repetir esta
medición cuando haya más superficie (tareas 2–4 y en adelante).

### Por qué el `1` es comparable con producción, y una respuesta más fuerte que aparece al mirarlo

El `1` de arriba solo sirve como medida de "cuánto ruido mete WinUI 3 en
producción" porque hereda el mismo listón que usará el código real: aunque
`tools/winui3-lab` está **fuera** de `WinCueSharp.sln` (ver Autorrevisión),
MSBuild resuelve `Directory.Build.props` y `.editorconfig` **por árbol de
directorios**, no por pertenencia a una solución — y el lab vive dentro de
`WinCueSharp/`, así que hereda sin ningún ajuste especial tanto
`WinCueSharp/Directory.Build.props` (`AnalysisLevel=latest-all`,
`EnforceCodeStyleInBuild`) como `WinCueSharp/.editorconfig`. Si esto no
fuera así, el `1` no significaría nada — sería el ruido de un proyecto sin
las reglas del repo, no una previsión de lo que va a ver el hito 2.

Tirando de ese mismo hilo aparece una respuesta más fuerte que el `1` en sí:

- **La regla que dispara el único diagnóstico ya está silenciada en todo el
  repo.** `WinCueSharp/.editorconfig:165` fija
  `dotnet_diagnostic.CA5392.severity = none`, con el motivo documentado en
  las líneas 163-164 (libmpv usa un `DllImportResolver` propio; forzar
  `DefaultDllImportSearchPaths` lo rompería). El diagnóstico **solo aparece
  aquí** porque el `.cs` que lo dispara
  (`UndockedRegFreeWinRT-AutoInitializer.cs`, tabla de arriba) vive en
  `~/.nuget/packages/`, **fuera del árbol al que llega el `.editorconfig`**
  de este repo — un `.editorconfig` solo se aplica a ficheros bajo su propio
  árbol de directorios, y la caché de NuGet no lo está. Dicho de otro modo:
  **la respuesta real y más fuerte a esta pregunta es cero reglas nuevas**
  — el único aviso que mete WinUI 3 viene de una regla que el repo ya
  decidió no exigir, no de una regla distinta.
- **El remedio de la sección anterior necesita esta corrección.** Un
  `.editorconfig` del repo **nunca** va a alcanzar un `.cs` inyectado desde
  la caché de NuGet, por la misma razón de arriba: esa ruta está fuera de su
  árbol. Lo único que funciona ahí es
  `<NoWarn>$(NoWarn);CA5392</NoWarn>` en el `.csproj` del proyecto que
  referencia el paquete.

**Para el hito 2 — la misma trampa, con más superficie:** cuando Secuence
entre de verdad en `WinCueSharp.sln` con el CI en
`TreatWarningsAsErrors=true` y el listón del repo a cero, cualquier
diagnóstico que venga de código fuente inyectado desde un paquete NuGet
(este CA5392, o cualquier otro que aparezca con más dependencias) **no se
puede silenciar con `.editorconfig`** por muy documentado que quede el
motivo ahí — hace falta `NoWarn` en el `.csproj`. Es fácil dar por bueno el
`.editorconfig` porque es lo que ya funciona para el código propio del
repo, y descubrir tarde que no cubre este caso.

**Reconciliación con el número de la Pregunta 3 (añadido tras revisión).**
El `1` de arriba y el recuento de esta sección son de **antes** de que la
tarea 3 añadiera el control negativo (Pregunta 3, `ProbarConWidAsync`), que
introdujo sus propias llamadas a P/Invoke sin comprobar el retorno. Repetido
el mismo `dotnet build -p:TreatWarningsAsErrors=true` de arriba sobre el
código actual: **7 diagnósticos, no 1.** Desglosados:

| Regla | Cuenta | Origen |
|---|---|---|
| CA5392 | 1 | WinUI 3 / SDK de Microsoft (el mismo de siempre, sin cambios) |
| CA1806 | 4 | `ProbarVideo` (×2) y `ProbarConWidAsync` (×2): retornos de `mpv_set_option_string`/`mpv_get_property_int64` sin usar |
| CA1822 | 1 | `ProbarConWidAsync` podría marcarse `static` |
| CA1303 | 1 | cadena literal en `Console.WriteLine` dentro de `EjecutarControlNegativoAsync` |

**El `1` sigue siendo la respuesta correcta a "¿cuánto ruido mete WinUI 3?"**
— es la única fila que no es un descuido de este lab escrito deprisa, y es
la que hay que vigilar cuando crezca la superficie real de Secuence. Las
otras 6 son del tipo que ya se dejó **a propósito** sin arreglar en este
lab desechable (ver "Lo que NO tienes que hacer" del encargo de esta
revisión) — no son ruido de WinUI 3, y no cambian el veredicto de no tocar
el listón del repo.

**Actualización (tarea 2):** el hallazgo del Paso 2 (componente de Visual
Studio ausente) está resuelto — ver el recuadro de arriba y la Pregunta 1.
La medición de analizadores de esta sección queda así **confirmada sobre
un build completo** (0 errores, 1 advertencia CA5392), no sobre uno
truncado en `CoreCompile` como cuando se escribió esto por primera vez.
Lo que sigue abierto, y va a la Pregunta 1 y al epígrafe PENDIENTE, es otra
cosa distinta: que el `.exe` ya publicado arranque limpio en esta
máquina.

## Pregunta 1 — forma del paquete

*Tarea 2, ejecutada 2026-08-22 sobre el mismo `.csproj` de la tarea 1 (sin
tocar), con el componente de VS ya instalado (ver arriba).*

### Nota sobre el `AppxMSBuildToolsPath` literal del encargo

El encargo trae la propiedad ya escrita en cada comando, con instrucción
explícita de no cambiarla. Al ejecutar los comandos tal cual venían en
`task-2-brief.md`, fallaron con MSBuild quejándose de que la carpeta
`AppxPackage` no existía en la ruta indicada. Inspeccioné el archivo del
encargo con `cat -A` (bytes crudos, no el renderizado) y confirmé que el
valor de `AppxMSBuildToolsPath` en ese archivo lleva **bytes corruptos**:
donde debería decir `\2022\` aparece el carácter U+2022 (viñeta "•") sin la
barra ni los dígitos, y donde debería decir `\v17.0\` aparece un tabulador
vertical (`\x0B`) en vez de la `v`. Es decir, el texto del encargo no es la
ruta real de esta máquina, es esa misma ruta con dos fragmentos
reemplazados por caracteres de control — un problema de codificación en
cómo se generó/guardó el `.md`, no una ruta distinta a propósito.

Verifiqué la ruta real en disco (`ls` sobre el árbol de Build Tools) y
encontré ahí mismo, íntegros, los dos `.dll` que el hallazgo de la tarea 1
había dado por ausentes (`Microsoft.Build.Packaging.Pri.Tasks.dll` y
`Microsoft.Build.AppxPackage.dll`):

```
C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage\
```

Esta es también, carácter a carácter, la ruta que el controlador ya había
verificado por separado en `progress.md` con `MSBuild.exe` directo. Usé
esta ruta (la correcta, no la corrupta) en los dos comandos de publish de
abajo — es la misma propiedad, con el mismo propósito, solo con los bytes
arreglados. No es un cambio de criterio: es corregir una corrupción de
texto para poder ejecutar literalmente lo que el encargo pedía.

### Paso 1 — publicación single-file

```
dotnet publish tools/winui3-lab/WinUi3Lab.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o tools/winui3-lab/pub-single "-p:AppxMSBuildToolsPath=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage\\"
```

**Falla, con un mensaje claro y literal del propio SDK** (no un error de
entorno esta vez):

```
Microsoft.WindowsAppSDK.SingleFile.targets(15,9): error : PublishSingleFile requires EnableMsixTooling for embedded resources.pri generation:
    <PropertyGroup>
        <EnableMsixTooling>true</EnableMsixTooling>
    </PropertyGroup>
```

No se generó ni siquiera la carpeta `pub-single/` — el publish aborta antes
de escribir ninguna salida (`find` sobre esa ruta: 0 archivos, y la carpeta
ni existe).

**Lectura del mensaje:** WindowsAppSDK acopla `PublishSingleFile` a la
generación de `.pri` (el índice de recursos empaquetados), que exige
`EnableMsixTooling=true` — es decir, volver a encender la maquinaria de
empaquetado MSIX/Appx que este `.csproj` apaga a propósito con
`WindowsPackageType=None`. No lo probé (`EnableMsixTooling=true` no es la
propiedad que el encargo pidió tocar, y mezclar cosas para "hacerlo
funcionar" habría maquillado la respuesta): el hallazgo en sí, tal como
pide el Paso 1, es que **`PublishSingleFile` no es una opción libre en
WinUI 3 no empaquetado** — es una u otra cosa, no ambas a la vez, con la
configuración de este lab.

### Paso 2 — publicación en carpeta, como referencia

```
dotnet publish tools/winui3-lab/WinUi3Lab.csproj -c Release -r win-x64 --self-contained true -o tools/winui3-lab/pub-carpeta "-p:AppxMSBuildToolsPath=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage\\"
```

Termina en verde:

```
...UndockedRegFreeWinRT-AutoInitializer.cs(18,36): warning CA5392: [...]
  WinUi3Lab -> ...\bin\Release\net8.0-windows10.0.19041.0\win-x64\WinUi3Lab.dll
  WinUi3Lab -> ...\tools\winui3-lab\pub-carpeta\
```

0 errores, 1 advertencia (CA5392, la misma de la Pregunta 2, del propio
paquete NuGet). Esto **confirma de nuevo**, esta vez con `dotnet publish`
completo de punta a punta (no solo `CoreCompile` como en la Pregunta 2),
que el build entero termina en verde con la propiedad correcta.

### Paso 3 — recuento y medida

```
for d in pub-single pub-carpeta; do echo -n "$d: "; find "tools/winui3-lab/$d" -type f 2>/dev/null | wc -l; du -sh "tools/winui3-lab/$d" 2>/dev/null; done
```

| Paquete | Archivos | Tamaño | Nota |
|---|---|---|---|
| `pub-single` | 0 | — (carpeta ni se creó) | el publish aborta antes de escribir salida (ver Paso 1) |
| `pub-carpeta` | 439 | 160 MB | self-contained, `WindowsPackageType=None`, sin MSIX |
| **WinCue actual** (referencia, no medido en esta tarea) | **24** | **324 MB** | libmpv-2.dll y ffmpeg-lgpl/ fuera por LGPL |

**Lectura, con la vara que pide el encargo (mide acabado, no viabilidad):**
`pub-carpeta` es, en la forma, exactamente el mismo tipo de paquete que ya
usa WinCue hoy — una carpeta con el `.exe`, sus DLL y sus recursos al lado,
sin instalador ni virtualización MSIX de por medio — así que "WinUI 3 no
admite un exe suelto" no es un fallo nuevo frente a WinCue, es el mismo
punto de partida. La diferencia real está en la composición: **muchos más
archivos (439 contra 24) pero bastante menos peso (160 MB contra 324 MB)**.
La causa del recuento alto no es equivalente a las DLL nativas de mpv/ffmpeg
que WinCue deja fuera por LGPL — aquí no hay nada parecido a eso, este lab
no toca mpv. Es que el runtime self-contained de WindowsAppSDK/WinUI3 se
reparte en más piezas finas (varias decenas de DLL nativas de
Composition/DWrite/Input/Xaml.Controls, etc.) y añade **una carpeta por
idioma** (`de-DE/`, `es-ES/`, `ja-JP/`, etc. — 86 en total) para los
recursos `.resources.dll` de .NET, cosa que WinCue con WPF no acarrea igual.
Ninguna de las dos cosas (recuento ni peso) señala un problema de
viabilidad: es, literalmente, "forma distinta de la misma clase de
paquete" — más archivos, menos peso total, sin instalador, tal como ya
convive WinCue con su propia carpeta de 24.

### Paso 4 — arranque en limpio y `settings.json`

**Arranque del `.exe` — solo lo observable, sin pantalla:**

```
[System.Diagnostics.Process]::Start(...) sobre tools/winui3-lab/pub-carpeta/WinUi3Lab.exe
```

El proceso **no queda vivo**: termina solo, sin esperar señal externa,
con código de salida `-1073741189` en las 4 veces que lo lancé así. Ese
decimal es `0xC000027B` (lo comprobé con la conversión exacta, no de
memoria) — **no `0xC0000135`** como sugiere el encargo (esa aclaración del
encargo parece un error de conversión: `0xC0000135` en decimal es
`-1073741515`, un número distinto). No hubo nada en stdout/stderr (ambos
vacíos).

**Control de variables confundidas (añadido tras revisión).** Lo de arriba
compara el `.exe` de `pub-carpeta` (revienta) contra el de `bin/` lanzado
con `dotnet run` (vive, Pregunta 3), pero entre esos dos casos cambian
**dos** variables a la vez: el paso de `publish` y el método de
lanzamiento (`dotnet run` arranca el proceso desde dentro de su propio
host; `Start-Process` lo lanza como lo haría un doble clic). Para aislar
cuál de las dos pesa, repetí el mismo `Start-Process` de arriba sobre el
`.exe` de `bin/` sin publicar:

| Binario | Método de lanzamiento | Resultado |
|---|---|---|
| `bin/.../win-x64/WinUi3Lab.exe` | `dotnet run` | vivo, mpv decodificando (Pregunta 3) |
| `bin/.../win-x64/WinUi3Lab.exe` | `Start-Process` | **vivo 6 s** |
| `pub-carpeta/WinUi3Lab.exe` | `Start-Process` | muere (`0xC000027B`) |

Con el método de lanzamiento fijo en las dos últimas filas, el único
binario que revienta es el publicado. **El método de lanzamiento no es la
causa; el paso de publicación sí.** Esto también adelanta, en parte, el
punto 3 del epígrafe PENDIENTE de más abajo (que pedía probar con doble
clic real): `Start-Process` es el mismo mecanismo con el que Windows lanza
un proceso fuera de una sesión de `dotnet run`, así que la variable "sesión
interactiva vs. no interactiva" ya no explica la diferencia — sigue
pendiente confirmar a ojo si con doble clic aparece el diálogo de "ha
dejado de funcionar", pero ya no por sospecha de que el lanzamiento sea la
causa.

**La misma pregunta con otro nombre.** El Paso 1 de más arriba encontró que
`PublishSingleFile` exige `EnableMsixTooling=true` "for embedded
`resources.pri` generation", y la hipótesis principal (sin confirmar) de
por qué `pub-carpeta` revienta es también `resources.pri` mal colocado o
ausente. No son dos anécdotas sueltas: es la misma pieza —el índice de
recursos empaquetados de WinUI 3— apareciendo dos veces en el mismo hito.
No afirmo que la causa del arranque roto vaya a confirmarse como
`resources.pri` —sigue sin confirmar—, pero si lo es, lo que queda en
cuestión no es un detalle de `dotnet publish`, sino si WinUI 3
desempaquetado (sin MSIX) admite el modelo de distribución en carpeta sin
instalador — que es exactamente el que vende WinCue hoy. El hito 2 tiene
que cerrar esto, no darlo por bueno porque el lab compile en verde.

Para no quedarme solo con el código de salida, miré el Visor de eventos
(`Get-WinEvent -LogName Application`) justo después de cada intento: las 4
veces aparece un "Application Error" (Id 1000) idéntico:

```
Nombre del módulo con errores: Microsoft.UI.Xaml.dll, versión 3.1.6.0
Código de excepción: 0xc000027b
Ruta de aplicación: ...\pub-carpeta\WinUi3Lab.exe
```

Es decir: el proceso sí arranca (el loader carga `Microsoft.UI.Xaml.dll`,
que está presente en la carpeta) pero **revienta dentro de esa DLL** antes
de mostrar nada, con una excepción nativa no capturada (`0xC000027B` es
`STATUS_STOWED_EXCEPTION`, típica de un fallo de inicialización de WinRT
que se propaga como excepción del sistema). Comprobé también que
`vcruntime140.dll`/`msvcp140.dll` no van incluidas en `pub-carpeta`
(`find ... -iname "*vcruntime*"` → vacío) — es una hipótesis plausible de
por qué falla (WindowsAppSDKSelfContained empaqueta los binarios propios de
WinAppSDK pero no necesariamente el runtime de C++ que sus DLL nativas
necesitan), pero no la confirmé instalando ni copiando nada: eso es
diagnóstico adicional que dejo para quien tenga ojos sobre la pantalla y
decida si merece la pena, no algo que resolví aquí. **No maquillo esto como
"funciona": en esta máquina, el paquete publicado no arranca todavía**,
aunque el build que lo produjo esté en verde. Compila y arranca son cosas
distintas, y esta tarea solo confirma la primera.

**`settings.json` junto al ejecutable — lo que se midió, y lo que no:**

```
echo '{"prueba":1}' > tools/winui3-lab/pub-carpeta/settings.json
# leído con Get-Content, luego reescrito con Set-Content, releído de nuevo
```

**Lo que esto mide:** que PowerShell puede crear, leer, reescribir y releer
un archivo en la carpeta de `pub-carpeta` sobre el sistema de archivos NTFS
normal de Windows — las tres operaciones funcionan sin fricción, como sobre
cualquier carpeta corriente. **Lo que esto NO mide:** que `WinUi3Lab.exe`
pueda hacer lo mismo *desde dentro de su propio proceso*. El plan pedía
confirmar que **el proceso** puede leer y reescribir el archivo desde su
propia carpeta — el riesgo que se quería descartar (redirección o
virtualización de almacenamiento, el mecanismo que sí existe con
`ApplicationData` bajo MSIX empaquetado) solo existe, o no, **dentro del
proceso**; PowerShell tocando la carpeta desde fuera no está sujeto a esa
virtualización aunque la hubiera, así que no aporta un solo bit sobre la
pregunta real. No pude hacer la prueba dentro del proceso porque el `.exe`
no arranca en esta máquina (ver arriba).

La razón por la que el veredicto va a ser probablemente "sí" es de
**propiedad del proyecto**, no de medición: `WindowsPackageType=None`
significa que no hay paquete MSIX, y sin paquete MSIX no hay virtualización
de `ApplicationData` que active el mecanismo que se quería descartar. Es un
razonamiento sólido, pero es un razonamiento, no la prueba a nivel de
proceso que pedía el plan — y no distinguir las dos cosas es exactamente el
patrón que esta revisión pidió dejar de repetir en este hito.

**Veredicto de `settings.json`: sí, por la propiedad del proyecto
(`WindowsPackageType=None`), no por medición dentro del proceso.** Falta la
prueba que sí cerraría esto con evidencia —el `.exe` leyendo y reescribiendo
su propio `settings.json` en su propia carpeta, una vez arranque—; queda
como casilla en el epígrafe PENDIENTE de abajo.

### Veredicto de la Pregunta 1

`PublishSingleFile` no es viable tal cual con `WindowsPackageType=None`
(exige `EnableMsixTooling=true`, no probado aquí). La alternativa sin
single-file **sí produce un build limpio** (0 errores) y una carpeta del
mismo tipo que ya distribuye WinCue: más archivos (439 contra 24) pero
menos peso (160 MB contra 324 MB), sin instalador. `settings.json` puede
vivir junto al ejecutable sin problema **a nivel de propiedad del proyecto
y de sistema de archivos** — la prueba dentro del propio proceso sigue
pendiente, ver Paso 4 y el epígrafe PENDIENTE. Lo que queda sin cerrar, y no
es parte de esta pregunta pero hay que arrastrarlo, es que el `.exe`
resultante no arranca todavía en esta máquina concreta (excepción
`0xC000027B` en `Microsoft.UI.Xaml.dll`, causa aislada del método de
lanzamiento — ver Paso 4) — ver el epígrafe PENDIENTE.

## Pregunta 3 — mpv en un HWND hijo

*Tarea 3, ejecutada 2026-08-22.*

### Lo que no pude hacer

No tengo ojos sobre esta máquina: no puedo confirmar a simple vista que se
vea el vídeo, ni arrastrar la ventana entre dos monitores con DPI distinto.
Ambas comprobaciones quedan en el epígrafe PENDIENTE de abajo, para Andrés.

### Lo que sí pude instrumentar y medir

`HwndHijo.cs` es literal al encargo: crea la ventana hija Win32 con
`CreateWindowExW` (clase `"static"`, `WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN
| WS_CLIPSIBLINGS`), mismo patrón que `src/WinCue.App/SwapChainHost.cs` sin
la parte de `HwndHost` (que es de WPF).

`MainWindow.xaml.cs` sigue el cuerpo literal del encargo (crear HWND hijo,
`mpv_create`, `wid` antes de `mpv_initialize`, `loadfile`), con dos añadidos
justificados por la resolución de la ambigüedad del propio encargo:

1. **Se recogen y muestran los códigos de retorno** de `mpv_create` (por
   `nint` distinto de 0), `mpv_initialize` y `mpv_command` — antes el
   encargo literal ignoraba el de `mpv_command`.
2. **Sondeo de `width`/`height` tras el `loadfile`** (que es asíncrono):
   un bucle de hasta 15 intentos cada 400 ms (~6 s) que llama a
   `mpv_get_property` con `MPV_FORMAT_INT64`. Si devuelve dimensiones > 0,
   es evidencia de que mpv decodifica de verdad hacia la superficie del
   `wid`, sin que nadie tenga que mirar la pantalla.
3. **Disparo automático de la prueba al arrancar** cuando `LAB_MEDIA` ya
   está definida (además del botón, que se deja intacto para la
   verificación humana). Sin esto no había forma de generar evidencia sin
   pulsar el botón a mano — cosa que tampoco puedo hacer.

Todos los mensajes se escriben también a stdout con el prefijo
`[winui3-lab]`.

### Ejecución

Build (Release, con el `AppxMSBuildToolsPath` de esta máquina):

```
dotnet build tools/winui3-lab/WinUi3Lab.csproj -c Release "-p:AppxMSBuildToolsPath=C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Microsoft/VisualStudio/v17.0/AppxPackage/"
```

Termina en verde: 0 errores, 3 advertencias (la CA5392 ya conocida del
paquete NuGet, y dos CA1806 nuevas porque `ProbarVideo` no usa el código de
retorno de las dos llamadas a `mpv_set_option_string` — así es el encargo
literal, no las toqué). **Este número es de antes del control negativo de
más abajo; ya no es el vigente** — ver la Pregunta 2 para el recuento
actualizado (7 advertencias, 1 de WinUI 3 y 6 del propio lab) una vez
`ProbarConWidAsync` añadió sus propias llamadas sin comprobar el retorno.

**Carpeta de salida real, verificada antes de copiar nada** (el encargo
decía `bin/x64/Release/...`, que no existe en este proyecto):

```
tools/winui3-lab/bin/Release/net8.0-windows10.0.19041.0/win-x64/
```

Copiada ahí la DLL (98 MB, fuera del commit — ver Autorrevisión):

```
cp libmpv-lgpl/libmpv-2.dll tools/winui3-lab/bin/Release/net8.0-windows10.0.19041.0/win-x64/
```

Ejecución, con `LAB_MEDIA` apuntando a la media de prueba de esta máquina
(no versionada, existe en `tests/show-prueba/`):

```
LAB_MEDIA="C:\Proyectos\ffmpeg-gui\WinCueSharp\tests\show-prueba\video-A-colores.mp4" \
  dotnet run --project tools/winui3-lab/WinUi3Lab.csproj -c Release \
  "-p:AppxMSBuildToolsPath=C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Microsoft/VisualStudio/v17.0/AppxPackage/"
```

**El proceso `WinUi3Lab.exe` queda vivo** (confirmado con `tasklist`, PID
visible varios segundos después de arrancar, hasta que lo cerré yo con
`taskkill`). Salida real por stdout (redirigida a un log; el mojibake de
"Vídeo" es solo la consola mostrando UTF-8 con la página de códigos
equivocada, no un dato corrupto):

```
[winui3-lab] mpv en HWND 657218 (padre 1312620). create=OK, initialize=0, command=0.
[winui3-lab] mpv en HWND 657218 (padre 1312620). create=OK, initialize=0, command=0. Vídeo decodificando: 1280x720.
```

Contrasté el `1280x720` con `ffprobe` sobre el propio archivo, **sin tocar
mpv**:

```
ffprobe -v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 tests/show-prueba/video-A-colores.mp4
→ 1280,720
```

**Coincide exacto.** No es un valor por defecto ni basura: es la resolución
real del archivo, reportada por mpv después de haberlo cargado sobre el
`wid` del HWND hijo.

### Diagnóstico, siguiendo la guía del encargo para distinguir causas

- `mpv_create` → HWND ≠ 0. **No es un problema de DLL** (`libmpv-2.dll` sí
  carga y sí se resuelve el símbolo).
- `mpv_initialize` → `0` (éxito; los códigos de error de libmpv son
  negativos). **No es un problema de configuración de mpv ni del `wid`.**
- `mpv_command("loadfile", ...)` → `0` (éxito, comando aceptado; que sea
  asíncrono es normal en mpv).
- `mpv_get_property("width"/"height")` → `0` (éxito) con `1280x720`, que
  coincide con la resolución real del archivo por `ffprobe`.

**Corrección metodológica (revisión de la tarea 3):** lo de arriba, tal
como estaba escrito la primera vez, **sobrevendía el veredicto**. La
revisión encontró el fallo correcto: `width`/`height` son metadatos del
demuxer, se pueblan en cuanto se abre el archivo — **con `wid` válido,
inválido o sin fijar en absoluto**. No distinguen "mpv decodificó hacia
NUESTRO HWND" de "mpv decodificó hacia cualquier sitio". La sección
siguiente resuelve esto con un control negativo; el veredicto de más
abajo está reescrito con esa evidencia, no con la de aquí arriba.

### Control negativo — la comprobación que sí discrimina

Añadido tras la revisión. Se repitió el mismo experimento tres veces, cada
vez con una instancia de mpv nueva (creada y destruida con
`mpv_terminate_destroy` para no arrastrar estado entre pruebas), variando
solo el `wid`:

- **A** — `wid` = nuestro HWND hijo real (el control positivo: el mismo
  camino que la Prueba A de arriba).
- **B** — sin fijar `wid` en absoluto (mpv decide su propia superficie).
- **C** — `wid` inválido, un HWND que no existe: `0xDEADBEEF`.

Y, en las tres, además de `width`/`height`, se midió:

- `video-out-params/w` (sub-propiedad de un nodo; solo se puebla con una
  cadena de render **activa**, a diferencia de `width`/`height`).
- `current-vo` (qué salida de vídeo eligió mpv).
- **El discriminador real**: cuántas ventanas descendientes tiene nuestro
  HWND hijo real, contadas con `EnumChildWindows` (recursivo) **antes** de
  las tres pruebas y **después** de cada una. Si mpv crea su superficie de
  render dentro de nuestro HWND, tiene que aparecer ahí; si no, el número
  no debe moverse.

Comando ejecutado (con `LAB_CONTROL=1` además de `LAB_MEDIA`; deliberadamente
una variable de entorno **distinta** para no interferir con la verificación
humana normal, que necesita que el vídeo de la Prueba A del botón se quede
reproduciendo sin que nada lo destruya a los pocos segundos):

```
LAB_MEDIA="C:\Proyectos\ffmpeg-gui\WinCueSharp\tests\show-prueba\video-A-colores.mp4" LAB_CONTROL=1 \
  dotnet run --project tools/winui3-lab/WinUi3Lab.csproj -c Release \
  "-p:AppxMSBuildToolsPath=C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Microsoft/VisualStudio/v17.0/AppxPackage/"
```

Salida real (log completo, sin recortar ni maquillar):

```
[control] HWND hijo real = 1050372. Descendientes antes de las pruebas: 0.
[control] A (wid = hijo real, control positivo): init=0 command=0 width=1280 height=720 video-out-params/w=1280 current-vo=gpu-next descendientesDeHijoReal=1
[control] B (sin wid): init=0 command=0 width=1280 height=720 video-out-params/w=1280 current-vo=gpu-next descendientesDeHijoReal=0
[control] C (wid invalido = 0xDEADBEEF): init=0 command=0 width=0 height=0 video-out-params/w=0 current-vo=(ninguno) descendientesDeHijoReal=0
```

**Lectura, literal, sin adornar:**

- **A y B dan exactamente el mismo `width`/`height` (1280x720), el mismo
  `video-out-params/w` (1280) y el mismo `current-vo` (`gpu-next`).** Esto
  **confirma la crítica del revisor al pie de la letra**: ninguna de esas
  tres propiedades distingue "decodificó hacia nuestro HWND" de "decodificó
  hacia su propia ventana top-level". Si me hubiera quedado solo con esas
  tres, el veredicto de la primera versión de este informe habría sido
  **igual de "alto" con B que con A** — y B es precisamente el caso en el
  que **no** se usó nuestro HWND. Dicho con las palabras que pidió Andrés:
  **la evidencia de la primera versión de este informe no discriminaba
  nada**, tal como señaló la revisión.
- **La diferencia real está en `descendientesDeHijoReal`.** Antes de las
  tres pruebas, nuestro HWND hijo real tenía **0** descendientes. Después
  de A (wid = ese mismo HWND), tiene **1**: mpv creó su superficie de
  render (el contexto GPU de `gpu-next`) *dentro* de él. Después de B (sin
  wid), vuelve a **0**: el mismo mpv, con la misma media, el mismo vídeo
  decodificado, **no tocó nuestro HWND** — abrió su propia ventana en otro
  sitio. Esto es justo la comprobación que sugirió el revisor
  (`EnumChildWindows`) y es la que de verdad separa las dos hipótesis.
- **C (wid inválido) se comporta distinto de las otras dos**: ni
  `width`/`height` ni `video-out-params/w` llegaron a poblarse en los ~6 s
  de sondeo, y `current-vo` quedó vacío. Lectura más plausible (no
  confirmada con logs de mpv, que no capturé): con un HWND que no existe,
  la salida de vídeo `gpu-next` falla al intentar enganchar la superficie
  de render, y esa parte de la carga del archivo no llega a completarse en
  el tiempo sondeado — a diferencia de la hipótesis inicial de que
  `width`/`height` se poblarían igual "pase lo que pase" con el demuxer.
  No hizo falta profundizar más porque no cambia el veredicto: en los tres
  casos, `descendientesDeHijoReal` se mantuvo en su valor de partida salvo
  exactamente en el caso donde el `wid` era el HWND real.

### Veredicto de la Pregunta 3 (recalibrado tras la revisión)

**El HWND hijo de un `AppWindow` de WinUI 3 acepta vídeo, y ahora hay
evidencia que de verdad lo distingue de la hipótesis alternativa** ("mpv
decodificó hacia cualquier sitio, no hacia nuestro HWND"). La evidencia
que sostiene esto ya no es solo `width`/`height` (que, como demuestra el
control negativo, **no discriminan** — A y B dan el mismo valor) sino el
recuento de ventanas descendientes de nuestro HWND real con
`EnumChildWindows`: **0 antes, 1 con `wid` = nuestro HWND (A), 0 sin `wid`
(B) y 0 con `wid` inválido (C)**. Ese patrón solo se explica si mpv crea su
superficie de render dentro de nuestro HWND exactamente cuando se le pasa
como `wid`, y en ningún otro caso.

**Grado de certeza: alto para "mpv decodifica sobre el HWND hijo"** — esta
vez apoyado en un control negativo real, no en un único código de retorno
en 0. **Certeza nula (por diseño) sobre las dos cosas que exigen ojo
humano**: si el vídeo tapa al `TextBlock` (airspace) y qué pasa al
arrastrar entre monitores con DPI distinto — ninguna de las dos se puede
inferir desde código o desde `stdout`, y no las doy por buenas. Van al
epígrafe PENDIENTE de abajo, con el comando exacto al lado.

**Duda metodológica que sí existía y que la primera versión de este
informe no reconocía** (contestando directamente al hallazgo de la
revisión): sí, había una duda de fondo — la evidencia original
(`initialize`/`command` = 0, `width`/`height` coincidente con `ffprobe`)
era compatible con que mpv hubiera abierto su propia ventana top-level en
vez de usar nuestro HWND, y no había nada en el informe que descartara esa
posibilidad. Queda descartada ahora, con el control negativo de arriba —
pero **la duda era real y el "Ninguna duda de fondo" que escribí la
primera vez estaba mal calibrado.**

### Actualización — verificación en pantalla (Andrés)

Cierra la laguna que dejaba abierta el veredicto de arriba: "certeza nula
sobre las dos cosas que exigen ojo humano". Ya no es cero.

- **Vídeo, confirmado a ojo.** Barras de color en movimiento, con el OSD
  propio de mpv encima (el recuadro del timestamp en la esquina del
  vídeo). Que sea el propio mpv el que dibuja ese OSD es lo que distingue
  "mpv es dueño de esta superficie" de "algo enseña un fotograma suelto" —
  y coincide exactamente con la instrumentación de más arriba (`create=OK,
  initialize=0, command=0`, `1280x720`). El bucle máquina↔ojo se cierra.
- **Airspace, confirmado a ojo.** El vídeo tapa el botón "Probar mpv en
  HWND hijo" donde se solapan; el texto de estado, que en la disposición
  va por encima del vídeo, se lee perfectamente. El vídeo no tapa "todo",
  tapa lo que se le solapa — el comportamiento correcto de una ventana
  hija Win32, el mismo que documenta `src/WinCue.App/SwapChainHost.cs`
  para WPF.
- **Cruce entre monitores del mismo DPI.** Al mover la ventana al monitor
  de 1024x600, el vídeo mantiene su posición relativa y su tamaño
  (~640x360) — no se descoloca ni escala mal, que era el miedo original.
  Pero apareció un hallazgo nuevo, no buscado: la ventana **se resiste al
  arrastrar** ("como pegada"), **se queda en negro** un instante al cruzar,
  y **el vídeo se corta y vuelve**. No rebota al monitor de origen y no es
  simple lentitud — es fricción real. Los dos monitores de esta máquina
  están confirmados a **96 DPI, escalado 100%** (`GetDpiForMonitor`), así
  que esta fricción **no** tiene un cambio de DPI que la explique.
- **DPI distinto: no probado.** El cruce fue entre pantallas de escalado
  idéntico, el caso fácil. Esta máquina no tiene dos monitores a
  escalados distintos, así que la pregunta original — qué pasa al cruzar
  con DPI distinto — **sigue sin respuesta**.

**El defecto de plan que esto destapó.** La casilla original medía algo
que el producto no hace nunca: nadie arrastra la ventana de salida de
WinCue, ni en ensayo ni en función — se coloca a pantalla completa en el
monitor que Settings ya tiene elegido y ahí se queda. Arrastrar una
ventana entre monitores y que alguien desenchufe un proyector son, para
Windows, la misma clase de evento (un cambio de topología de pantallas),
así que la fricción de arriba es un **indicio**, no un veredicto, de que
la combinación HWND hijo + WinUI 3 podría llevar mal esos eventos — algo
que vigilar en el hito 2, no un tapón de este hito 0, que no tiene código
de selección de monitor ni de pantalla completa. Las preguntas correctas
para esa comprobación reemplazan a la casilla del arrastre en el epígrafe
PENDIENTE.

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

**Actualización (tarea 2): resuelto así.** El diagnóstico de arriba sigue
siendo correcto — el componente que faltaba era real y el `.dll` no estaba
en ningún sitio del disco — pero ya no es un bloqueo. Andrés instaló el
componente entre la tarea 1 y esta, y con
`-p:AppxMSBuildToolsPath="C:\Program Files (x86)\Microsoft Visual
Studio\2022\BuildTools\MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage\"`
en el comando, `dotnet build`/`dotnet publish` terminan en verde sin cambiar
de motor ni tocar el `.csproj` (ver Pregunta 1, Paso 2: 0 errores, 1
advertencia). Lo que **no** queda resuelto por esto es si `dotnet run`
abre ventana de verdad en esta máquina — eso es un problema distinto,
posterior al build, que sí sigue abierto: ver Pregunta 1, Paso 4 (el `.exe`
publicado revienta con excepción `0xC000027B` en `Microsoft.UI.Xaml.dll`
antes de mostrar nada). Para el hito 2: la ruta de `AppxMSBuildToolsPath`
de arriba es de **esta máquina concreta**; en el runner de CI hay que
resolverla con `vswhere` (p. ej. `vswhere -latest -find
"MSBuild\Microsoft\VisualStudio\v*\AppxPackage\"`), no escribirla a mano
como aquí.

## Autorrevisión

- El proyecto **no** se añadió a `WinCueSharp.sln` — verificado con
  `grep -i tools WinCueSharp.sln`, sin resultados.
- El informe distingue explícitamente lo medido (recuento de analizadores,
  confirmado en la tarea 2 sobre un build **completo** vía `dotnet publish`,
  no solo sobre `CoreCompile`) de lo no verificado (ventana visible en
  pantalla) y de lo que sí se ejecutó pero dio un resultado negativo real
  (el `.exe` publicado no arranca en esta máquina — Pregunta 1, Paso 4). Lo
  que en la tarea 1 estaba "bloqueado" (build en verde completo, por el
  componente de VS ausente) ya no lo está: resuelto en la tarea 2, ver
  Pregunta 2 y "Notas del Paso 2" arriba.
- El commit no incluye `bin/` ni `obj/` de `tools/winui3-lab/` — cubiertos
  por las reglas globales `bin/` y `obj/` de `.gitignore`; confirmado que el
  árbol de trabajo estaba limpio de esas carpetas antes del `git add`.
- El `.csproj`, `App.xaml`, `App.xaml.cs`, `MainWindow.xaml` y
  `MainWindow.xaml.cs` commiteados son **literales** al encargo — no llevan
  ninguno de los ajustes de la investigación (`AppxGeneratePriEnabled`,
  versión 1.5.x), que solo se probaron para diagnosticar y no dejaron
  rastro en el árbol final.
- (tarea 3) `HwndHijo.cs` es copia literal del encargo. `MainWindow.xaml.cs`
  lleva dos desvíos sobre el literal, ambos comentados en el propio archivo
  y justificados en la Pregunta 3: recoger el código de `mpv_command` (el
  encargo lo ignoraba) y el disparo automático + sondeo de `width`/`height`
  cuando `LAB_MEDIA` está definida. Nada de eso toca `HwndHijo.Crear` ni el
  orden `wid` → `mpv_initialize` → `loadfile`, que es lo que de verdad
  responde a la pregunta del hito.
- (tarea 3) El commit no incluye la copia de `libmpv-2.dll` (98 MB) dentro
  de `tools/winui3-lab/bin/...` — cubierta por la regla global `bin/` de
  `.gitignore` (y también por `libmpv-lgpl/*.dll`, que es de donde se
  copió). Confirmado con `git status` antes del `git add` que esa carpeta
  no aparece como pendiente de commitear.
- (arreglo, tras revisión) El `_mpv` del botón (Prueba A humana) sigue sin
  llamar a `mpv_terminate_destroy` — decisión deliberada, no descuido: tiene
  que seguir reproduciendo en bucle para que Andrés pueda mirar el airspace
  y arrastrar la ventana entre monitores. Las instancias del control
  negativo (`ProbarConWidAsync`), que sí son de usar y tirar, se destruyen
  cada una antes de pasar a la siguiente — necesario además para que el
  recuento de `EnumChildWindows` de una prueba no arrastre el descendiente
  creado por la anterior.
- (arreglo, tras revisión) `ProbarVideo(this, null!)` se deja igual — el
  revisor lo marcó como "frágil pero inocuo" y coincido: `e` no se usa en
  el cuerpo del método, así que pasar `null!` no tiene efecto observable.

## Dudas para Andrés

1. ~~La máquina de desarrollo no tiene el componente de Visual Studio para
   empaquetado UWP/Appx...~~ **Resuelto** — Andrés instaló el componente
   entre la tarea 1 y la tarea 2. Ver "Notas del Paso 2" y la Pregunta 2
   para la confirmación.
2. Si el runner de CI **sí** trae ese componente (lo normal en
   `windows-latest` de GitHub Actions), este hallazgo afectaría solo al
   desarrollo local, no al hito de "¿compila en CI?" — pero no lo he podido
   comprobar desde aquí. **Sigue sin comprobar** tras la tarea 2; sigue
   fuera de alcance de esta máquina.
3. (tarea 2, nueva) El `.exe` de `pub-carpeta` (ver Pregunta 1, Paso 4)
   arranca el loader pero revienta con excepción `0xC000027B` en
   `Microsoft.UI.Xaml.dll` antes de mostrar nada, en las 4 veces que lo
   lancé como proceso. Detecté que `vcruntime140.dll`/`msvcp140.dll` no
   están en la carpeta publicada, lo que es una hipótesis plausible (no
   confirmada) de la causa. No instalé ni copié nada para probarlo. ¿Quieres
   que investigue esto más a fondo (instalar el "Windows App SDK Runtime" o
   los framework packages VCLibs/UI.Xaml en esta máquina), o lo dejamos
   anotado como hallazgo de entorno y seguimos con las tareas 3/4, que
   también van a necesitar arrancar este tipo de proyecto?
4. (tarea 3, nueva) **Corregido tras revisión** — aquí decía "ninguna duda
   de fondo sobre la pregunta del hito". Eso estaba mal calibrado: la
   revisión encontró, con razón, que la evidencia original (`initialize`/
   `command` = 0, `width`/`height` coincidente con `ffprobe`) **no
   discriminaba** "mpv decodificó hacia nuestro HWND" de "mpv decodificó
   hacia cualquier sitio" — sí había una duda de fondo, y no estaba
   reconocida. Se cerró con un control negativo (ver Pregunta 3): con el
   mismo `width`/`height`/`video-out-params/w`/`current-vo` en los casos
   con y sin `wid` válido, el único dato que sí distinguió los dos casos
   fue `EnumChildWindows` sobre nuestro HWND real (0 descendientes sin
   `wid`, 1 con `wid` = nuestro HWND). Queda una duda menor, de forma, sin
   relación con lo anterior: el `cp` del encargo apuntaba a
   `bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/`, que no existe en
   este proyecto (no hay carpeta `x64/` intermedia); la real es
   `bin/Release/net8.0-windows10.0.19041.0/win-x64/`, y es donde copié la
   DLL. Documentado en la Pregunta 3 para que la tarea equivalente del
   hito 2 no repita el tropiezo.

## PENDIENTE — verificación en pantalla (Andrés)

- [x] Confirmar que, una vez resuelto el punto 1 de arriba (componente de
      Visual Studio para Appx), `dotnet build tools/winui3-lab/WinUi3Lab.csproj -c Release`
      termina en verde de verdad (0 errores). **Confirmado en la tarea 2**
      sin necesidad de ojo humano: `dotnet publish` (Pregunta 1, Paso 2) con
      la misma propiedad termina con 0 errores, 1 advertencia.
- [x] Ejecutar `dotnet run --project tools/winui3-lab/WinUi3Lab.csproj -c Release "-p:AppxMSBuildToolsPath=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage\"`
      y confirmar **a ojo** que aparece una ventana real, con el texto
      "WinUi3Lab" y el botón "Probar mpv en HWND hijo" visibles y legibles
      (tamaño, fuente, que no esté en blanco ni recortada). **Confirmado
      (Andrés):** aparece con el texto "WinUi3Lab" y el botón "Probar mpv
      en HWND hijo", tamaño y fuente normales, nada recortado.
- [ ] (tarea 2, nueva) Hacer doble clic en
      `tools\winui3-lab\pub-carpeta\WinUi3Lab.exe` (tras regenerarlo con el
      comando del Paso 2 de la Pregunta 1: `dotnet publish
      tools/winui3-lab/WinUi3Lab.csproj -c Release -r win-x64
      --self-contained true -o tools/winui3-lab/pub-carpeta
      "-p:AppxMSBuildToolsPath=C:\Program Files (x86)\Microsoft Visual
      Studio\2022\BuildTools\MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage\"`)
      y confirmar si también así se cierra solo / aparece el diálogo de "ha
      dejado de funcionar" — descarta que el cierre con `0xC000027B` visto
      en esta tarea sea un artefacto de lanzarlo sin sesión interactiva en
      vez de con doble clic real.
- [ ] (tarea 2, nueva) Si el punto anterior confirma el cierre también con
      doble clic: decidir si merece la pena instalar en esta máquina el
      redistribuible "Windows App SDK Runtime" (o los framework packages
      VCLibs/Microsoft.UI.Xaml) para descartar la hipótesis de runtime de
      C++ ausente, o si se deja anotado como hallazgo de entorno sin
      bloquear el resto del hito 0.
- [ ] (hallazgo de revisión, hito 0) **`settings.json` desde dentro del
      proceso.** Una vez el `.exe` arranque de verdad (puntos anteriores de
      este mismo epígrafe), lanzarlo, dejar que escriba o reescriba su
      `settings.json` junto al propio ejecutable, y confirmar que lo lee de
      vuelta sin que nada lo redirija. Es la única prueba que cierra la
      Pregunta 1 con medición real y no solo con la propiedad del proyecto
      (`WindowsPackageType=None`) — ver "Paso 4 — arranque en limpio y
      `settings.json`" para el porqué.
- [x] (tarea 3, nueva) **Airspace.** Con la DLL ya copiada en
      `tools\winui3-lab\bin\Release\net8.0-windows10.0.19041.0\win-x64\` (ver
      Pregunta 3 para el porqué de esa ruta y no `bin\x64\Release\...`),
      ejecutar:
      `LAB_MEDIA="C:\Proyectos\ffmpeg-gui\WinCueSharp\tests\show-prueba\video-A-colores.mp4" dotnet run --project tools/winui3-lab/WinUi3Lab.csproj -c Release "-p:AppxMSBuildToolsPath=C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Microsoft/VisualStudio/v17.0/AppxPackage/"`
      y confirmar **a ojo** que el vídeo se ve dentro de la ventana y que
      tapa al `TextBlock` de arriba (el texto de estado) allí donde se
      solapan — es el comportamiento esperado del airspace de una ventana
      hija Win32, no un fallo. **Confirmado (Andrés):** el vídeo se ve —
      barras de color en movimiento con el OSD propio de mpv encima, prueba
      de que mpv es dueño de esa superficie y no que algo enseñe un
      fotograma suelto — y tapa el botón donde se solapa; el texto de
      estado, que va por encima en la disposición, se lee perfectamente.
      Comportamiento correcto; ver la Actualización al final de la
      Pregunta 3.
- [x] (tarea 3, cerrada parcialmente) **Cruce entre monitores.**
      Confirmado (Andrés) que cruzar entre dos monitores **del mismo DPI**
      (96, escalado 100% en los dos, verificado con `GetDpiForMonitor`) no
      descoloca ni escala mal el vídeo: mantiene su posición relativa y su
      tamaño (~640x360). Reveló, sin buscarlo, fricción real al arrastrar
      — ventana pegada, negro momentáneo al cruzar, vídeo cortado y
      recuperado — sin ninguna transición de DPI que la explique (ver la
      Actualización al final de la Pregunta 3). **La prueba con DPI
      distinto de verdad no se pudo hacer** — esta máquina no tiene dos
      monitores a escalados distintos — y esa parte queda sin responder.
- [ ] (hito 2, no de Andrés — requiere código que todavía no existe) La
      casilla original de este punto pedía arrastrar la ventana de salida
      entre monitores con DPI distinto, algo que WinCue **no hace nunca**:
      la ventana de salida se coloca a pantalla completa en el monitor que
      Settings ya tiene elegido y ahí se queda; nadie la arrastra, ni en
      ensayo ni en función. Las preguntas correctas, para el hito 2:
      1. ¿Puede WinUI 3 poner una ventana a **pantalla completa en un
         monitor elegido por índice**, como hace hoy `PlaceOnScreen` en
         `src/WinCue.App/VideoOutputWindow.xaml.cs`?
      2. ¿Se queda ahí **sin perder el always-on-top** ni irse detrás de
         la ventana de control en mitad de un GO?
      3. **¿Qué pasa al cambiar la topología de pantallas con la app
         corriendo** — proyector desenchufado, cambio de resolución,
         monitor dormido? Arrastrar una ventana entre monitores y
         desenchufar un proyector son, para Windows, la misma clase de
         evento; la fricción del punto anterior es un **indicio**, no un
         veredicto, de que la combinación HWND hijo + WinUI 3 podría llevar
         mal esos eventos — en una función real eso no ocurre por
         arrastrar, ocurre porque alguien toca un cable.
