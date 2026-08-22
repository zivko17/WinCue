# Diseño — Secuence: segundo producto sobre WinUI 3

*Escrito 2026-08-22 sobre `pruebas-v2` (f91306d3), a partir de una sesión de
brainstorming con Andrés. Verificado contra el código, no contra el README.*

Este documento **se basta solo**: qué se construye, en qué orden, cómo se
verifica cada hito y dónde se puede parar sin dejar nada roto. El plan de
implementación detallado va aparte.

---

## El problema

WinCue tiene una UI de WPF de **15.191 líneas** que se quiere rehacer en
WinUI 3, por cuatro motivos que Andrés confirmó todos: aspecto Fluent,
`SwapChainPanel` para el compositor D3D11, plataforma con futuro, y mejor
comportamiento en táctil, DPI y multi-monitor.

La petición original era **copiar el proyecto entero y renombrarlo**. Eso
duplicaría también las 22.116 líneas de motor y las 694 pruebas, con dos
compositores D3D11 divergiendo y dos auditorías LGPL que mantener. Este
diseño consigue lo que la petición perseguía —un segundo producto con
identidad propia, sin tocar la beta que ya está en la calle— **sin duplicar
el motor**.

---

## Reglas que gobiernan el plan

1. **La beta no se entera.** WinCue (WPF) sigue siendo lo que se distribuye
   y se vende durante todo el proyecto. Nada de lo que sigue puede ponerla
   en riesgo.
2. **Un solo motor.** Si Secuence necesita algo que hoy vive en
   `WinCue.App`, no se copia: **sube** a una capa compartida y los dos
   productos lo heredan. Copiar código entre los dos productos está
   prohibido.
3. **Secuence nunca implementa `IVideoLayerSurface`.** Esa interfaz está
   moldeada alrededor de `D3DImage`, que no existe en WinUI 3. Si el
   compositor tarda, Secuence espera; no se le hace un provisional.
4. **El guardián de licencias no se puede olvidar.** Se extrae a un script
   compartido que ambos productos invocan.
5. **Nada se da por bueno hasta que se ve en pantalla**, y para la salida de
   vídeo, en pantalla **con un proyector enchufado**.

---

## Lo que NO se toca

- `global.json` — el SDK sigue clavado en 8.0.423.
- `WinCue.Core`, `.Audio`, `.Controller`, `.Video` como `net8.0` puro: el
  motor no aprende qué es WinUI.
- `publish.ps1` — el script del producto 1 se queda como está (salvo la
  extracción del guardián, que es equivalencia funcional verificable).
- La ruta clásica de vídeo (`SetWindowHandleProvider`), que es la que va en
  la beta.
- El régimen de verificación de [plan-compositor.md](../../plan-compositor.md):
  Secuence no lo relaja ni lo duplica.

---

## Hallazgos verificados

Cinco cosas que el README no dice y que condicionan todo el diseño.

**1. El repo git es `C:/Proyectos/ffmpeg-gui`, no `WinCueSharp/`.**
`global.json` y `.github/workflows/ci.yml` viven un nivel por encima. El CI
lo documenta: *"GitHub solo mira `.github/workflows` del primer nivel"*.

**2. El motor no tiene referencias a WPF, pero sí diseño de WPF.**
Esto es lo importante, y contradice la lectura optimista de los `.csproj`:

| Evidencia | Qué revela |
|---|---|
| `MediaEngineContracts.cs:219` — `SetSurface(nint d3d9Surface, …)` | **D3D9** en el contrato compartido. Existe solo porque `D3DImage` no consume otra cosa. |
| `MediaEngineContracts.cs:240` — `SetFrameCopier` | Su contrato documentado es *"DENTRO del `Lock` del D3DImage, justo antes de `AddDirtyRect`"*. Es la API de WPF escrita dentro de `WinCue.Core`. |
| `Gpu/MpvSource.cs:5` | Renderiza a una superficie **D3D9Ex** *"que consume un D3DImage de WPF"*. Igual en `GlDxInterop.cs` y `Gpu/HapRingTarget.cs`. |

**3. Fase 2b ya existe para matar esa ruta.** `Gpu/OutputSinks.cs:14`:
*"no hay D3DImage, ni dirty-rects, ni escritura fuera del Lock"*. El port a
WinUI 3 y el compositor quieren lo mismo. No compiten.

