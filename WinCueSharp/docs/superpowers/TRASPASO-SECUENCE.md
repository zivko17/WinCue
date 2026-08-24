# Traspaso — Secuence (segundo producto sobre WinUI 3)

*Escrito 2026-08-23 al agotarse el contexto de la conversación que hizo este
trabajo. Sirve para continuar en una sesión nueva sin haber estado en la
anterior.*

---

## Qué es esto en una frase

**WinCue** (reproductor de cues para directo estilo QLab, C#/.NET 8, UI en
WPF, **en beta pública y vendiéndose**) tiene ahora un hermano: **Secuence**,
mismo motor, UI en **WinUI 3**, orientado a **teatro profesional** — denso,
para quien vive en la consola. Andrés quiere multiviewer y timecode en él, a
futuro.

**La grafía "Secuence" es deliberada, no una errata. No la corrijas.**

---

## Estado a día de hoy

Rama **`secuence/hitos-0-1`**, salida de `pruebas-v2` @ `6caa1580`.
**56 commits**, árbol limpio, todo commiteado. Sin push (nadie lo ha pedido).

| | |
|---|---|
| Pruebas | **1.057** (se partió de 752) |
| Build | 0 avisos, 0 errores con `TreatWarningsAsErrors=true` |
| `WinCue.App` | 14.073 líneas (eran 15.191) — **−1.126 en toda la rama** |
| `Secuence.App` | ~5.800 líneas |

---

## La estructura

```
C:/Proyectos/ffmpeg-gui/          ← RAÍZ GIT (ojo: no es WinCueSharp/)
├── global.json                    ← SDK clavado en 8.0.423. NO SE TOCA
├── .github/workflows/ci.yml       ← el CI vive aquí, no dentro de WinCueSharp/
└── WinCueSharp/
    ├── scripts/verificar-licencias.ps1   ← guardián LGPL compartido
    ├── scripts/verificar-paleta.ps1      ← guardián de la paleta (en el CI)
    └── src/
        ├── WinCue.Core|Audio|Controller|Video   ← motor, net8.0 puro
        ├── WinCue.Presentation/   ← COMPARTIDO: cálculos sin framework
        ├── WinCue.Remote/         ← OSC + MIDI (NAudio), fuera de la UI
        ├── WinCue.App/            ← Producto 1: WinCue (WPF)
        └── Secuence.App/          ← Producto 2: Secuence (WinUI 3)
```

**`WinCue.Presentation`** (~2.700 líneas, 215 pruebas propias) contiene:
`Palette` (94 colores), `Icons` (59 glifos), `UiMetrics` (16 medidas),
`BevelVeil`/`BevelPalette` (el relieve), `CueTimeField`, `MonitorClockFormat`,
y el cálculo de los siete controles: `VuMeterLayout`, `TimelineLayout`,
`MixerLayout`, `WaveformLayout`, `GeometryLayout`, `EqCurveLayout`,
`CompCurveLayout`, la interacción del timeline: `TimelineViewport`
(zoom/scroll), `TimelineHitTest`/`TimelineDragGesture` (agarrar y arrastrar)
y `TimelineEditing` (qué escribe el soltar); y `CueChoices` — las opciones de
los desplegables del inspector y la paleta de colores de etiqueta, con
pruebas que exigen que cada valor de cada enum tenga su etiqueta (si añades
un modo a un enum de Core y no lo etiquetas, el CI se pone rojo en vez de
dejar a un producto sin ofrecerlo). (`EqResponse`, los biquads RBJ
que alimentan la curva de EQ, vive en `WinCue.Core` — matemática de audio,
no presentación.)

---

## LAS REGLAS. No las rompas

1. **WinCue no puede cambiar de comportamiento ni de aspecto.** Está
   vendiéndose. Andrés ha verificado control a control en pantalla que sigue
   igual.
2. **Prohibido copiar código entre los dos productos.** Si Secuence necesita
   algo que vive en `WinCue.App` y no es de framework, **sube a
   `WinCue.Presentation` con pruebas** y lo usan los dos. Si es de framework,
   se reescribe.
3. **El patrón de los controles pintados a mano**, validado en los siete:
   *primero se extrae el cálculo a `Presentation` con pruebas, después se
   pinta*. Y **WinCue pasa a usar ese mismo cálculo**, para que no puedan
   divergir.
4. **El formato JSON de los shows es sagrado.** Compatible incluso con los
   shows de la versión Python; hay usuarios con shows guardados. Todo pasa
   por `CueModel.ToJson/FromJson`. Nunca escribas serialización propia.
5. **`settings.json` va junto al ejecutable**, no en `%APPDATA%`. Por eso
   Secuence **tiene que ser desempaquetado** (`WindowsPackageType=None`),
   nunca MSIX.
6. **Los subagentes no ven pantallas.** Todo lo visual va a un epígrafe
   `## PENDIENTE — verificación en pantalla (Andrés)` en su informe, con el
   comando exacto y qué mirar. Andrés lo comprueba y responde.

