# CONTEXTO WinCueSharp — para continuar en otro chat

> **Repasado el 2026-07-28.** Este documento describe la ARQUITECTURA, que se
> mueve poco. Para el ESTADO (qué está hecho, qué falta, qué decide Andrés) la
> referencia es `docs/hoja-de-ruta.md`; para el motor de vídeo y el reloj,
> `docs/arquitectura-motor-video.md` y `docs/arquitectura-reloj.md`.
> No se anotan aquí cifras que caducan (número de tests, tamaño del exe): las da
> `dotnet test` y `publish.ps1`, que no mienten.

## Qué es
**WinCueSharp**: reproductor de cues para directo estilo QLab, en **C#/.NET 8 + WPF**.
Es la migración completa (y ya superación) del WinCue original en Python/PySide6, que
vive en `../WinCue` como referencia funcional. El objetivo original (.exe de Windows)
está cumplido: `publish.ps1` genera un single-file autocontenido.

## Ubicación / stack / ejecutar
- Carpeta: `C:\Proyectos\ffmpeg-gui\WinCueSharp` (repo ffmpeg-gui, rama `pruebas-v2`).
  Las hermanas `ffmpeg-gui/` y `FMC_py/` son OTRO proyecto con repos git propios — IGNORAR.
- Stack: .NET 8 LTS · WPF · **NAudio 2.3** (audio) · **libmpv-2.dll vía P/Invoke
  propio** (vídeo) · **bibliotecas LGPL de FFmpeg en proceso** (`ffmpeg-lgpl\bin`,
  motor ProRes propio) · FFmpeg/ffprobe en PATH por SUBPROCESO (conversión a HAP,
  duraciones, waveform — ahí la licencia es del usuario).
- **Dónde se busca libmpv** (`MpvNative.Resolve`, de lo explícito a lo ambiental):
  `WINCUE_LIBMPV` → junto al exe → subiendo, `libmpv-lgpl/` → subiendo, `WinCue/`
  → PATH. **`libmpv-lgpl/` es la build LGPL que se distribuye**; la de `WinCue/`
  (proyecto Python hermano) es **GPL** y solo vale para probar en local. El PATH
  va el último a propósito: iba primero y hacía que una libmpv cualquiera del
  sistema tapara la del repo.
- Ejecutar: `dotnet run --project src/WinCue.App` o el exe en
  `src\WinCue.App\bin\Debug\net8.0-windows\WinCue.App.exe`. Acepta ruta `.json` como
  argumento y `--smoke` (arranca y se cierra solo a los 3 s, sin diálogo de recuperación).
- Publicar: `.\publish.ps1` → `publish\WinCue.App.exe` autocontenido + `libmpv-2.dll`
  + `ffmpeg-lgpl\bin` + notices/licencias. **Verifica la licencia de las nativas
  del paquete y aborta si alguna no es LGPL** (borrándolas): es un guardián, no un
  aviso.
- Tests: `dotnet test WinCueSharp.sln` — la suite completa, toda en verde.

## Cómo trabajar
Andrés prueba en pantalla (el asistente no la ve). Flujo: editar → `dotnet build` →
`dotnet test` → smoke (`--smoke`, exit 0) → commit → relanzar en background para Andrés
(matar instancia previa: `Get-Process WinCue.App | Stop-Process -Force`; ojo: la
instancia abierta bloquea las DLLs del build). Fidelidad QLab y honestidad sobre qué
se puede/no. Commits en español, uno por feature.

## Arquitectura (proyectos)
- **WinCue.Core** — `Cue` (~70 campos, JSON snake_case idéntico a Python vía
  JsonPropertyName), `CueModel` (colección + jerarquía de grupos + ToJson/FromJson),
  `SettingsManager` (settings.json compatible), `MediaEngineContracts.cs`
  (**IAudioEngine/IVideoEngine**, MediaCueState, AudioPlayParams/VideoPlayParams/VideoGeometry).
- **WinCue.Audio** — `AudioEngine` **multi-bus** (un WASAPI/ASIO persistente + MixingSampleProvider
  POR DISPOSITIVO; bus "" = principal; salida por cue vía `AudioPlayParams.OutputDevice`),
  `CueSampleProvider` (TODA la matemática RT: fades sample-accurate, loop/play_count, trim,
  pausa individual con silencio, **RMS estéreo L/R** throttled 20 Hz — puro y testeable sin
  hardware), `AudioDecoder` (NAudio/MediaFoundation nativo + FFmpeg pipe f32le para
  ogg/opus/ac3/dts).
- **WinCue.Video** — `MpvNative` (P/Invoke mínimo del client API), `MpvPlayer` (wrapper con
  hilo de eventos; EndFile unifica MPV_EVENT_END_FILE + **eof-reached** — con keep-open mpv
  NO emite end-file), `VideoEngine` (estados por cue, salida única —un mpv por destino—,
  fades por timer, geometría en vivo log2-zoom/pan/rot90, imágenes, PauseCue). Superficie
  abstraída tras `Func<nint>` (HWND).
- **WinCue.Controller** — `MainController` (playhead + cues concurrentes QLab, transport
  GO/STOP ALL/FADE ALL/PANIC/pausa global y POR CUE, cadenas pre/post-wait +
  auto-continue/follow, grupos ENTER/TIMELINE/GO_NEXT/RANDOM round-robin, retrigger,
  fade-others, modos GO, FADE/START/STOP/SCRIPT, **undo/redo** por snapshots JSON con
  coalescencia de ráfagas <800 ms, copy/paste/duplicate con remapeo de jerarquía, move,
  **wall-clock operativo** (NowProvider inyectable), anti doble-GO (go_min_interval_ms),
  autosave silencioso, rutas relativas al guardar + media faltante→ERROR al cargar,
  **audición local** (AuditionCueId=-9999, dispositivo aparte), log del show (evento ShowLog),
  GetProgramVideo para el PGM). Desacoplado con **IShowView** e **IScheduler** (tests con
  reloj virtual y motores falsos). `CueFields` aplica campos snake_case por reflexión.
  `MediaImporter` (validación + ffprobe duración al importar).