**4. `settings.json` se guarda junto al ejecutable**
(`SettingsManager.cs:3`), no en `%APPDATA%`. Dos consecuencias: renombrar no
rompe la configuración de nadie, y **Secuence tiene que ser desempaquetado**
—nunca MSIX—, porque el directorio de instalación de un MSIX es de solo
lectura.

**5. La capa que se va a reescribir es la única sin pruebas.**

| Proyecto | Tests |
|---|---|
| `WinCue.Controller.Tests` | 377 |
| `WinCue.Video.Tests` | 145 |
| `WinCue.Audio.Tests` | 96 |
| `WinCue.Core.Tests` | 76 |
| **`WinCue.App`** | **0** |

---

## Decisiones tomadas

| Decisión | Elegido | Descartado, y por qué |
|---|---|---|
| Relación con la beta | En paralelo, sin tocarla | Congelar la venta; portar solo la salida |
| Estructura | **Monorepo, dos productos, un motor** | NuGet privado (ceremonia excesiva en solitario); copia entera (dos motores divergiendo) |
| Nombre | **Secuence** | Grafía deliberada, decidida por Andrés tras señalarle que no es ni inglés ni español y que "sequence" colisiona con la categoría *sequencer* |
| Empaquetado | Desempaquetado, obligatorio | MSIX rompe `settings.json` junto al ejecutable |
| Superficie del compositor | `CompositionSink : IOutputSink` en el producto | Tocar `WinCue.Video`, que debe seguir siendo `net8.0` puro |

---

## Sección 1 — Estructura y compartición del motor

```
C:/Proyectos/ffmpeg-gui/            ← raíz git
├── global.json                      ← SDK 8.0.423, intacto
├── .github/workflows/ci.yml         ← se amplía, no se duplica
└── WinCueSharp/
    ├── WinCueSharp.sln              ← ambos productos
    ├── Directory.Build.props        ← + sección condicionada a Secuence
    ├── scripts/verificar-licencias.ps1   ← NUEVO: guardián compartido
    ├── src/
    │   ├── WinCue.Core|Audio|Controller|Video   ← motor, 22.116 LOC
    │   ├── WinCue.Presentation/     ← NUEVO: ~1.044 LOC sin UI
    │   ├── WinCue.App/              ← Producto 1: WinCue (WPF)
    │   └── Secuence.App/            ← Producto 2: WinUI 3
    ├── publish.ps1                  ← Producto 1
    └── publish-secuence.ps1         ← Producto 2
```

`Secuence.App` referencia el motor por `ProjectReference`. Nunca copia código
de `WinCue.App`.

---

## Sección 2 — La superficie de render

Tres rutas de salida de vídeo, tres destinos:

| Ruta | Mecanismo | Estado hoy | En Secuence |
|---|---|---|---|
| **1. Clásica HWND** | mpv `--wid` — `SetWindowHandleProvider` | **Va en la beta** | **Sí.** HWND hijo por P/Invoke, como ya hace `SwapChainHost.cs:40` |
| **2. Compositor D3DImage** | `IVideoLayerSurface` + `CompositorLayerControl : Image` | Vivo; señalado por la auditoría 05 | **Nunca** |
| **3. Compositor swapchain** | `IOutputSink` + `SwapChainSink` | Fase 2b, escrito, **sin conectar** | **Sí, cuando aterrice** |

### El corte

`IOutputSink` (`Gpu/OutputSinks.cs:24`) ya es la abstracción correcta, con
`SwapChainSink` (HWND) y `TextureSink` (sin pantalla). En las 2.382 líneas de
`Gpu/`, lo único hostil a WinUI 3 es una línea:

```
OutputSinks.cs:91   factory.CreateSwapChainForHwnd(gpu.Device, hwnd, desc)
```

WinUI 3 necesita `CreateSwapChainForComposition` +
`ISwapChainPanelNative.SetSwapChain`. Mismo swapchain DXGI, otra puerta.

- **`CompositionSink : IOutputSink`** — clase nueva, ~80 líneas.
- Vive en **`Secuence.App`**, no en `WinCue.Video`: `SwapChainPanel` es un
  tipo de WinUI y el motor sigue `net8.0` puro. El motor expone la interfaz;
  el producto trae la implementación.
