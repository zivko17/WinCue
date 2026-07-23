# Investigación: cómo capturan vMix/OBS el audio de los vídeos — y cómo replicarlo en WinCue

*2026-07-23 — investigación previa a decidir la "ruta separada" del audio de vídeo.*

## La respuesta corta

**Ni vMix ni OBS "capturan" el audio de un player externo: no hay player externo.
Ambos decodifican los archivos ELLOS MISMOS** (con FFmpeg en el caso de OBS; vMix
con su propio stack de decodificación) y por eso el audio nace ya dentro de su
mixer, con EQ/compresor/VST/buses/VU por entrada. El control total viene de ser
el dueño de TODO el pipeline, no de un truco de captura.

## Cómo lo hace OBS (verificado en código/documentación)

- **Media Source = FFmpeg embebido**: la librería `media-playback` (`mp_media`)
  demuxa con `av_read_frame` y decodifica con `avcodec_send_packet` /
  `avcodec_receive_frame`. Los frames de vídeo se convierten a formatos de OBS
  (I420…) y el audio a `AUDIO_FORMAT_FLOAT_PLANAR`.
- **El audio manda**: el subsistema de audio de libobs es *pull-based* — un hilo
  dedicado tica cada 1024 muestras (~21 ms a 48 kHz) y MEZCLA lo que las fuentes
  han ido depositando (`obs_source_output_audio` → buffer circular por fuente,
  con resampleo automático vía swresample si la fuente no coincide con el
  backend). El dispositivo de salida tira del mixer: el reloj del dispositivo de
  audio es el metrónomo.
- **El vídeo se presenta contra timestamps**: las fuentes asíncronas entregan
  frames con PTS (`obs_source_output_video`) y libobs los bufferiza y presenta
  alineados con el audio. El drift entre relojes distintos (cada dispositivo de
  captura tiene el suyo) lo absorbe libobs bufferizando/descartando, y aun así
  es fuente clásica de glitches (existe un filtro comunitario, obs-async-audio-
  filter, solo para eso). **Lección: el drift es EL problema, incluso para OBS.**

## Cómo lo hace vMix

Arquitectura equivalente (propietaria): decodifica sus inputs, todo el audio
pasa por su mixer interno — 8 buses estéreo (Master, A–G), EQ/compresor/gain
por entrada, VST3 — y asume ~2 frames de latencia interna entre entrada y
salida, como un mezclador HD hardware. De nuevo: dueño del pipeline completo.

## Por qué WinCue no puede "capturar" el audio de mpv

Confirmado (y no solo por nosotros): **libmpv NO ofrece callback de audio
crudo**. El proyecto obs-mpv (mpv como fuente dentro de OBS) documenta la misma
pared: puedes controlar el volumen de mpv, pero no interceptar sus muestras.
mpv renderiza el audio directo al dispositivo con su propio ao. La única
"captura" posible es el loopback WASAPI del dispositivo (lo que ya hace nuestro
VU de programa), que sirve para MEDIR pero no para PROCESAR por-cue.

## Las dos formas de replicarlo en WinCue

### Opción A — Híbrido "mpv mudo + audio nuestro" (RECOMENDADA)

mpv sigue siendo el motor de VÍDEO (todo lo que ya funciona: compositor GPU,
standby, HAP, geometría). El audio del vídeo deja de salir por mpv:

1. **mpv arranca sin audio** (`--no-audio` o `ao=null`): sin pista de audio,
   mpv presenta el vídeo contra el reloj del sistema.
2. **La pista de audio la decodifica WinCue** (ffmpeg → PCM, como ya hace
   `AudioDecoder` para los cues de audio) y suena por el **AudioEngine**:
   VU por cue, RTA, EQ nativa, compresor, limitador de bus, buses por cue,
   audición… TODO el rack existente, gratis.
