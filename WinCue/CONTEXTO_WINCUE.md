# CONTEXTO WinCue — para continuar en otro chat

Pega esto al inicio de un chat nuevo. Es el estado completo del proyecto.

## Qué es
**WinCue**: reproductor de cues para eventos en directo estilo **QLab**, hecho en
Python, con salida de vídeo/audio para directo. Objetivo final: empaquetarlo a
**.exe** de Windows. Es un clon funcional de QLab que se va construyendo pestaña
a pestaña siguiendo la documentación de QLab v4.

## Ubicación / stack / ejecutar
- **Carpeta:** `C:\Proyectos\ffmpeg-gui\WinCue` (repo git `ffmpeg-gui`, rama
  `pruebas-v2`; snapshot de seguridad = commit `b1286c70`). Las carpetas hermanas
  `ffmpeg-gui/` y `FMC_py/` son OTRO proyecto (GUI de FFmpeg en Electron) — IGNORAR.
- **Stack:** PySide6 6.11 + **python-mpv** (libmpv; `libmpv-2.dll` está en la
  carpeta WinCue y `main.py` la mete en el PATH) + sounddevice + soundfile + numpy
  + pydub. FFmpeg/ffprobe en PATH (winget). Python 3.13 global. Deps instaladas.
- **Ejecutar:** `cd C:\Proyectos\ffmpeg-gui\WinCue && python main.py`
- **Arranca MAXIMIZADA** por defecto.
- **NADA está commiteado** desde el snapshot `b1286c70` (el repo tiene cambios del
  proyecto hermano en staging, por eso no se commitea). Todo está en el árbol de
  trabajo.

## Cómo trabajar (importante)
- Andrés lanza la app y prueba EN PANTALLA (el asistente no ve la pantalla). Los
  logs `[VIDEO]/[CUE]/[DEBUG]` salen por stdout.
- Flujo del asistente: editar código → **test headless** (`QT_QPA_PLATFORM=offscreen
  python -c "..."`) → a veces **1 arranque real** con auto-quit → **relanzar** la app
  en background para el usuario. Reiniciar la app tras cada cambio (Python no recarga
  en caliente).
- Para relanzar: matar la instancia previa con PowerShell
  `Get-CimInstance Win32_Process -Filter "Name='python.exe'" | Where-Object { $_.CommandLine -like '*WinCue*main.py*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }`
  y lanzar `python main.py` en background.
- Andrés quiere **fidelidad exacta a QLab** (layout de las capturas incluido) y va
  pidiendo feature por feature. Prefiere respuestas directas y honestas sobre qué
  se puede y qué no.

## Arquitectura (archivos)
- **main.py** — punto de entrada; PATH de libmpv; construye modelo→vista→controlador,
  conecta señales, `showMaximized()`, `refresh_video_output()`.
- **cue_model.py** — `Cue` (dataclass con TODOS los campos, ver abajo) + `CueModel`
  (lista + helpers de jerarquía). Constantes: CUE_TYPES, CONTINUE_MODES, GROUP_MODES,
  RETRIGGER_MODES, FADE_SCOPES, CUE_COLORS, STATE_COLORS.
- **cue_controller.py** — `MainController`: playhead + cues concurrentes, GO/transport,
  cadenas auto-continue/follow (chain timers), grupos, triggers, geometría en vivo,
  volumen master, timer PGM, guardar/cargar JSON, edición.
- **main_window.py** — `MainWindow` (vista) + `CueTableView`. Top bar (EDIT/SHOW +
  master + botones), panel izquierdo en TARJETAS (monitor PGM/PVW, transporte,
  standby, en ejecución), columna derecha = tabla de cues + **inspector debajo**
  (QSplitter vertical). Salida a monitor externo (blackout) + testigo.
- **inspector_panel.py** — `InspectorPanel`: pestañas Basics / Triggers / Display &
  Geometry (vídeo) / Time & Loops / Audio Levels. Edita EN VIVO el cue seleccionado.
- **inspector_widgets.py** — `TimeEdit` (MM:SS.mmm), `ColorSwatches` (✕+6 colores),
  `VerticalVu` (VU vertical del Audio Levels).