- `RenderGraph`, `LayerRenderer`, `FrameRing`, `OutputCompositor` y `RtPool`
  no se enteran.

### Acoplamiento de calendario

Secuence **no depende de Fase 2b para arrancar**: sale con la ruta 1, la
misma que la beta. Vídeo funcionando, sin opacidad ni capas reales. Cuando
Fase 2b aterrice, hereda el compositor añadiendo `CompositionSink`.

### Ganancia gratis

El *airspace* que `SwapChainHost.cs:15` documenta como su peaje —el slate y
el velo de blackout obligados a ser nodos del grafo porque nada de WPF puede
pintar encima de una ventana hija— **desaparece con `SwapChainPanel`**, que
sí es un elemento del árbol visual.

---

## Sección 3 — Orden de port

### Lo que sube (≈1.400 LOC)

A `WinCue.Presentation` (`net8.0`):

| Origen | LOC | Qué es |
|---|---|---|
| `Theme.cs` | 421 | La paleta central. Son colores, no WPF. |
| `CueRowViewModel.cs` | 283 | Lógica de presentación pura |
| `Icons.cs` | 174 | Codepoints de Phosphor (el truco de fuente compuesta se queda en WPF) |
| `UiMetrics.cs` | 113 | Números |
| cadenas de `LocExtension` | 53 | Localización |

A `WinCue.Controller`: **`RemoteInputService.cs` (270)** — es OSC y MIDI,
entrada remota, no interfaz. Que viva en la capa de UI es un accidente.

A `tools/`: `StressRunner.cs` (145).

### Hitos

| Hito | Qué | Aprox. |
|---|---|---|
| **0** | **Pruebas de humo técnicas.** Nada de UI. | 1-2 días |
| **1** | Extracción de las ~1.400 líneas | 3-5 días |
| **2** | **Rebanada vertical: Secuence arranca y hace GO** | 2-3 semanas |
| **3** | Los 7 controles pintados a mano → Win2D (~5.342 LOC) | 4-6 semanas |
| **4** | Inspector (1.746) + Settings (894) | 2-3 semanas |
| **5** | LiveMonitor + `CompositionSink` | según Fase 2b |

Total grueso hasta paridad funcional: **3-4 meses de trabajo efectivo**.
Números de servilleta; el hito 0 puede moverlos en cualquier dirección.

### Hito 0 es un cortafuegos

Tres preguntas. Si alguna falla, se replantea todo antes de gastar un mes:

1. `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`
   sobre un WinUI 3 vacío. **¿Sale un exe?** Decide la distribución entera.
2. WinUI 3 + `ProjectReference` al motor, bajo `global.json` 8.0.423 con
   `AnalysisLevel=latest-all` y `TreatWarningsAsErrors=true`. ¿Compila?
   **¿Cuántas reglas saltan?**
3. HWND hijo bajo un `AppWindow` + mpv `--wid`. ¿Se ve vídeo?

Sale además de aquí: el `THIRD-PARTY-NOTICES.md` de Secuence, con Windows
App SDK y Win2D (ambos MIT) declarados.

### Regla del hito 3

Para cada uno de los siete controles —`TimelinePanel` (2.000),
`EqEditorControl` (1.159), `AudioMixerPanel` (1.126),
`GeometryCanvasControl` (460), `WaveformControl` (353), `VuMeterControl`
(175), `FaderScaleControl` (69)—: **primero se extrae el cálculo** (dónde
caen las marcas, cómo se mapea dB a píxeles, el trazado de la curva del EQ,
el decaimiento del VU) a `WinCue.Presentation` **con pruebas**, y solo
después se pinta en Win2D.

Así el port es "mismo cálculo, otro pincel" en vez de una reinvención, y el
control de WPF puede pasar a usar el cálculo compartido. **WinCue gana
cobertura donde hoy tiene cero.**

---

## Sección 4 — Build, CI y distribución

### Analizadores

El CI compila con `-p:TreatWarningsAsErrors=true` y hoy está **a cero**.
Con `AnalysisLevel=latest-all` y `EnforceCodeStyleInBuild=true`, un proyecto
WinUI 3 **no pasa ese listón recién creado**: el generador de C#/WinRT y el
codegen de XAML disparan reglas CA e IDE.

