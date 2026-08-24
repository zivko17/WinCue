# Traspaso — Secuence (segundo producto sobre WinUI 3)

*Escrito 2026-08-23 al agotarse el contexto de la conversación que hizo este
trabajo. Sirve para continuar en una sesión nueva sin haber estado en la
anterior.*

*Puesto al día el 2026-08-24 por la primera sesión LOCAL, que sí podía
ejecutar y MIRAR los dos productos. Cambian el estado, la lista de lo hecho,
la de lo que falta y la de trampas de WinUI 3. Para arrancar una sesión en la
máquina de Andrés, empieza por `ARRANQUE-SESION-LOCAL.md`, que trae además
cómo capturar la pantalla y cómo pilotar la interfaz sin manos.*

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

*(Puesto al día el 2026-08-24 al final de la primera sesión LOCAL. Los
números de antes —56 commits, 1.057 pruebas— eran los del día anterior.)*

Rama **`claude/secuence-hitos-0-1-1occ8p`**, salida de `pruebas-v2` @
`6caa1580`. **86 commits**, árbol limpio y **subida a `origin`**, con el CI en
verde sobre `windows-latest`.

**Ojo con el nombre de la rama:** `secuence/hitos-0-1` se quedó atrás. La
buena es la de `claude/…`, que es la que escucha el CI (`claude/**`).

| | |
|---|---|
| Pruebas | **1.160** (se partió de 752) |
| Build | 0 avisos, 0 errores con `TreatWarningsAsErrors=true` |
| Guardián de paleta | OK — 74 asignaciones, 94 constantes, 20 alias |
| `WinCue.App` | 12.312 líneas de C# + 1.338 de XAML |
| `Secuence.App` | 9.639 líneas de C# + 917 de XAML |
| `WinCue.Presentation` | 3.643 líneas · **366 pruebas propias** |

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

**`WinCue.Presentation`** (3.643 líneas, 366 pruebas propias) contiene:
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

Añadido el 2026-08-24, todo con pruebas y con **WinCue pasando a usarlo**:

- **`SplitterDrag`** — hasta dónde llega el arrastre de un separador. WPF trae
  `GridSplitter` y WinUI 3 no, así que Secuence arrastra a mano, pero **dónde
  para** no puede ser a mano en cada sitio.
- **`HotkeyName`** — los alias de tecla heredados de Qt (`Esc`, `Return`) que
  traen los shows guardados por la versión Python. Convertir el nombre en
  tecla NO se comparte: WPF usa `Key` y WinUI 3 `VirtualKey`.
- **`TimelineLayout.SpanLabelText(span, withIcon)`** — el rótulo del asa de un
  grupo, con o sin su glifo (ver abajo por qué Secuence lo pide sin él).

