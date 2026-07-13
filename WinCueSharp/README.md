# WinCueSharp — migración de WinCue (Python) a C#/.NET

Reproductor de cues para directo estilo QLab. Port del WinCue original en
Python (`../WinCue`), que se mantiene como referencia funcional viva durante
la migración.

## Stack

- .NET 8 (LTS), C#
- **Audio** (fase 2): NAudio — WASAPI exclusivo / ASIO, mixer multi-stream con
  fades sample-accurate (port de `audio_engine.py`)
- **Vídeo** (fase 3): libmpv render API → textura D3D11 → compositor propio
  (sustituye la "fase 2: compositor" del proyecto Python: opacidad, layers,
  rotación 3D, escala no uniforme, preview PGM en vivo real)
- **UI** (fase 5): WPF

## Estado de la migración

- [x] **Fase 1 — Núcleo**: `Cue`, `CueModel` (jerarquía de grupos incluida),
      `SettingsManager`. JSON 100 % compatible con los shows guardados por la
      versión Python (verificado con round-trip cruzado en ambas direcciones).
- [x] **Fase 2 — Motor de audio** (NAudio): mezcla multi-stream in-process
      (WASAPI/ASIO persistente + MixingSampleProvider), fades sample-accurate,
      loop/play_count/trim/pause/master, VU y latencia. Decoder universal
      (NAudio/MediaFoundation nativo + FFmpeg por pipe para ogg/opus/ac3/dts).
      Mejoras deliberadas sobre Python: loop sin hueco de silencio en el wrap,
      fade-out con play_count>1 solo en la última pasada (bug original), y GO
      sin coste de apertura de stream (el dispositivo queda abierto).
- [x] **Fase 3a — Motor de vídeo** (libmpv vía P/Invoke propio): máquina de
      estados por cue, salida única (un vídeo por destino), volumen/fades/
      loop/play_count/rate/trim/escala/geometría en vivo/imágenes/master.
      Superficie de render abstraída (Func&lt;HWND&gt;) para enchufar el compositor
      después. Fin de archivo con keep-open detectado vía eof-reached.
      Arreglos sobre Python: el huérfano de salida única pasa a STOPPED (su
      any_playing mentía), el estado se publica antes del Dispose (~100 ms de
      ventana IsPlaying/estado inconsistentes) y el modo FULL de geometría ya
      no pisa la escala del cue a 0.
- [ ] **Fase 3b — Compositor D3D11** (mpv render API → textura): opacidad,
      layers, rotación 3D, varias surfaces, preview PGM en vivo real.
- [ ] **Fase 4 — Controlador**: playhead + cues concurrentes, transport,
      cadenas pre/post-wait + auto-continue/follow, grupos (4 modos),
      triggers, retrigger, fade others, modo GO, master, EDIT/SHOW.
- [ ] **Fase 5 — UI WPF**: tabla de cues, inspector en vivo, top bar,
      panel de tarjetas, salida a monitor externo con blackout + testigo,
      mini-monitor PGM/PVW, atajos, drag-drop.
- [ ] **Fase 6 — Backends fase-2 del original**: MIDI/Timecode/Wall-clock,
      Duck/Boost, rate con preserve-pitch en audio, OSC.

## Compatibilidad de datos

- Los archivos de show (`*.json`) son intercambiables entre WinCue (Python)
  y WinCueSharp: mismas claves snake_case, mismos valores de enum
  (SCREAMING_SNAKE), mismo wrapper `{"version": 1, "cues": [...]}`.
- `settings.json` también es compatible (mismas claves y tipos).
- Los tests de `WinCue.Core.Tests` cubren esta compatibilidad con un fixture
  real generado por la versión Python (`Fixtures/demo_show.json`).

## Ejecutar tests

```
dotnet test WinCueSharp.sln
```