`Directory.Build.props` gana una sección **condicionada a `Secuence.App`**
que relaja lo que dispara el código generado, sin tocar el listón de los
demás. Cada regla relajada lleva su motivo al lado — no un `<NoWarn>` de cien
códigos que nadie vuelve a mirar.

`timeout-minutes: 25` puede quedarse justo: `Microsoft.WindowsAppSDK` es un
paquete grande. Se sube cuando se vea.

### Target

`Secuence.App` → `net8.0-windows10.0.19041.0`, con `Microsoft.WindowsAppSDK`
y `Microsoft.Graphics.Win2D`. El motor sigue en `net8.0` a secas, que es lo
que permite correr las pruebas sin arrastrar Windows App SDK.

### El guardián de licencias

`publish.ps1:6` lo dice bien: *"una avcodec GPL colada en un instalador no es
un bug de software, es un problema legal"*. Secuence distribuye las mismas
nativas (`libmpv-2.dll`, `ffmpeg-lgpl\bin`) y necesita el mismo guardián.

El fallo más probable de todo este proyecto es que `publish-secuence.ps1`
nazca de un copia-pega, alguien recorte para que corra más rápido, y el
guardián se quede por el camino en silencio. Por eso:

- El guardián sale a **`scripts/verificar-licencias.ps1`**: recibe la carpeta
  de salida y **lanza excepción** si el veredicto no es LGPL.
- Los dos scripts lo invocan. Ninguno reimplementa nada.
- Igual con `THIRD-PARTY-NOTICES.md` y `licenses/`.

### Distribución

Si el hito 0 confirma que WinUI 3 no admite `PublishSingleFile`, Secuence se
distribuye como **carpeta**. El técnico deja de copiar un archivo, y en
directo —donde la gente lleva el software en un USB— eso importa. El ZIP
crece sobre los 142 MB de la beta.

### Mínimo de Windows

WPF corre en cualquier Windows. El Windows App SDK exige **Windows 10 1809**
como mínimo, y con el target 19041 se pide de facto 2004 o superior. Los PC
de cabina de teatro están entre los equipos más viejos y menos actualizados
que existen, precisamente porque funcionan y nadie los toca. **Es un
argumento para que WinCue siga vivo y vendiéndose**: cubre la cola larga que
Secuence deja fuera.

---

## Sección 5 — Cómo se verifica

### Lo que Secuence hereda gratis

Los 694 tests están **todos en el motor**, y Secuence comparte motor: viene
con la suite entera detrás sin escribir un test. Con la copia entera habría
dos suites que mantener.

### El hito 1 es el único que toca el producto vivo

- Va **primero**, con la UI de WPF delante, cuando un fallo se ve en tres
  segundos.
- `Theme`, `UiMetrics`, `Icons`, `CueRowViewModel` son datos y lógica pura:
  si compilan, están.
- **`RemoteInputService` pide cuidado**: se va a `WinCue.Controller`, donde
  hay 377 tests que van a opinar. Commit propio, verificación propia con OSC
  y MIDI reales. No en el commit de los colores.

### `--smoke` desde el hito 2

El patrón existe (`App.xaml.cs:10`): arranca, se cierra solo a los 3 s, sin
diálogos modales; y `--stress <show.json> [--stress-loops N]` corre
desatendido. Secuence necesita ambos **desde el hito 2**. Un producto de
directo que no se puede arrancar desatendido no se puede verificar de forma
repetible.

### Puertas de aceptación

| Hito | Verde cuando |
|---|---|
| **0** | Las tres preguntas contestadas por escrito. Cuenta de warnings anotada. Veredicto de single-file tomado. |
| **1** | 694 tests verdes + CI verde + **WinCue abre un show real y hace GO**, con OSC y MIDI funcionando |
| **2** | Un show sencillo de verdad en Secuence: cargar, GO, STOP ALL, PANIC, vídeo en la segunda pantalla, guardar |
| **3** | Cada control: pruebas del cálculo + comparación **lado a lado** con el de WPF |
| **4** | Todos los campos del inspector y las 5 pestañas de settings contra el mismo `settings.json` |
| **5** | El régimen de `plan-compositor.md` tal cual: paridad, genlock, soak de 4 h, verificación visual, **F2 antes que F3** |