---

## Trampas de WinUI 3 ya descubiertas y pagadas

Estas costaron tiempo. **No las redescubras:**

- **`AppxMSBuildToolsPath`** — `MrtCore.PriGen.targets` busca sus tareas bajo
  `$(MSBuildExtensionsPath)`, y con `dotnet` eso apunta al SDK de .NET donde
  no están. Hizo falta instalar la carga
  `Microsoft.VisualStudio.Workload.UniversalBuildTools` en Build Tools 2022.
  El `.csproj` de Secuence **ya autorresuelve la ruta**. **Sin verificar en el
  runner de CI** — primer sospechoso si un push sale rojo.
- **`ms-appx:///` SÍ resuelve en apps desempaquetadas.** `file:///` **no** lo
  admite `FontFamily`, y **falla en silencio** (interfaz llena de cuadritos).
  La fuente de iconos usa `ms-appx:///assets/Phosphor.ttf#Phosphor`.
- **Los selectores de archivo LANZAN** sin
  `WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd)`.
- **`CanvasControl` (Win2D) está sellada** — hay que componer
  (`UserControl` + `CanvasControl`), no heredar.
- **CA5392** ya está apagada en `.editorconfig`, pero **un `.editorconfig`
  nunca alcanza un `.cs` inyectado desde la caché de NuGet** — hace falta
  `<NoWarn>` en el `.csproj`.
- **No todo lo que WPF pinta a mano hay que pintarlo**: el fader del mixer
  resultó ser un `Slider` nativo. **Mira siempre si hay equivalente nativo
  antes de pintar.**
- **WinUI 3 no trae `TabControl` retemplable ni `GridSplitter`** de serie.

## Trampas de licencia — el producto se distribuye CERRADO

Tres sorpresas, las tres por abrir el paquete en vez de fiarse del repo:

- **`Microsoft.WindowsAppSDK` NO es MIT.** Su `.nuspec` declara
  `<license type="file">license.txt</license>` con
  `requireLicenseAcceptance=true`, y ese archivo son los términos propietarios
  de Microsoft. El **repositorio** de GitHub sí es MIT; el **paquete binario**
  no. Documentado en `docs/legal/notices-secuence.md`.
- **`Microsoft.Graphics.Win2D` 1.2.0 no trae texto de licencia dentro** —
  solo un `licenseUrl` por HTTP de 2014 (Web Platform Installer, retirado).
  **PENDIENTE: probar una versión reciente**, que probablemente declare MIT
  limpiamente.
- **`Microsoft.Windows.SDK.BuildTools`** — no se distribuye, sin obligación de
  aviso; su EULA no se pudo leer. Anotado como *no verificado*, no supuesto.

**Regla:** ninguna licencia entra a un documento sin abrir el `.nuspec`.

---

## Lo que Secuence hace ya

Todo verificado por Andrés en pantalla, paso a paso:

- Tabla de cues con colores por tipo, iconos y sangría de grupos
- Transport: GO / PAUSE / STOP ALL / FADE ALL / PANIC, countdown, playhead,
  panel de activos. **GO suena de verdad**
- Salida de vídeo a pantalla completa en el monitor elegido, con PANIC y
  blackout. mpv en un HWND hijo (ruta clásica, no compositor)
- Abrir / Guardar / Guardar como, asterisco de modificado, aviso de cambios
- Inspector **completo** (dos pasadas): identidad con color/armado/flag/notas,
  destino (cue objetivo, modo de grupo, «parar los demás»), tiempos,
  encadenado, fundidos con el aviso de fundido incoherente, reproducción con
  velocidad y tono, rack de audio, geometría, y los **disparadores** (hotkey,
  retrigger, «fundir y parar otros», wall-clock y MIDI)
- **Los siete controles pintados a mano en Win2D**: VU, timeline
  (interactivo: seleccionar, arrastrar en tiempo/pista/bordes, zoom con
  `Ctrl+rueda` — pendiente de verificación en pantalla), mixer + escala de
  faders, waveform (con trim arrastrable), canvas de geometría (**conectado a
  la salida en vivo**), EQ y compresor
- Disposición igual a la de WinCue, con el mismo lenguaje de relieve
- Previos **PGM/PVW en vivo** (mpv embebido)
- Añadir / Importar / Borrar, modo EDIT-SHOW, conmutador de salida, SIGUIENTE
- Ajustes: **las cinco pestañas** — Vídeo, Audio, Red/Control, Interfaz, Rutas
- **Disparo remoto por OSC y MIDI**, con ping de prueba y MIDI Learn

## Lo que falta

**Inmediato:**
- **Hotkeys por cue en Secuence** — el inspector ya guarda el campo `hotkey` y
  WinCue lo respeta, pero `RebuildHotkeys` de Secuence solo cubre Espacio/Esc:
  falta construir los atajos por cue. El campo lo avisa en pantalla.