- **WinCue.App** (WPF) — `MainWindow:IShowView`; `InspectorPanel` (5 pestañas SIN scroll:
  Basics/Triggers 3 columnas, Time&Loops con waveform estirada, Geometry = editor visual,
  Levels = fader vertical + VU por cue); `GeometryCanvasControl` (lienzo arrastrable:
  canvas azul + tercios + cruz, vídeo ámbar recortado); `WaveformControl` (onda 1600
  buckets, manejadores de trim, evento Scrubbing); `LiveMonitorControl` (mpv embebido
  MUDO en WindowsFormsHost — PGM re-sync por drift 0.35 s cada 500 ms + espejo de pausa,
  PVW en bucle, visor EDICIÓN pausado con scrub y marco azul = canvas);
  `VuMeterControl` (horizontal/vertical, peak-hold); `SettingsWindow` (5 pestañas,
  dispositivos reales WASAPI/ASIO/MIDI vía NAudio.Midi, ping OSC, MIDI Learn, hot-apply);
  `VideoOutputWindow` (colocación por píxeles físicos SetWindowPos en el monitor
  configurado, fullscreen blackout por defecto).

## UI actual (tras iteraciones con Andrés)
Top bar: GO/STOP ALL/FADE ALL/PANIC/PAUSA + "SIGUIENTE" | **reloj del show centrado**
(RESTANTE suma pre+dur de armados y resta desde el primer cue, rojo al agotarse ↔
TRANSCURRIDO verde; clic alterna, doble clic reinicia) + countdown del cue | derecha:
Modo **EDIT amarillo/SHOW verde**, **Salida vídeo rojo/verde** según activa, Configuración.
SIN fader master (vive en Settings→Audio). Panel izq: **PGM arriba, PVW abajo, cada uno
con VU ESTÉREO vertical a su derecha** (PGM = programa/mixer real; PVW = audición local),
notas del standby, tarjetas activas con botón ■. Tabla: progreso del cue DENTRO de su
fila, tipo con icono+color, drag-reorder, F2 renombra, Ctrl+Z/Y/C/V/D/P, hotkeys por cue.

## Compatibilidad JSON (restricción clave)
Shows y settings.json 100% intercambiables con Python: claves snake_case, enums
SCREAMING_SNAKE, wrapper `{"version":1,"cues":[...]}`. Campo nuevo `audio_output`
(C#-only; Python lo ignora al cargar y lo pierde al guardar — asumido). Tests de compat
con fixture real (demo_show.json) + round-trip cruzado verificado. Al guardar, el media
junto al .json va con ruta RELATIVA (show portátil).

## Recortes deliberados (UI honesta — no reintroducir sin motivo)
Fuera de la UI (los campos siguen en el modelo): duck/boost (fade_others lo cubre),
rotación 3D y scale_y, anchor, second_trigger_on_release, timecode, auto_load (la
precarga ya es automática), settings placebo pre_roll_ms/video_fade_frames. Columna
Lat OFF por defecto.

## Pendiente

> La lista viva está en `docs/hoja-de-ruta.md`. Aquí solo lo que afecta a la
> arquitectura descrita arriba.

1. ~~**Fase 6 — OSC/MIDI IN**~~ **HECHO** (`RemoteInputService`, `OscPacket`,
   `RemoteRouter` y sus tests).
2. **Compositor D3D11**: escrito en `src/WinCue.Video/Gpu/` y **sin conectar** —
   no lo llama nadie todavía. Su plan y sus condiciones, en
   `docs/plan-compositor.md`.
3. Menores: VU del inspector podría ser estéreo, rate/preserve-pitch en AUDIO
   (resampling), editor Timeline de grupos, pulido del tema.

## Gotchas resueltos (NO repetir)
- **mpv keep-open=yes NO emite end-file** al llegar al final → se observa `eof-reached`.
- **WPF airspace**: no se puede pintar sobre HWND nativo (mpv) → badges en barra, no overlay.
- WindowsFormsHost oculto = HWND oculto ✓ (equivalente al gotcha Qt del Python).
- El inspector SOLO se repuebla al cambiar selección (jamás al commitear un campo).
- Atajos globales inhibidos mientras el foco está en TextBox/ComboBox (Espacio ≠ GO).
- DPI mixto: la ventana de salida se coloca con SetWindowPos en píxeles FÍSICOS.
- Instancia de app abierta bloquea DLLs del build → matarla antes de compilar.
- `--smoke` omite el diálogo de recuperación de autosave (modal colgaba la verificación).
- UseWindowsForms mete usings implícitos que chocan con WPF → `<Using Remove=...>`.
- XAML exige constructor sin parámetros REAL (los opcionales no valen).
- Kills forzados dejan autosave.json huérfano → diálogo de recuperación legítimo al abrir.
- Python tenía 3 bugs no replicados: fade-out con play_count>1 silenciaba pasadas,
  huérfano de salida única quedaba PLAYING eterno, geometría FULL pisaba la escala.

## Último trabajo (2026-07-14, commit d0cd6194)
VU estéreo en motor (RMS L/R) + monitores PGM/PVW con VU estéreo, reloj del show
centrado, botones con código de color. **Falta feedback de Andrés**: si los VU estéreo
del PGM/PVW se mueven bien, si los colores de botones convencen, y el reloj del show.
Después: fase 6 (OSC/MIDI IN).
