# Traspaso — Secuence (segundo producto sobre WinUI 3)

*Escrito 2026-08-23 al agotarse el contexto de la conversación que hizo este
trabajo. Sirve para continuar en una sesión nueva sin haber estado en la
anterior.*

*Puesto al día el 2026-08-24 por la primera sesión LOCAL, que sí podía
ejecutar y MIRAR los dos productos. Cambian el estado, la lista de lo hecho,
la de lo que falta y la de trampas de WinUI 3. Para arrancar una sesión en la
máquina de Andrés, empieza por `ARRANQUE-SESION-LOCAL.md`, que trae además
cómo capturar la pantalla y cómo pilotar la interfaz sin manos.*

*Puesto al día el 2026-08-25 tras la tanda del editor de canvas y el
compositor GPU (`.superpowers/sdd/2026-08-25-secuence-canvas-compositor/`).
Secuence tiene por primera vez opacidad real entre pistas, crossfades de
vídeo y un PGM que espeja de verdad lo que sale a sala — nada de esto había
funcionado antes en este producto. Cambian el estado, «Lo que Secuence hace
ya», «Lo que falta» y las trampas. Y hay una **pregunta abierta para Andrés**
justo debajo del estado: léela antes que el resto.*

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

*(Puesto al día el 2026-08-25 tras la tanda del editor de canvas y el
compositor GPU. Los números de antes —86 commits, 1.160 pruebas— eran los de
la tanda anterior.)*

Rama **`claude/secuence-hitos-0-1-1occ8p`**, salida de `pruebas-v2` @
`6caa1580`. **124 commits**, árbol limpio, CI en verde sobre `windows-latest`
en la última pasada. **Ojo: local va 28 commits por delante de `origin` — el
push de esta tanda queda pendiente, es decisión de Andrés, no la hagas tú
sin que te lo pida.**

**Ojo con el nombre de la rama:** `secuence/hitos-0-1` se quedó atrás. La
buena es la de `claude/…`, que es la que escucha el CI (`claude/**`).

| | |
|---|---|
| Pruebas | **1.227** (se partió de 1.194 al empezar esta tanda) |
| Build | 0 avisos, 0 errores con `TreatWarningsAsErrors=true` |
| Guardián de paleta | OK — 74 asignaciones, 94 constantes, 20 alias |
| `WinCue.App` | 15.352 líneas de C# + 1.338 de XAML |
| `Secuence.App` | 12.447 líneas de C# + 952 de XAML |
| `WinCue.Presentation` | 4.125 líneas · **409 pruebas propias** |

---

## PREGUNTA ABIERTA PARA ANDRÉS — mira esto antes que nada

La Task 8 (el PGM que espeja el compositor) midió algo que **contradice a
este mismo documento**: en la máquina donde se ejecutó, el hueco del PGM sale
NEGRO — con el compositor encendido **y** con el compositor APAGADO (la rama
clásica de mpv, la de siempre). En las DOS ramas, no solo en la nueva.

La causa medida: el puente XAML de WinUI 3
(`Microsoft.UI.Content.DesktopChildSiteBridge`) queda **por encima** de los
HWND hijos en el orden Z y los tapa — al revés de lo que dice la cabecera de
`HwndHijo.cs` («esta ventana se pinta SIEMPRE por encima del XAML de su
padre, confirmado a ojo por Andrés en el hito 0»), y al revés de lo que dice
este traspaso más abajo: que los previos PGM/PVW en vivo están **verificados
en pantalla** por Andrés. Subiendo a mano el HWND del PGM por encima del
puente (`SetWindowPos(HWND_TOP)`, desde fuera del proceso) la imagen aparece
al instante — así que no es que el vídeo no llegue, es que algo lo tapa.

**No se ha tocado nada.** Puede ser un defecto real y anterior a esta tanda
(el hito 0 verificó el orden Z en `tools/winui3-lab`, un laboratorio aislado,
no en la app final), o puede ser un fallo de la propia automatización que lo
midió, no del código — en este proyecto ya se ha culpado tres veces al
código de fallos de la automatización (ver «Cómo se ha trabajado», al
final del documento). El método completo y la medición están en el
`## PENDIENTE` de `task-8-report.md` (§5), en
`.superpowers/sdd/2026-08-25-secuence-canvas-compositor/`.

**Lo primero que hay que contestar, antes que cualquier otra cosa de esta
tanda:** con el compositor APAGADO (la ruta clásica) y un cue de vídeo al
aire, ¿el hueco del PGM enseña el vídeo en tu máquina, o está negro?

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

**`WinCue.Presentation`** (4.125 líneas, 409 pruebas propias) contiene:
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