- **Timeline: lo que queda tras la segunda pasada.** Arrastrar bloques
  (tiempo/pista/bordes con sus topes), zoom con `Ctrl+rueda`, scroll y
  selección bidireccional con la tabla YA están — el cálculo es compartido y
  probado (`TimelineViewport`/`TimelineHitTest`/`TimelineDragGesture`/
  `TimelineEditing` en WinCue.Presentation; WinCue.App pasó a usarlo). Falta:
  pintar los recuadros de grupo y los marcadores GO en Secuence (y con ello
  agarrar grupos), el cursor de edición con scrub y doble clic, y la casilla
  del crossfade automático (lee `timeline_auto_crossfade` de settings; sin
  interfaz propia todavía) — todo eso va con la pasada del menú contextual.
- Menú contextual de la tabla, arrastrar y soltar archivos, barra de estado,
  atajos más allá de Espacio/Esc

**Anotado y sin hacer:**
- **`<AssemblyName>WinCue</AssemblyName>`** — el ejecutable se llama
  `WinCue.App.exe` de cara al cliente. Cambiarlo rompería accesos directos de
  quien ya tenga la beta.
- **Multiviewer y timecode**, que es lo que Andrés quiere de Secuence:
  - **Timecode ya existe a medias**: `Cue.TimecodeEnabled/TimecodeValue`,
    `RemoteRouter.OnTimecode()` que dispara al cruzar la marca, MTC por
    quarter-frame. **Falta el *chase*** (que el playhead siga al timecode) y
    **LTC** (timecode por audio, que es lo que da un teatro).
  - **El multiviewer ES la fase 2b.** No se puede mostrar N salidas sin
    renderizarlas a texturas. Contradice lo que dice el spec sobre que
    Secuence no depende del compositor.
- Verificación de **DPI distinto entre monitores** — nunca se hizo: los dos
  monitores de Andrés están al 100 %.

---

## Cómo lanzar las cosas

```powershell
# Secuence
$exe = Get-ChildItem "src\Secuence.App\bin" -Recurse -Filter "Secuence.exe" |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
Start-Process $exe.FullName -ArgumentList "tests\show-prueba\show-prueba.json"

# WinCue
Start-Process "src\WinCue.App\bin\Release\net8.0-windows\WinCue.App.exe" `
              -ArgumentList "tests\show-prueba\show-prueba.json"
```

Pruebas y build:

```powershell
dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true
dotnet test WinCueSharp.sln -c Release --filter "Requiere!=libmpv"
pwsh -File scripts\verificar-paleta.ps1
```

**Prueba intermitente conocida:**
`WinCue.Video.Tests.MpvPropertyTests.FadeVolume_ReachesTarget` falla a veces
por temporización bajo carga. **Pasa en solitario y al repetir.** No está
rota. Cualquier OTRA en rojo sí es señal.

**El entorno de Andrés:** dos monitores, 1920×1080 y 1024×600, **ambos al
100 %**. La salida de vídeo se configura por índice (`video_output_screen`).

---

## Documentos que conviene leer

| Documento | Para qué |
|---|---|
| `docs/superpowers/specs/2026-08-22-secuence-winui3-design.md` | El diseño aprobado. Las reglas salen de aquí |
| `docs/superpowers/plans/2026-08-22-secuence-hitos-0-y-1.md` | El plan de los hitos 0 y 1 |
| `tools/winui3-lab/INFORME.md` | El hito 0: por qué WinUI 3 es viable |
| `.superpowers/sdd/2026-08-22-secuence-hitos-0-y-1/progress.md` | El ledger: todas las decisiones y por qué |
| `.superpowers/sdd/.../secuence-shell-report.md` | Los informes de cada tarea de Secuence |
| `docs/plan-compositor.md` | La fase 2b, escrita y sin conectar |
| `docs/legal/checklist-distribucion.md` | Lo legal antes de distribuir |

Los dos últimos de `.superpowers/` **no están versionados** (ese directorio
tiene su propio `.gitignore`). Si desaparecen, la historia está en `git log`.

---

## Cómo se ha trabajado, por si se quiere seguir igual

Un agente por tarea, con encargo escrito; **revisión independiente de cada
una** contra el diff; y Andrés verificando en pantalla lo que ninguna máquina
puede ver. Las revisiones encontraron cosas reales — tres casos de
"medición correcta, conclusión inflada" y un hueco legal — así que merecen la
pena.

**Y el dato más útil de todos:** en los dos hitos, **los defectos estuvieron
en el plan, no en la ejecución**. Doce supuestos falsos escritos por quien
planificaba —`mpv.exe` en el PATH, el Windows App SDK siendo MIT, el producto
arrastrando ventanas entre monitores, una prueba del guardián que no probaba
nada, un script con un error de sintaxis de PowerShell— y ninguno puesto por
un agente. **Desconfía del plan antes que del ejecutor.**
