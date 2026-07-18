# Auditoría WinCue — 02 · Arquitectura y MainController

**Fecha:** 2026-07-18 · **Rama:** `pruebas-v2` · **Modo:** diagnóstico, sin refactorizar.
**Objeto:** `src/WinCue.Controller/MainController.cs` — 2 383 líneas, 1 clase, 60+ métodos.

---

## 1. Inventario por responsabilidad real

Grupos deducidos leyendo el fichero completo (las líneas son del fichero actual; los grupos no son contiguos en algún caso — el propio desorden es parte del diagnóstico).

| # | Responsabilidad | Miembros principales | Líneas ~ | Depende de | Llamado desde fuera por |
|---|---|---|---:|---|---|
| G1 | **Transport (API de vista)** | `OnGoPressed` `OnStopPressed` `OnPanic` `OnFadeAll` `StopCueById` `OnPauseCueToggle` `OnMasterVolume` `OnPauseToggle` `OnStopSelected` `OnPrev/Next/JumpTo/SetPlayhead` | 230 | núcleo (G4), motores, settings, vista | MainWindow (botones/hotkeys), RemoteRouting (G3), tests Transport |
| G2 | **Audición local + EQ** | `AuditionToggle` `EqFor` `GetAudioSpectrum` `GetAudioGainReduction` (`AuditionCueId`, `_auditionSourceCueId`) | 66 | `_audio`, settings, modelo | MainWindow, InspectorPanel (RTA/GR), tests Editing |
| G3 | **Control remoto** (OSC/MIDI/MTC/wall-clock) | `OnOscMessage` `OnMidiMessage` `OnTimecode` `ParseTimecode` `FindByNumber` `TriggerByNumber` `CheckWallclock` (`_tcFired`, `_tcLastSeconds`, `_lastWallclockSecond`, `NowProvider`) | 200 | G1 (transporte), `Trigger`, modelo, settings | RemoteInputService (App), `Tick`, **RemoteControlTests** |
| G4 | **Núcleo de disparo** | `GoCue` `GoGroup` `FireGroupChildren` `FireRandomChild` `Trigger` `StartAction` `ContinueNext` `OnActionEnd` `BeginVideoFadeOut` `VideoEndCheck` `DurationElapsed` `ApplyGoExclusivity` `ApplyFadeOthers` `PlayFromTimeline` `TriggerCueId` + helpers puros (`RateOf` `ActionDuration` `VideoLayerOf` `ScreenOf`) + `RunningCue`/`_running`/`_rrState` | 590 | TODO: modelo, ambos motores, scheduler, vista, settings, G5, G6, G7 | G1, G3, G6; **TransportAndChain/GroupAndTargeting/PlayFromTimeline/VideoFadeOut/ShowPruebaE2E** |
| G5 | **Canvas / geometría / standby vídeo** | `CanvasMode` `CanvasDims` `CanvasViews` `ParseRegion` `SetCueLayerOpacity` `VideoParamsFor` `PrepareVideoStandby` `GeoFor` | 150 | settings, vista (dimensiones/visibilidad), `_video` | G4, `SetPlayhead`; **CanvasModeTests, VideoStandbyTests** |
| G6 | **Cues de control** (FADE/START/STOP/SCRIPT) | `ExecFade` `ExecStart` `ExecStop` `ExecScript` | 87 | motores, G4 (`Trigger`/`EndCue`/`StopEverything`), `Process.Start` | Solo interno (desde `StartAction`); **GroupAndTargetingTests** |
| G7 | **Fin de cue / stop global** | `EndCue` `StopEverything` | 70 | motores, vista, G10 (tally) | G1, G3, G4, G6, persistencia |
| G8 | **Señales de motores** | `OnAudioStateChanged` `OnVideoStateChanged` `OnLatencyReport` `OnAudio/VideoError` | 60 | modelo, vista, G4 (`OnActionEnd`) | Suscripciones en el constructor |
| G9 | **Tick (estado del panel de activos)** | `Tick` (+ countdown, elapsed por motor) | 100 | `_running`, motores, vista, modelo | App (DispatcherTimer 100 ms), tests con reloj virtual |
| G10 | **Playhead / vista / precargas / tally** | `SetPlayhead` `ModelState` `GetProgramVideo` `TopVideo` `RefreshVideoTally` `PreloadUpcoming` `Schedule` `ScheduleChain` `Log` | 180 | modelo, motores, vista, G5 | Todos los grupos; MainWindow (PGM) |
| G11 | **Undo/redo** | `PushUndo` `Undo` `Redo` `RestoreSnapshot` `CanUndo/CanRedo` (`_undoStack`, `_redoStack`, `HasUnsavedChanges`) | 65 | modelo (ToJson/FromJson), vista, `SetPlayhead` | MainWindow (Ctrl+Z/Y), G12–G15 (`PushUndo`); **EditingAndSafetyTests** |
| G12 | **Portapapeles / mover** | `CopyCues` `PasteCues` `DuplicateCues` `SerializeRows` `InsertSerialized` `OnMoveCue` (`_clipboard`, `CueJson`) | 143 | modelo, G11, vista | MainWindow (Ctrl+C/V/D, drag), timeline; **EditingAndSafety/ListManagement** |
| G13 | **Gestión de lista** | `AddNewCue` `OnInsertCue` `OnDeleteCue` `RenumberCues` `OnGroupSelected` `OnUngroup` `OnToggleCollapse` `AddCuesFromFiles` `Capitalize` | 185 | modelo, G11, vista, MediaImporter | MainWindow (menús); **ListManagementTests** |
| G14 | **Edición desde inspector** | `OnCueEditCommitted` `ApplyLiveEdits` `IsEqKey` `GeoKeys` | 100 | modelo, G11, motores (live), G5, G10 | InspectorPanel; **EditingAndSafetyTests** |
| G15 | **Persistencia** | `SaveShow` `RotateBackups` `Autosave` `LoadShow` `MissingMediaCount` `RelocateMedia` | 195 | modelo, filesystem, G11, G7, `SetPlayhead`, `_audio.InvalidateCache` | App (autosave), MainWindow (diálogos); **RelocateMediaTests, EditingAndSafety** |
| G16 | **Cierre** | `Shutdown` | 15 | motores, timers | App.OnExit |