Añadido el 2026-08-25, misma regla (cálculo compartido primero, con pruebas,
y WinCue pasa a usarlo):

- **`CanvasEditorLayout`** — el cálculo entero del editor de canvas de
  Ajustes → Vídeo: escala/origen del lienzo, imán a bordes y a las otras
  salidas, mover, redimensionar, presets de tamaño. **16 pruebas puras.**
  Portado formula a formula desde el `CanvasEditorControl.cs` de WinCue (se
  verificó que no había ninguna discrepancia antes de mover una sola línea);
  WinCue **pasa a usarlo** — perdió su cálculo propio de escala/imán/clamp,
  que ahora vive una sola vez.

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

## Trampas del compositor GPU (D3D9Ex ↔ D3D11), pagadas en la Task 4 y la Task 7

Estas no son de WinUI 3 sino de interop D3D y de compilación, pero costaron
tanto tiempo como las de arriba y por el mismo motivo: fallan **en
silencio**, no en el compilador.

- **`ColorFill` está en el índice 35 de la vtbl de `IDirect3DDevice9`, no en
  el 42** (42 es `EndScene`, que no lleva argumentos). Contar mal un método
  COM por vtbl **no falla al compilar**: llama a otro método con otra firma,
  corrompe la pila o devuelve basura, y el fallo aparece lejos de la causa.
  Se recontó y se confirmó contra vecinos que ya funcionan en producción en
  el mismo fichero (`CreateRenderTarget = 28`, `StretchRect = 34`) — si el
  recuento estuviera desplazado, esos dos también estarían mal, y no lo
  están. Regla: **antes de fiarte de un índice de vtbl nuevo, recuéntalo
  contra uno vecino que ya esté en producción.**
- **Una superficie compartida D3D9Ex abierta en D3D11 NO trae sincronización
  implícita entre devices.** Son dos colas de comandos distintas sobre la
  misma GPU física: que `ColorFill` (o `StretchRect`) devuelva `S_OK` solo
  dice que el driver **encoló** el trabajo, no que la GPU ya lo **ejecutó**.
  Sin una barrera explícita (`CreateQuery(D3DQUERYTYPE_EVENT)` → `Issue` →
  `GetData` en bucle), D3D11 puede leer memoria todavía sin escribir: negro
  no determinista, intermitente, sin excepción ni HRESULT de error. **Y
  falla también con un `Thread.Sleep(100)` de por medio** — ese es el dato
  que mata la explicación fácil de «es cuestión de darle tiempo»: no es una
  carrera de temporización que un margen resuelva, es la ausencia total de
  la barrera. La fencea SIEMPRE el productor (quien escribió), nunca el
  consumidor, y nunca desde el hilo de render de mpv (no puede bloquearse).
  Ver `WinCue.Video/Gpu/SharedSurfaceSource.cs` y
  `D3D9Minimal.FlushAndWait` en `GlDxInterop.cs`.
- **Compilar con `dotnet build src\Secuence.App\Secuence.App.csproj` escribe
  en `bin\Release\...`, pero el exe que de verdad se lanza vive en
  `bin\x64\Release\...`.** Los dos árboles de salida coexisten y **el
  lanzador (`Get-ChildItem ... -Recurse`) puede coger el viejo** si el nuevo
  no se generó. Esto hizo que una medición saliera en verde con un fallo
  puesto A PROPÓSITO, porque se estaba midiendo el binario antiguo — se cazó
  comparando las marcas de tiempo de los dos `Secuence.dll`. **Compila
  SIEMPRE con `dotnet build WinCueSharp.sln`**, que es el único comando que
  llena la salida x64 de verdad.

## La lección que se repitió tres veces: un verde no siempre sostiene lo que dice sostener

En esta tanda, tres pruebas distintas salieron **verdes por el motivo
equivocado** — no estaban rotas, pero tampoco estaban demostrando lo que
afirmaban:

1. El primer fence «que fenceaba» (antes de corregir `D3DISSUE_END`) pasaba
   porque el trabajo incidental del test —compilar shaders, montar
   `TextureSink`/`OutputCompositor`— daba de sobra el tiempo que el fence
   debía garantizar. El fence no hacía nada; el verde venía de otro sitio.
2. Una prueba del espejo del PGM no habría cazado el fallo que decía cazar
   porque, en el escenario con el que se probó, los dos sinks (sala y
   espejo) medían exactamente igual — no había diferencia que la prueba
   pudiera detectar aunque el mecanismo estuviera roto.