- **waveform_widget.py** — barra de trim (audio=forma de onda, vídeo=barra lisa) con
  regla + manejadores in/out arrastrables. Decodifica en QThreadPool.
- **geometry_preview.py** — `GeometryPreview`: preview de la geometría (miniatura del
  vídeo sobre la surface; arrastrar=trasladar, scroll=escalar).
- **monitor_widget.py** — `MonitorWidget`: mini-monitor PGM/PVW (extrae fotograma con
  ffmpeg, con guardia anti-solapamiento).
- **cue_edit_dialog.py** — diálogo modal antiguo (doble clic); sigue funcionando.
- **audio_engine.py** — MULTI-STREAM (dict cue_id→ctx, un OutputStream cada uno →
  audios se MEZCLAN). Fades sample-accurate, loop/play_count, trim, pause/resume,
  volumen master, stop con fade. Callback RT.
- **video_engine.py** — python-mpv, un player por cue pero **un vídeo por salida**
  (al arrancar uno termina los demás → no superposición). Volumen/fade/loop/play_count/
  rate/preserve_pitch, scale/trim/geometría (video-zoom/pan/rotate), imágenes, master,
  screenshot para PGM, salida a contenedor configurable (set_container).
- **video_engine.VideoContainer** — QWidget nativo (winId→HWND) donde renderiza mpv.
- **settings_manager.py / settings_dialog.py** — settings.json + diálogo (Audio, Vídeo
  [incluye SUPERFICIE/CANVAS], Red/Control, Interfaz [go_mode], Rutas).
- **media_import.py** — validación de archivos (audio/vídeo/imagen).
- **audio_decoder.py, vu_meter.py** — decodificador universal, VU horizontal.

## Modelo Cue (campos)
id, type, name, duration, state, latency_ms, file_path,
number, color, notes, armed,
pre_wait, post_wait, continue_mode (NONE/AUTO_CONTINUE/AUTO_FOLLOW),
volume, fade_in, fade_out, loop,
target_id, fade_stop_on_done, stop_others,
parent_id, group_mode (TIMELINE/ENTER/GO_NEXT/RANDOM), collapsed,
flagged, auto_load,
hotkey, retrigger (NONE/PANIC/STOP/HARD_STOP/HARD_RESTART),
fade_others, fade_others_time, fade_others_scope (PEERS/LIST/ALL),
scale, trim_start, trim_end,
play_count, rate, preserve_pitch,
midi_* / wallclock_* / timecode_* (UI guardada, backend fase 2),
duck_enabled/level/time (UI guardada, backend fase 2), second_trigger_on_release,
geo_mode (FULL/CUSTOM), layer, opacity, preserve_aspect,
translate_x/y, scale_x/y, anchor_x/y, rotate_x/y/z.
Tipos de cue: AUDIO, VIDEO (incl. imágenes), WAIT, FADE, START, STOP, SCRIPT, MEMO, GROUP.
`to_dict/from_dict` hacen roundtrip de todo y son retrocompatibles.

## Funcionalidad IMPLEMENTADA y verificada
- **Reproducción concurrente estilo QLab**: playhead/standby, GO dispara y avanza sin
  cortar; varios cues a la vez; audios se mezclan (multi-stream).
- **Temporización**: pre_wait, post_wait, AUTO_CONTINUE (chain timer independiente),
  AUTO_FOLLOW (al terminar la acción).
- **Tipos de cue**: FADE/START/STOP (con target), SCRIPT, MEMO, WAIT; fades sample-accurate.
- **Transport**: GO (Espacio), STOP ALL (S), PANIC (Esc, fundido global), PAUSE (P).
- **Modo GO configurable** (Settings→Interfaz): concurrent (audios solapan, vídeo
  exclusivo) / sequential / per_cue (flag stop_others).