**Lectura clave:** el **núcleo de disparo (G4, ~590 líneas)** es el corazón real y lo único que justifica llamarse "MainController". Los grupos G11–G15 (**~690 líneas, un 29 % del fichero**) son *edición y persistencia del show* — no tocan `_running` salvo puntualmente y son extraíbles con bajo riesgo. G3 (~200) es *parseo/ruteo* que solo necesita una fachada de acciones.

### Estado (campos) y quién lo comparte

| Campo | Grupos que lo tocan | Riesgo |
|---|---|---|
| `_running` (Dictionary) + `RunningCue` mutable | G1, G3, G4, G6, G7, G8, G9, G10, G14 — **9 de 16 grupos** | **El estado caliente.** Flags de `RunningCue` (`ManualStop`, `SuppressChain`, `SuppressVisualFadeOut`…) codifican invariantes implícitas entre grupos |
| `_playhead` | G1, G4, G10, G11, G12, G13, G15 | Medio (siempre vía `SetPlayhead`, que además dispara precargas — efecto oculto) |
| `_undoStack`/`_redoStack`/`HasUnsavedChanges` | G11 ← G12–G15 | Bajo (solo vía `PushUndo`) |
| `_chainTimers` | G4, G7, G16 | Bajo |
| `_rrState` (round-robin RANDOM) | G4, G13 (`OnUngroup` lo limpia) | Bajo |
| `_clipboard`, `_tcFired`, `_lastGoTicks`, `_auditionSourceCueId` | 1 grupo cada uno | Nulo |

---

## 2. Complejidad ciclomática (aproximada*)