3. Una medida de arrastre de ventana (para probar que redimensionar no
   borra el vídeo al aire) no llegó a ejercitar el camino roto porque el
   arrastre simulado terminaba en el mismo tamaño de salida por el que ya
   había pasado: el código tomaba un atajo (`early-return`) que el test ni
   se enteraba de estar evitando.

**El control que hace falta es siempre el simétrico: romper a propósito la
pieza que dices que sostiene el verde, y comprobar que asoma el rojo.** No
basta con sabotear la aserción (eso solo confirma que el test corre de
verdad); hay que sabotear el MECANISMO. Las tres pruebas de esta tanda que
sobrevivieron a revisión llevan ese sabotaje documentado en su informe, con
la salida literal en rojo antes de revertirlo.

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
- Previos **PGM/PVW en vivo** (mpv embebido) — *ojo: la Task 8 midió que en
  su máquina esto sale negro en las dos ramas por el z-order del puente
  XAML; ver la «PREGUNTA ABIERTA» al principio de este documento antes de
  dar esto por bueno en la tuya*
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
- **Scrub del cursor de edicion**: el fotograma bajo el cursor sale pausado
  en el PVW (visor de PREPARACION: no toca la sala).
- **Glifos en el timeline**: marcador GO, asa de grupo y etiquetas de
  bloque con sus prefijos. Win2D no resuelve la fuente por nombre ni por
  `ms-appx:///`, pero SI por un URI de ARCHIVO (IconFont.Win2DFamily), con
  guardian al arrancar.
- **Normalizar sonoridad** en el menu contextual (-16 LUFS), con la medida
  y la ganancia que implica compartidas.
- **Reordenar filas arrastrandolas** en la tabla, con su formato propio para
  no confundirlo con soltar archivos. La lista NO reordena sola
  (`CanReorderItems=False`): mutaria la coleccion a espaldas del modelo, y
  quien mueve un cue —con su deshacer— es el controlador.
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

Y lo añadido el **2026-08-25**, en la tanda del editor de canvas y el
compositor GPU (medido por máquina, con píxeles — el detalle técnico
completo está en `task-4-report.md`, `task-7-report.md` y
`task-8-report.md`, dentro de
`.superpowers/sdd/2026-08-25-secuence-canvas-compositor/`; lo que solo se
puede verificar mirando la pantalla está en el `## PENDIENTE` de esos
mismos informes y en la «PREGUNTA ABIERTA» de arriba):

- **Editor de canvas en Ajustes → Vídeo**: arrastrar y redimensionar las
  cuatro salidas dentro del lienzo, con imán a bordes y a las otras salidas,
  y presets de tamaño — el mismo control que ya tenía WinCue, ahora con el
  cálculo compartido (`CanvasEditorLayout`, ver arriba) y WinCue usando el
  mismo camino.
- **El compositor GPU vive en Secuence**: un `OutputCompositor` +
  `SwapChainSink` por salida, sobre el HWND hijo que ya existía — por eso
  blackout e «Identificar» le siguen funcionando sin tocarlos: los dos
  ocultan y muestran ese HWND, y el swapchain se va con él. Se enciende y
  se apaga desde Ajustes → Vídeo, en caliente.
- **Opacidad real entre pistas y crossfades de vídeo — esto en Secuence no
  había funcionado nunca.** `SetVideoCueOpacity` tenía el cuerpo vacío por
  defecto; ahora la opacidad baja en rampa de verdad (medido sobre la
  salida: `126 → 127 → 130 → 126 → 99,8 → 73,4 → 46,6`).
- **Modo canvas funcionando**: un vídeo puede repartirse entre varias
  salidas del compositor GPU a la vez.
- **El PGM espeja el compositor**, con selector `LIENZO` / `SALIDA 1..4` en
  su cabecera. Respeta el blackout **por región** (una salida a oscuras se
  ve a oscuras en el monitor, no el lienzo entero) y dice «Salida N — sin
  señal» en vez de decodificar y enseñar otra cosa cuando lo que mira no
  tiene nada al aire. Si la fontanería del espejo falla, cae a decodificar
  su copia muda en vez de dejar al operador sin imagen.
- El puente D3D9Ex→D3D11 (`SharedSurfaceSource`) que hace posible todo lo
  anterior, verificado por píxel — ver la trampa de sincronización entre
  devices, más abajo.

## Lo que falta

**Inmediato: nada.** La lista de paridad con WinCue quedó CERRADA el
2026-08-24 — inspector, tabla, timeline, barra de estado y menú contextual.
Todo lo que había aquí está en la lista de arriba, verificado en pantalla.

Lo que sigue NO es paridad, sino producto nuevo: es lo de «anotado y sin
hacer». Antes de abrir cualquiera de esos frentes conviene que Andrés repase
Secuence entera en pantalla, porque la paridad se ha comprobado control a
control pero no de un tirón como show completo.