- **Grupos** (4 modos + anidamiento + colapsar + agrupar Ctrl+G/desagrupar).
- **Inspector acoplado** (Ctrl+I) que edita EN VIVO: Basics (fiel a QLab, 2 columnas,
  color dots, target+→, notas), Triggers (hotkey funcional, second-trigger funcional,
  fade&stop funcional; MIDI/timecode/wall-clock/duck = UI guardada, motor fase 2),
  Time & Loops (barra de trim con manejadores, play count/infinite loop, rate,
  preserve pitch), Display & Geometry (Full/Custom, preview arrastrable), Audio Levels
  (fader + VU meter, fade in/out). Pop-out a ventana flotante.
- **Salida de vídeo a 2º monitor** con **blackout permanente** (nunca escritorio) +
  **testigo** (monitor, qué hay en pantalla). Canvas configurable en Settings
  (resoluciones VESA + custom).
- **Display & Geometry en vivo** (mpv): preserve aspect, scale (video-zoom), translate
  (pan), rotación 90°. Se aplica al instante sobre el vídeo que suena.
- **Mini-monitor PGM/PVW**: PVW=frame del cue en standby (al clicar), PGM=frame de la
  salida (~1.2s, ffmpeg). Aparece cuando la salida es externa.
- **Escala/trim/imágenes**, **volumen master** (top bar, aplica a audio+vídeo),
  **modo EDIT/SHOW** (Show = mínimo, bloquea edición, deja transporte + master).
- **Panel izquierdo en tarjetas** (MONITOR / TRANSPORTE / STANDBY / EN EJECUCIÓN),
  fuentes y botones grandes. Countdown por cue (en fila + panel + grande).
- **Cuenta atrás por cue** con duración real del media.
- **Guardar/cargar show JSON**, **drag-drop** de archivos crea cues, menú contextual.

## PENDIENTE / limitaciones (fase 2)
- **Compositor de vídeo** (rearquitectura del render, p.ej. mpv→texturas GL): hace falta
  para **opacidad**, **layers** (varios vídeos apilados), **rotación 3D arbitraria
  (quaternions)**, **escala no uniforme**, **varias surfaces**, **Video Effects (Quartz)**
  y **preview en vivo real** del programa (ahora el PGM es fotograma cada ~1.2s).
- **Backends de triggers**: escucha real de **MIDI / Timecode / Wall-clock**, motor de
  **Duck/Boost**, y **rate/preserve-pitch en AUDIO** (necesita resampling). La UI ya
  guarda los valores.
- **Pestañas de inspector que faltan**: Audio Trim, Audio Effects, Video Effects.
- **Editor visual de Timeline de Group** (arrastrar/trim/slip de hijos).
- **Pause** no congela los timers de cadena (un auto-follow puede dispararse en pausa).
- **Empaquetado PyInstaller a .exe** (incluir libmpv-2.dll + bin/ffmpeg).
- Pulido estético general.

## Gotchas resueltos (no repetir)
- Inspector se atascaba: cada edición repoblaba el inspector → re-decodificaba audio y
  re-extraía frames. Arreglado con flag `_suppress_inspector` en MainWindow.
- Superposición de dos vídeos: el `stop` de vídeo debe TERMINAR el player (no
  `command('stop')` con keep-open, que dejaba el frame congelado). Un vídeo por salida.
- Monitor PGM/PVW se salía del recuadro: la ventana nativa de mpv (VideoContainer) no
  se ocultaba en el QStackedLayout → hay que `setVisible(False)` explícito.
- Hilos de decodificación: NO usar QThread suelto (segfault). Usar **QThreadPool +
  QRunnable** y envolver los `signal.emit()` en `try/except RuntimeError` (el objeto de
  señales se puede destruir al cerrar).
- `screenshot_raw` de mpv necesita Pillow (no instalado) → usar ffmpeg `-ss time` para
  extraer el frame del PGM.
- No usar QThread para audio; el motor de audio es multi-stream con callback RT.

## Estado del último trabajo
Se acababa de: mover el inspector debajo de la tabla (columna derecha, splitter
vertical), dejar la columna izquierda libre a lo alto, arreglar el monitor PGM/PVW
que se solapaba (ocultar ventana nativa) y suavizar el PGM (refresco 1.2s + guardia
anti-solapamiento). Falta feedback del usuario sobre si el monitor ya no se sale, si
el PGM va suave y si el preview al clicar funciona.