\* Contando puntos de decisión (`if/while/for/foreach/case/catch/&&/||/?:/??`) + 1, con script; margen ±2. Umbral pedido: >10.

| CC | Método | Líneas | Grupo | Comentario |
|---:|---|---:|---|---|
| **27** | `StartAction` | 143 | G4 | El switch gigante por tipo de cue; el caso Video (crossfades) es la mitad |
| **22** | `RefreshVideoTally` | 50 | G10 | Dos bucles anidados + **construye texto de tooltip de UI** (ver §3) |
| **22** | `OnOscMessage` | 48 | G3 | Parseo de direcciones + dispatch |
| **21** | `Tick` | 80 | G9 | Elapsed/countdown por cada cue activo con casos por tipo |
| **16** | `OnCueEditCommitted` | 56 | G14 | Casos especiales de file_path/hotkey/standby |
| **15** | `ApplyLiveEdits` | 33 | G14 | Reflejos en vivo por familia de campo |
| **14** | `OnMoveCue` | 49 | G12 | Aritmética de bloques/grupos |
| **14** | `LoadShow` | 53 | G15 | Rutas relativas + media faltante |
| **14** | `ApplyGoExclusivity` | 25 | G4 | Predicado compuesto pista/salida/crossfade |
| **13** | `RelocateMedia` | 55 | G15 | Indexado + recorrido |
| **13** | `OnMidiMessage` | 45 | G3 | Notas globales + triggers por cue |
| **12** | `SaveShow` | 42 | G15 | Relativizar/restaurar rutas |
| **11** | `InsertSerialized` | 49 | G12 | Remapeo de ids/jerarquía |
| **11** | `FireRandomChild` | 37 | G4 | Round-robin con doble pasada |
| **11** | `EndCue` | 37 | G7 | Ramas audio/vídeo/fade |
| 10 | `Trigger`, `OnTimecode`, `OnPauseCueToggle`, `OnGroupSelected` | — | — | En el umbral |

Ninguno es un disparate por sí solo (están comentados y testeados), pero `StartAction` (27/143) es objetivamente el método más frágil del proyecto: toda modificación de reproducción pasa por ahí.

---

## 3. Acoplamientos problemáticos

1. **Sin dependencias circulares de proyecto** ✓ — `Core ← Audio/Video/Controller ← App` es un DAG limpio. El acoplamiento problemático es **interno a la clase**, no entre ensamblados.

2. **`_running` como estado compartido por 9 grupos.** Las invariantes viven en comentarios ("el fin lo retira su propio fin", "evita doble action-end") y en flags mutables de `RunningCue`. Cualquier extracción tiene que decidir quién es el **dueño único** de `_running` (propuesta: lo sigue siendo el núcleo G4+G7; nadie más lo muta).

3. **`SetPlayhead` tiene efectos ocultos** (precarga/standby de vídeo, G5): mover el playhead desde la edición de lista (G12/G13) dispara maquinaria de vídeo. Correcto funcionalmente, sorprendente para quien mantiene.

4. **Presentación incrustada en lógica de negocio:**
   - `RefreshVideoTally` **construye el texto del tooltip** (`StringBuilder` con "S1: … | pista N", "MODO CANVAS") — formato de UI en el controlador.
   - ~30 llamadas a `FlashStatus` con textos con emoji ("🎧", "⏸", "◢") — mensajería de UI dispersa por todos los grupos.
   - No es WPF (eso está bien aislado tras `IShowView`), pero sí es *copy* de UI que debería vivir en la vista o en un formateador.

5. **`ExecScript` lanza procesos** (`Process.Start` con python/cmd/powershell) desde el controlador — efecto de sistema operativo en la capa de orquestación; candidato a servicio inyectable (y hoy imposible de testear sin lanzar procesos).

6. **Lectura de settings dispersa** (`go_mode`, `video_canvas_mode`, `surface_*`, `midi_go_note`…): 15+ claves leídas en caliente por 6 grupos. Sin tipado ni punto único; un typo en una clave falla en silencio.