**Anotado y sin hacer:**
- **`<AssemblyName>WinCue</AssemblyName>`** — el ejecutable se llama
  `WinCue.App.exe` de cara al cliente. Cambiarlo rompería accesos directos de
  quien ya tenga la beta.
- **Multiviewer y timecode**, que es lo que Andrés quiere de Secuence:
  - **Timecode ya existe a medias**: `Cue.TimecodeEnabled/TimecodeValue`,
    `RemoteRouter.OnTimecode()` que dispara al cruzar la marca, MTC por
    quarter-frame. **Falta el *chase*** (que el playhead siga al timecode) y
    **LTC** (timecode por audio, que es lo que da un teatro).
  - **El multiviewer ya tiene dónde apoyarse.** La contradicción que decía
    esta misma frase —que Secuence "no depende del compositor"— ya no
    existe: el compositor GPU (render-a-textura) vive en Secuence desde
    esta tanda, que es justo lo que hacía falta para mostrar N salidas a la
    vez sin N ventanas de mpv. **Sigue sin construirse** — nadie ha escrito
    el panel que las junte — pero ya no es un bloqueo de arquitectura, es
    trabajo pendiente sobre una base que ya funciona.
- **Lo que el compositor GPU todavía no hace, para que quede escrito sin
  adornos y no se venda como terminado:**
  - **Genlock entre salidas.** Cada salida presenta a su propio vsync, sin
    reloj común entre ellas; en modo canvas, un vídeo que cruza dos
    proyectores puede desincronizarse hasta **±1 fotograma** entre los dos.
  - **La ruta nativa `MpvSource`** — la fase 2b de verdad (mpv renderizando
    directamente a una textura D3D11, sin el puente D3D9Ex de por medio).
    Lo que hay hoy, `SharedSurfaceSource`, funciona y está verificado por
    píxel, pero sigue siendo el paso intermedio, no el destino.
  - **El multiviewer** (ver el punto de arriba).
  - **El fundido del blackout.** Hoy el blackout de sala oculta el HWND de
    golpe — instantáneo y a prueba de que el compositor se atasque, elegido
    así a propósito en la Task 8 — pero sigue sin existir un fundido a
    negro configurable.
  - **El rectángulo de destino estilo Resolume**: recortar y posicionar
    libremente una capa dentro de su salida, más allá del recorte de canvas
    que ya existe.
  - **Una carrera conocida y no cerrada**: si el `Join` de un hilo de
    render se agota (2 s) y una recreación posterior de swapchain SÍ tiene
    éxito, el hilo "zombi" del compositor viejo y el `Mutate` del nuevo
    compiten sobre la misma raíz del grafo. No hay forma de arreglarlo sin
    poder matar el hilo (no se puede) o sin cambiar de raíz (que borraría
    el vídeo al aire — el mismo fallo que ya se pagó una vez en la Task 7).
- Verificación de **DPI distinto entre monitores** — sigue sin hacerse. Esta
  tanda tampoco la resuelve (los monitores de prueba de Andrés siguen al
  100 %). Que no se caiga de esta lista.

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

**Dos flakes conocidos, no roturas:**
- `WinCue.Video.Tests.MpvPropertyTests.FadeVolume_ReachesTarget` — falla a
  veces por temporización bajo carga. Pasa en solitario y al repetir.
- `WinCue.Audio.Tests` (nuevo de esta tanda) — falla por **contención del
  dispositivo de audio** cuando la suite corre EN PARALELO con las demás.
  En solitario: **105/105**. No es un test concreto el que falla, es la
  suite entera compitiendo por el mismo hardware de audio.

Cualquier OTRA prueba en rojo sí es señal — de esas dos no hay que
desconfiar, del resto sí.

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
| `.superpowers/sdd/.../secuence-shell-report.md` | Los informes de cada tarea de Secuence (hitos 0 y 1) |
| `.superpowers/sdd/2026-08-25-secuence-canvas-compositor/progress.md` | El ledger de la tanda del canvas y el compositor GPU: todas las decisiones y por qué |
| `.superpowers/sdd/2026-08-25-secuence-canvas-compositor/task-{4,7,8}-report.md` | El detalle técnico del puente D3D9Ex→D3D11, el compositor GPU en Secuence y el espejo del PGM |
| `docs/plan-compositor.md` | La fase 2b tal y como se escribió. El compositor GPU que describe ya vive en Secuence (esta tanda); lo que sigue sin conectar es la ruta nativa `MpvSource` — ver «Lo que falta» |
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