Y en **`WinCue.Audio`**, que no es presentación pero también se comparte:
**`MediaProbe`** — la duración por `ffprobe`, que era una copia privada e
idéntica en cada producto. Partida en dos: `ParseDuration` es pura y tiene
pruebas, `DurationSeconds` lanza el proceso y no se puede probar sin ffprobe
instalado.

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
  El `.csproj` de Secuence autorresuelve la ruta **en un puesto con VS 2022**,
  pero **en el runner de CI no**: la imagen trae **Visual Studio 18**
  (`...\Visual Studio\18\Enterprise\...\v18.0\AppxPackage`) y ninguna de
  las cuatro sondas fijas casa. Verificado el 2026-08-23 (run #5 en rojo). El
  `ci.yml` lo resuelve con **`vswhere`** y exporta `AppxMSBuildToolsPath` como
  variable de entorno — MSBuild trata el entorno como propiedad, así que la
  condición del `.csproj` la deja ganar.
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
- **Un `ContentDialog` se ajusta a su contenido** («is sized to its content by
  default», doc de `ContentDialog.FullSizeDesired`). Con el alto libre, un
  diálogo con pestañas **cambia de tamaño al cambiar de pestaña** y los
  botones se mueven bajo el ratón. Se arregla con un alto **fijo** (no
  `MaxHeight`), y que el scroll lo ponga cada pestaña. El alto no se clava a
  un número: se deduce de `XamlRoot.Size.Height` con topes, porque un monitor
  de 1024×600 no admite lo mismo que uno de 1080 — ver
  `WinCue.Presentation.SettingsDialogMetrics`, que es donde esa cuenta tiene
  pruebas.
- **Un `ContentControl` NO estira su contenido**: en WinUI 3 el valor por
  defecto de `Control` es `VerticalContentAlignment=Top` y
  `HorizontalContentAlignment=Left` (no `Stretch`, como es fácil suponer
  viniendo de WPF). Mientras el contenedor se ajusta al contenido no se nota;
  en cuanto le das un tamaño fijo, lo de dentro se queda flotando en una
  esquina. Hay que ponerlos a `Stretch` a mano.
- **WinUI 3 no trae `TabControl` retemplable ni `GridSplitter`** de serie. El
  `GridSplitter` ya está resuelto: `Secuence.App.PaneSplitter` (control) +
  `WinCue.Presentation.SplitterDrag` (los topes, con pruebas).
- **`Border` está SELLADO**, igual que `CanvasControl`. `PaneSplitter` hereda
  de `Grid`, que sí se puede heredar, pinta su `Background` y con él hace
  hit-test. Y al no ser un `Control`, los eventos de puntero **se enganchan**
  en vez de sobrescribirse: `OnPointerPressed` y compañía son virtuales en
  `Control`, no en `UIElement`. (`ProtectedCursor`, en cambio, sí está en
  `UIElement` — por eso el separador tiene que ser una clase y no un `Grid`
  suelto con manejadores puestos desde fuera.)
- **Los estilos por defecto traen MÍNIMOS que se comen los anchos que pides:**
  `TextBox` lleva `MinWidth = 64` (`TextControlThemeMinWidth`) y `CheckBox`
  lleva `MinWidth = 120` (`CheckBoxMinWidth`). Las cajas de banda de la EQ son
  de 38, 42 y 52 y **las tres se pintaban a 64**, del mismo ancho; y una
  casilla con la palabra «HPF» reservaba 120 px y abría un hueco enorme antes
  de su campo. En WPF no existe ninguno de los dos. Es la misma familia que el
  `MinHeight` de la barra de trim: **si no lo anulas a 0, gana el del
  control**. Cuando un ancho «no se aplica», sospecha de un mínimo del tema
  antes que de tu código.
- **El relleno por defecto también es otro**: `Padding` (6,4) en WinUI contra
  (4,2) en WPF. Con el de WinUI las filas del inspector salían más altas y el
  panel entero se leía más suelto que el de WinCue.

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

Y lo añadido el **2026-08-24**, en la primera sesión LOCAL (verificado en
pantalla por la propia sesión, que sí podía mirar — ver
`ARRANQUE-SESION-LOCAL.md`):

- **Los dos separadores arrastrables**: columna izquierda (455 / 200 / 760) y
  panel de pestañas (330 / 140 / 900). El alto del panel se guarda
  (`bottom_pane_height`) y el ancho de la columna no — copiado de WinCue,
  incluida esa asimetría.
- **Cabecera del timeline**: rótulo, pista de uso, casilla del crossfade
  automático (que ya persiste) y los tres botones de zoom. Más los números de
  pista al margen.
- **Recuadros de grupo y marcadores GO** en el timeline, y los grupos ya se
  **agarran por su asa** (asa → bloque → cuerpo, la prioridad de WinCue).
- **Agrupar con `Ctrl+G`.** Antes de esto, en Secuence no había forma de meter
  un cue dentro de un grupo: se podía crear el GROUP y nada más.
- **Menú contextual de la tabla** con las acciones de WinCue.
- **Cursor de edición del timeline**: se coloca pinchando en la regla, se
  arrastra, y con doble clic reproduce el show desde ahí — lo que cruza el
  cursor arranca a mitad y lo posterior se programa con su retraso, hasta el
  próximo GO manual.
- **Barra de estado con historial**: el mensaje se borra solo a los 4 s y
  queda apuntado; el boton lo abre y se enciende en ambar si el mensaje era
  un aviso (WinCue.Presentation.StatusLog decide que cuenta como tal).
- **Soltar archivos del Explorador sobre la tabla** — alta por el mismo
  camino que el botón IMPORTAR. En modo SHOW se rechaza ya en el `DragOver`,
  para que el cursor lo diga antes de soltar.
- **Selección múltiple** en la tabla (`Extended`: Ctrl suma, Mayús extiende),
  con agrupar / copiar / duplicar / borrar operando sobre el bloque entero.
  El inspector enseña la fila que se ACABA de tocar, no la primera: al
  extender con Mayús hacia arriba, saltaría a un cue que nadie ha señalado.
- **Hotkeys por cue**: el campo ya dispara, y se calla mientras se escribe.
- **Barra de trim también en cues de VÍDEO** (sin onda, como WinCue).
- Las dos columnas que faltaban en la tabla: banderín y ✕ (desarmado).
- El inspector cuadrado con el de WinCue en anchos, altos y disposición del
  rack de audio.

## Lo que falta

**Inmediato** (revisado el 2026-08-24; lo que estaba aquí y ya está hecho se
ha movido a la lista de arriba):

- **Reordenar filas arrastrándolas** dentro de la tabla. Es la OTRA mitad del
  `Drop` de WinCue (formato `"WinCueRow"` → `OnMoveCue`); soltar archivos ya
  está. En Secuence se reordena desde el timeline, no desde la tabla.
- **El SCRUB del cursor de edición no se ve.** El cursor ya está y emite
  `EditScrubbed`, pero no lo escucha nadie: Secuence no tiene visor de
  EDICIÓN separado del de salida (`LiveMonitorControl` no trae `ShowPaused`
  — ver su cabecera). Cuando llegue, es UNA línea en MainWindow.
- **«Normalizar sonoridad»** en el menú contextual. En WinCue es un método
  PRIVADO de su ventana, no del controlador: copiarlo es justo lo que prohíbe
  la regla 2, así que primero hay que subirlo a compartido.
- **Etiquetas de bloque sin sus prefijos** ↗ / ∞ / 📌, y el marcador GO sin su
  ▸. No es descuido: Win2D/DirectWrite no resuelve la fuente de iconos, ni
  por nombre ni con la cadena `ms-appx://…#Phosphor` que sí vale en XAML.
  Probado: `DrawText` lanza `ArgumentException`. Si algún día hace falta,
  la salida es cargar la fuente como recurso de Win2D, no insistir por
  nombre.

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
| `docs/superpowers/ARRANQUE-SESION-LOCAL.md` | **Empieza por aquí si la sesión corre en la máquina de Andrés**: compilar, lanzar, capturar la pantalla, pilotar la interfaz y las trampas ya pagadas |
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

### Lo que añadió la sesión LOCAL del 2026-08-24

**Mirar encuentra lo que auditar no.** La auditoría remota había dado los 55
campos del inspector por equivalentes, y lo eran uno a uno. Lo que no se ve
leyendo dos ficheros en paralelo es que los `MinWidth` por defecto del tema de
WinUI descuadraban TODOS los anchos, que el rack de audio dejaba mil píxeles
en negro, o que la cabecera de la barra de trim se pintaba dos veces.

**Y arreglar una cosa destapa la siguiente.** Hacer arrastrable el separador
sacó a la luz la cabecera duplicada, que llevaba ahí desde siempre y no se
veía porque el panel se quedaba corto. Ir a verificar los recuadros de grupo
destapó que no había forma de crear un grupo con hijos. Verificar los hotkeys
destapó que la tabla perdía la selección en cada edición. **Cuando algo deja
de estar tapado, vuelve a mirarlo entero.**

**Cuidado con culpar al código de los fallos de tu automatización.** Tres
veces di por roto algo que funcionaba: un botón `Ajustar` que no respondía a
un clic mal puesto, un separador que no se arrastraba porque `SetCursorPos`
no inyecta eventos de puntero, y un manejador de trim al que apunté 17 px por
debajo. **Repite la prueba antes de tocar nada** — y si sospechas de un
cambio tuyo, busca una prueba de control que NO pase por él.