3. **Lazo de sincronía (la pieza nueva y la única delicada)**: un PLL suave —
   cada ~250-500 ms comparar `time-pos` de mpv (ya lo observamos en push para
   el timeline 60 Hz) con el `elapsed` del stream de audio (reloj WASAPI):
   - drift pequeño → micro-ajustar `speed` de mpv (±0,5-2 %). Sin audio en mpv
     el cambio de velocidad es INVISIBLE (solo adelanta/retrasa frames).
   - drift grande (> ~150-200 ms: stall de decode, seek) → corrección dura con
     seek de mpv a la posición del audio.
   - El AUDIO nunca se toca: es el reloj maestro, como en OBS.
4. **Arranque alineado, ya casi resuelto**: el standby actual precarga el vídeo
   EN PAUSA en su primer frame. GO = despausar mpv + arrancar el stream de
   audio en el mismo instante; la latencia variable de arranque de mpv
   (100-300 ms) desaparece del problema porque el standby ya la paga antes.

**Cuidado con la RAM**: `AudioDecoder` decodifica el archivo entero a RAM
(float estéreo 48 kHz ≈ 23 MB/min → una película de 2 h ≈ 2,7 GB). Para vídeo
hace falta un **provider de streaming** (decodificar por ventanas desde disco,
o ffmpeg → WAV temporal y leer en streaming) en vez de pre-decode total.

**Alcance estimado**: streaming provider de audio + flag "audio por WinCue" en
el cue de vídeo + PLL + arranque alineado + tests de drift con reloj virtual.
Es un proyecto mediano y ACOTADO; cada pieza existente (standby, observación
de time-pos, AudioEngine multi-bus) encaja sin refactor.

**Riesgos**: archivos VFR o con audio desincronizado de fábrica (el PLL lo
absorbe); seeks/loops (hard-resync tras cada uno); pérdida del `af` de mpv
(irrelevante: el procesado pasa a ser el nativo, que es el objetivo).

### Opción B — Motor de media propio completo (estilo OBS puro)

Decodificar TAMBIÉN el vídeo con FFmpeg (FFmpeg.AutoGen en C#) y presentar los
frames en nuestro compositor D3D contra el reloj de audio. mpv desaparece.
Control absoluto… y proyecto ENORME: decode HW (D3D11VA), HAP, seeks, loops,
VFR, subtítulos de color, años de casos raros que mpv ya tiene resueltos.
**No recomendada** mientras la Opción A cubra el objetivo (el control que se
busca es del AUDIO, y la A lo da entero).

## Recomendación

Opción A por fases:
1. **Prototipo de laboratorio** (scratchpad): un vídeo con claqueta A/V
   (beep+flash), mpv `--no-audio` + WAV por AudioEngine + PLL; medir drift real
   a 30-60 min. Criterio de éxito: |drift| < 20 ms sostenido (lipsync broadcast).
2. Si el prototipo aguanta: integrarlo como **opt-in por cue** ("Audio por
   WinCue") manteniendo la ruta mpv actual como fallback (y para ASIO-only).
3. La cadena `af` de mpv (commit de hoy) queda como fallback cuando el audio
   siga saliendo por mpv.

## Fuentes

- OBS media-playback (FFmpeg embebido): deepwiki.com/obsproject/obs-studio/4.2.1-media-playback-(ffmpeg-source)
- OBS pipeline de audio (tick 1024 samples, pull, resampler por fuente): deepwiki.com/obsproject/obs-studio/2.4-video-and-audio-pipelines · github.com/obsproject/obs-studio (libobs/media-io/audio-io.c, audio-resampler-ffmpeg.c)
- Diseño backend OBS: docs.obsproject.com/backend-design
- Drift entre relojes como problema real en OBS: github.com/norihiro/obs-async-audio-filter
- libmpv sin callback de audio (confirmación independiente): github.com/univrsal/obs-mpv
- vMix buses/EQ/comp/VST3 y ~2 frames de latencia: vmix.com/help27/AudioBusManager.html · vmix.com/help24/OutputAudioChannels.html