### Lo que no coge ningún test

El CI ya declara sus límites: no cubre la media, ni las DLL nativas, ni el
smoke, ni el guardián. Secuence añade una categoría nueva que **no se ve
trabajando en el escritorio**:

**El comportamiento de ventana de WinUI 3 con un proyector enchufado.** Foco
que se pierde, always-on-top que no se respeta, el cambio de DPI al arrastrar
entre el portátil y la pantalla grande, salir de fullscreen sin querer, la
salida que se va detrás de la ventana de control en mitad de un GO. Ahí es
donde WinUI 3 arrastra peor fama que WPF, y es lo que mata un show.

Lo único que lo coge es **soak con dos pantallas reales y un proyector**,
horas, no el portátil solo. Entra como criterio del hito 2 y se repite en
cada hito posterior: una regresión de ventana aparece por sorpresa al tocar
cualquier otra cosa.

---

## Riesgos, por orden

1. **WinUI 3 no admite `PublishSingleFile`.** Cambia la historia de
   distribución de un producto que hoy es un exe que se copia en un USB.
   *Mitigación: hito 0, antes de nada.* **Sin verificar** — es la limitación
   documentada conocida, pero el Windows App SDK se mueve rápido.
2. **Comportamiento de ventana en directo.** Sin cobertura posible por
   pruebas. *Mitigación: soak con proyector desde el hito 2.*
3. **Ruido de analizadores.** Podría obligar a relajar el listón que hoy
   protege todo el repo. *Mitigación: se cuenta en el hito 0 y se decide con
   el número delante.*
4. **El hito 1 rompe la beta.** *Mitigación: va primero, por commits
   separados, con los 694 tests y el CI.*
5. **Los 7 controles Win2D son el 35 % del trabajo** y no tienen atajo.
   *Mitigación: extraer el cálculo con pruebas antes de pintar.*
6. **Deriva entre los dos productos.** El riesgo de fondo de todo el
   enfoque. *Mitigación: la regla 2 — copiar código entre productos está
   prohibido; lo compartido sube.*
7. **Secuence estrecha el parque de Windows soportado.** *Mitigación:
   WinCue sigue vendiéndose para la cola larga.*

---

## Decisiones abiertas (de Andrés)

1. **¿Qué es Secuence como producto?** ¿El hermano mayor de teatro, o el
   pequeño accesible? No bloquea las secciones 1-5, que son fontanería.
   **Bloquea el diseño de las pantallas**, que es conversación aparte y debe
   ocurrir antes del hito 2.
2. **Nombres de ensamblado.** En la carpeta de Secuence se verán
   `WinCue.Core.dll`, `WinCue.Audio.dll`, `WinCue.Video.dll`. O se acepta
   (coste cero), o se renombra a un prefijo neutro ahora que hay un solo
   consumidor (mecánico, cubierto por el CI, pero es churn sobre el producto
   que se vende). **Se decide en el hito 1.** Por defecto: se acepta.
3. **Grafía "Secuence".** Decidida y cerrada, registrada aquí por si alguien
   la lee dentro de un año y la toma por una errata.
4. **Comprobar disponibilidad del nombre**: dominio `.app`/`.com`, EUIPO
   clase 9, colisión en el nicho de show control, handles. No verificado en
   esta sesión — sin acceso a red.

---

## Dónde se puede parar

- **Tras el hito 0**: se ha gastado un par de días y se sabe si el proyecto
  es viable. Si el single-file es un tapón inaceptable, se para aquí y no se
  ha perdido nada.
- **Tras el hito 1**: WinCue queda **mejor** que antes (1.400 líneas
  colocadas donde tocan, `RemoteInputService` en su capa) aunque Secuence no
  se escriba nunca. Este hito se paga solo.
- **Tras el hito 2**: hay una app que hace shows sencillos. Se decide con
  ella delante, no con este documento. **Es el punto de no retorno, puesto a
  propósito donde darse la vuelta sigue siendo barato.**
- **Tras el hito 3 o 4**: Secuence es utilizable pero incompleto. Se puede
  aparcar sin dejar a WinCue tocado, porque nunca se tocó.