7. **`IShowView` es ancha** (~20 métodos) y el controlador la llama desde todos los grupos. Funciona (los fakes de test la implementan), pero cada extracción deberá recibir la vista o una porción de ella.

---

## 4. Plan de extracción incremental

Criterio: **(riesgo de mantenimiento que elimina) / (riesgo de romper al extraer)**. Todas las extracciones mantienen la API pública de `MainController` (delegación), un módulo por fichero, y al núcleo (G4+G7) como único dueño de `_running`.

### Paso 1 — `ShowFileService` (persistencia, G15) → ~195 líneas
- **Qué:** `SaveShow`, `RotateBackups`, `Autosave`, `LoadShow`, `MissingMediaCount`, `RelocateMedia`.
- **Por qué primero:** cero contacto con `_running`/timers/motores en caliente (solo `StopEverything` al cargar, `InvalidateCache` — inyectables como callbacks); es I/O de ficheros + transformación de modelo, lo más lejos del directo. Máxima separabilidad, mínimo riesgo.
- **Cubierto por:** `RelocateMediaTests` (guardar/cargar/relativas/relocalizar), `EditingAndSafetyTests` (autosave/unsaved), `ShowPruebaFileTests` (formato), E2E carga el show real.
- **Faltaría:** test de `RotateBackups` (rotación .bak1→3 y que un backup fallido no bloquea el guardado).

### Paso 2 — `CueListEditor` (edición de lista + portapapeles, G12+G13) → ~330 líneas
- **Qué:** `CopyCues/PasteCues/DuplicateCues/SerializeRows/InsertSerialized/OnMoveCue` + `AddNewCue/OnInsertCue/OnDeleteCue/RenumberCues/OnGroupSelected/OnUngroup/OnToggleCollapse/AddCuesFromFiles`.
- **Por qué:** manipulación pura del modelo + `PushUndo` + `ReloadTable`; toca `_running` solo en `OnDeleteCue` (parar el cue borrado → callback al núcleo). El trozo más grande extraíble con seguridad.
- **Cubierto por:** `ListManagementTests` (293 líneas), `EditingAndSafetyTests` (copy/paste/duplicate/move/undo).
- **Faltaría:** poco; quizá mover-grupo-dentro-de-sí-mismo ya está cubierto — revisar y completar bordes de `OnMoveCue` (destino = último, grupos anidados).

### Paso 3 — `RemoteRouter` (control remoto, G3) → ~200 líneas
- **Qué:** `OnOscMessage`, `OnMidiMessage`, `OnTimecode`, `ParseTimecode`, `CheckWallclock`, `FindByNumber`, `TriggerByNumber`.
- **Por qué:** es un *parser+dispatcher* que solo necesita una fachada de 6 acciones (GO, STOP ALL, PANIC, FADE ALL, PAUSE, TriggerCueId/EndCue) + lectura del modelo. Encaja con la fase 6 recién hecha y aísla el punto de entrada externo (Companion/consolas) — valor de seguridad alto.
- **Cubierto por:** `RemoteControlTests` (226 líneas: OSC, MIDI, timecode, wall-clock con `NowProvider`).
- **Faltaría:** casos raros OSC (bundles anidados ya en tests de `OscPacket`; añadir dirección desconocida → no-op, argumento float de Companion).

### Paso 4 — `UndoService` (G11) → ~65 líneas
- **Qué:** pilas, coalescencia de 800 ms, `RestoreSnapshot` (con callbacks `ReloadTable`/`SetPlayhead`).
- **Por qué después de 2:** los clientes de `PushUndo` (pasos 1-2) ya estarán extraídos y recibirán el servicio; hacerlo antes obligaría a retocarlos dos veces.
- **Cubierto por:** `EditingAndSafetyTests` (undo/redo/coalescencia).
- **Faltaría:** test de límite `MaxUndo=100` y de snapshot corrupto (rama `FormatException`).

### Paso 5 — `CanvasLayout` + parámetros de vídeo (G5) → ~150 líneas
- **Qué:** `CanvasMode/CanvasDims/CanvasViews/ParseRegion` + `VideoParamsFor/GeoFor` (factoría de parámetros), quizá `PrepareVideoStandby`.
- **Por qué:** funciones casi puras sobre settings; las usa el núcleo pero no mutan nada. Reduce el fichero y da un hogar natural a la config de vídeo.
- **Cubierto por:** `CanvasModeTests`, `VideoStandbyTests`, `VideoOutputTests`.
- **Faltaría:** `ParseRegion` con regiones fuera de rango (clamps) — creo que parcial; completar.

### Paso 6 (opcional, mayor riesgo — solo si el proyecto crece) — `CueRuntime` (G4+G7+G8+G9)
- El núcleo de disparo con `_running` como estado privado, más señales de motores y Tick. Es el 40 % restante y el de mayor riesgo: **no extraer aún**; tras los pasos 1-5 el fichero queda en ~1 100 líneas coherentes ("motor de show"), que ya es un tamaño defendible.

**Además, transversal y barato (aprovechando pasos 1-5):** sacar los textos de `FlashStatus`/tooltip del tally a la vista o a un formateador (§3.4), y `ExecScript` → `IScriptLauncher` inyectable (§3.5, permitiría testearlo).

### Qué NO hacer
- No trocear `StartAction` "por bonito" mientras siga en el núcleo: su complejidad es esencial (tipos de cue × crossfades); dividirlo sin mover la propiedad de `_running` solo repartiría el acoplamiento.
- No introducir un contenedor de DI ni interfaces por grupo: servicios concretos con dependencias por constructor bastan (el patrón de tests actual — fakes del view/motores — sigue valiendo).

---

## Resultado de la ejecución (2026-07-18)

Los 5 pasos de bajo riesgo se ejecutaron el mismo día, uno por commit, con la
suite completa (255) en verde tras cada uno y smoke final OK:

| Paso | Servicio nuevo | Commit | MainController |
|---|---|---|---:|
| 1 | `ShowFileService` (241 L) | `5d637e13` | 2 383 → 2 236 |
| 2 | `CueListEditor` (379 L) | `1d06ea53` | → 1 959 |
| 3 | `RemoteRouter` (274 L) | `7b997989` | → 1 807 |
| 4 | `UndoService` (104 L) | `8d49dfb3` | → 1 766 |
| 5 | `CanvasLayout` (141 L) | `75496957` | → **1 687 (−29 %)** |

Reglas respetadas en los 5: API pública de `MainController` intacta
(delegación), un módulo por fichero, y el núcleo como **dueño único de
`_running`** — los servicios reciben callbacks, jamás mutan estado de
reproducción. `HasUnsavedChanges` quedó en el controlador (ciclo de edición);
`NowProvider` y los estáticos públicos (`ParseTimecode`, `ParseRegion`)
delegan para no romper tests ni UI.

Los tests pendientes anotados por paso (RotateBackups, MaxUndo, snapshot
corrupto, bordes de ParseRegion) siguen pendientes — son deuda de test, no de
código. El paso 6 (`CueRuntime`) queda como estaba: **no ejecutar** salvo
crecimiento del proyecto.

## 5. Resumen ejecutivo

- `MainController` son **16 responsabilidades en un fichero**; el núcleo legítimo (disparo+fin+tick, ~820 líneas) está rodeado de ~1 000 líneas de edición, persistencia y ruteo que **no necesitan estar ahí**.
- Los 5 primeros pasos extraen **~940 líneas (40 %)** con riesgo bajo y cobertura de tests ya existente en su mayoría; el fichero resultante (~1 100) es mantenible por una persona.
- El mayor peligro real no es la complejidad de un método, sino **`_running` compartido por 9 grupos con invariantes implícitas**: la regla de oro de toda la extracción es que el núcleo quede como dueño único.
- 15 métodos superan CC 10; solo `StartAction` (27) merece atención específica, y su momento es el paso 6 (si llega).
