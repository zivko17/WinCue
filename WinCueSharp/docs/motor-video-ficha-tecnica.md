# Motor de vídeo de WinCue — ficha técnica para comparar

*Escrito 2026-10-26 a partir del código (`src/WinCue.Video`, `src/WinCue.App/VideoOutputWindow*`,
`CompositorLayerControl`, `src/WinCue.Controller/MainController` y `CanvasLayout`).*

Este documento describe **lo que el motor hace hoy en producción** y lo separa de
lo que está escrito pero sin conectar. Está pensado para ponerlo al lado de OBS,
vMix, Resolume y Watchout. El porqué del diseño está en
[arquitectura-motor-video.md](arquitectura-motor-video.md) y el plan del
compositor nuevo en [plan-compositor.md](plan-compositor.md).

> **Lo primero que hay que saber para comparar:** WinCue **todavía no tiene
> compositor propio en producción**. Cada cue de vídeo dibuja en su propia
> superficie D3D9Ex, WPF la muestra con un `D3DImage` y **la mezcla entre capas
> (opacidad, orden z) la hace el DWM de Windows**. El compositor D3D11 propio
> (grafo, reloj por PTS, swapchain) está escrito y verificado por píxel en tests,
> pero **no lo llama nadie** (`src/WinCue.Video/Gpu/`).

---

## 1. Resumen en una tabla

| Aspecto | Hoy en producción | Escrito, sin conectar |
|---|---|---|
| API gráfica | OpenGL (mpv) + D3D11 (HAP/ProRes) → **D3D9Ex** compartido → `D3DImage` de WPF | **D3D11** en un único device |
| Quién mezcla las capas | **DWM** (animación de `Opacity` de WPF) | Compositor propio (blend factor en shader) |
| Reloj de salida | No hay tick de salida propio; cada fuente presenta a su ritmo | Un reloj maestro por salida (vsync del swapchain) |
| Sincronía entre capas | Por casualidad (cada capa va por su lado) | Todas las capas a un mismo instante T |
| Transiciones | Fundido de opacidad lineal animado por WPF, corte | Nodo `mix` con `Ramp` en el reloj del compositor |
| Salidas | Hasta **4** ventanas de salida, **8 pistas** (capas) cada una | Sinks: `SwapChainSink`, `TextureSink` |
| Multi-pantalla tipo Watchout | **Modo canvas**: un render, un recorte por salida | Nodo `transform` de recorte por salida |
| Espacio de trabajo | 8 bits sRGB (lo que haga el DWM) | `RGBA8` sRGB por defecto, `RGBA16F` lineal contemplado |
| Captura de la salida (grabar/NDI) | **No existe** | `GpuReadback` síncrono (solo tests); sink asíncrono planificado |
| Entradas en vivo (cámara, NDI, SDI, captura) | **No** | Fuera de alcance declarado |
| Efectos (color, key, LUT, blend modes) | **No** | Nodo `effect` previsto, no escrito |
| Warping / edge blending / máscaras | **No** (rotación solo a múltiplos de 90°) | Corner-pin previsto en F3 |
| Control | Motor de cues (GO/STOP/PANIC), OSC, MIDI, MTC, hora del día | GO = mutación agendada del grafo |

---

## 2. Arquitectura real (la que sale a la sala)

```
                        MainController (motor de cues)
                     decide la RUTA de cada cue al hacer GO
                                    │
     ┌──────────────────┬───────────┴──────────┬──────────────────────┐
     ▼                  ▼                      ▼                      ▼
 Motor HAP propio   Motor ProRes propio   mpv + compositor      mpv clásico (HWND)
 (.mov HAP)         (.mov ProRes)         (todo lo demás)       (fallback / HAP no
                                                                  elegible)
 MovDemuxer         MovDemuxer            libmpv, hwdec=auto    libmpv vo=gpu
 HapFrame (Snappy)  FFmpeg avcodec        render API OpenGL     embebido en un HWND
 BC1/BC3 → D3D11    planos R16 → D3D11    FBO = RT D3D9Ex       hijo por pista
 shader YCoCg→RGB   shader YUV→RGB        (WGL_NV_DX_interop2)
     │                  │                      │                      │
     ▼                  ▼                      ▼                      │
 RT B8G8R8A8 compartida → D3D9Ex (mismo adaptador por LUID)            │
     │  StretchRect a superficie de PRESENTACIÓN                       │
     ▼                                                                 ▼
 CompositorLayerControl (D3DImage, Lock/AddDirtyRect)       WindowsFormsHost
     │  Opacity + z por pista (WPF)                          (airspace: tapa
     ▼                                                        los overlays WPF)
 VideoOutputWindow  ── el DWM compone ──►  monitor de la salida
     │
     └─► VisualBrush ──► monitor PGM de la interfaz (espejo, sin decodificar)
```

### 2.1 Las cuatro rutas de un cue

La decide el controlador en el GO (`MainController.cs` ~1225-1300) y el motor la
ejecuta (`VideoEngine.StartPlayback`):

| Ruta | Cuándo | Vídeo | Audio | Fundidos de opacidad |
|---|---|---|---|---|
| **Motor HAP propio** | HAP en `.mov`, rate 1.0, compositor activo, sin canvas, geometría FULL | Decodificador propio en C# | Por el `AudioEngine` (rack), es el reloj maestro | Sí |
| **Motor ProRes propio** | ProRes en `.mov`, mismas condiciones | FFmpeg en proceso (LGPL dinámico) + shader propio | Por el `AudioEngine`, reloj maestro | Sí |
| **Híbrida mpv** | Resto de vídeos elegibles (H.264, HEVC, etc.) | mpv mudo (`aid=no`) en el compositor | Por el `AudioEngine`; `MpvSyncPll` disciplina el vídeo al audio | Sí |
| **mpv clásico** | Sin compositor, HAP no elegible, o driver sin interop | mpv `vo=gpu` dentro de un HWND | Por mpv | **No** (cortes) |

**Fallback por cue, nunca global:** si un motor propio falla (demux, DLL, GPU),
ESE cue cae a mpv mudo con el audio ya sonando por el rack. Si el interop
GL↔D3D falla, el compositor se apaga para la sesión (`_gpuFailed`) y todo va por
la ruta clásica.

### 2.2 Interruptores

| Variable / ajuste | Efecto |
|---|---|
| Ajustes → Vídeo → «Motor propio para ProRes» | Apaga el motor ProRes (vuelve a mpv) |
| `WINCUE_PRORES_ENGINE=0` | Igual, en caliente |
| `WINCUE_HAP_ENGINE` / `WINCUE_HAP_UNSAFE` | Control del motor HAP |
| `WINCUE_VIDEO_AUDIO=mpv` | Desactiva la ruta híbrida: el audio vuelve a mpv |
| `WINCUE_MIRROR_STREAMING=0` | El audio espejo se pre-decodifica a RAM en vez de streaming |
| `WINCUE_LIBMPV`, `WINCUE_FFMPEG_DIR` | Ubicación de las DLL nativas |
| `WINCUE_MPV_LOG=<dir>` | Log verboso de mpv por cue + traza del compositor |

---

## 3. Decodificación y códecs

| Códec / formato | Ruta | Detalle |
|---|---|---|
| **HAP / HAP Alpha / HAP Q** | Motor propio | Parser propio de la spec de Vidvox (Hap1/Hap5/HapY; segunda etapa None, Snappy o *chunked*). La GPU descomprime el DXT al muestrear. HapQ: shader YCoCg escalado→RGB (PSNR 51,6 dB contra ffmpeg) |
| **ProRes 422 Proxy/LT/422/HQ, 4444, 4444 XQ** | Motor propio | Decodificador de FFmpeg en proceso, `FF_THREAD_SLICE`, hilos por capa ≈ núcleos lógicos/capas (2-8). Planos `R16_UNORM`, conversión YUV→RGB en shader (BT.709/601/2020, rango limitado o completo, 10/12 bits). Croma POINT para igualar a swscale/mpv |
| H.264, HEVC, VP9, AV1, MPEG-2, DNxHD… | mpv (`hwdec=auto`) | Todo lo que soporte libmpv; decodificación por hardware cuando la hay |
| Imágenes (jpg, png, bmp, gif, tif, webp) | mpv | Fotograma fijo (`image-display-duration=inf`) |
| Contenedores | `.mov` para los motores propios (demuxer propio QuickTime); `.mp4 .mkv .avi .webm .mxf .ts .mts .m4v` por mpv | **`.mxf` no entra en los motores propios** (decisión declarada) |
| Conversión a HAP | `HapConvertService` | Lanza `ffmpeg.exe` por subproceso desde la interfaz |

**Demuxer propio** (`Containers/MovDemuxer.cs`) compartido por HAP y ProRes: lee
`stsz/stsc/stco/stts`, `colr` y `depth`; valida dimensiones, tamaños y offsets
antes de entregar nada a un decodificador nativo. No usa libavformat a propósito.

**Ring de decodificación:** 4 frames para HAP, 3 para ProRes (un frame ProRes 4K
4444 son 63 MB). ProRes no copia frames: cada slot posee su `AVFrame`
(`av_frame_move_ref`).

### Rendimiento medido (de los labs, RTX de escritorio)

| Escenario | Resultado |
|---|---|
| 2-3 × 1080p60 ProRes, cualquier perfil | Margen ×6 |
| 3 × 4K60 ProRes 422 | ✅ con ~30 % de margen (pide NVMe, ~660 MB/s) |
| 2 × 4K60 ProRes 4444 | ⚠️ al límite |
| 3 × 4K60 ProRes 4444 | ❌ cuello en la subida a GPU (11,4 GB/s pedidos frente a ~10,3 GB/s del bus), no en el decode |
| Subida por frame 4K | HAP 0,32 ms · ProRes 422 1,46 ms · ProRes 4444 3,47 ms |
| Compositor D3D11 nuevo (sin conectar) | Peor fotograma 0,15 ms sobre 16,7 ms de presupuesto |

Sin medir: máquinas solo con iGPU (Intel), AMD.

---

## 4. Tiempo, reloj y sincronía

**Principio:** el audio es el maestro y el vídeo se le esclaviza. Un glitch de
audio se oye; un frame repetido casi no se ve.

| Pieza | Qué hace |
|---|---|
| `HapMediaClock` | Reloj de los motores propios: se ancla al `elapsed` del audio del cue y extrapola con un monotónico entre bloques. Ignora un audio que llegue muy atrasado, sigue en *free-run* si se pierde el audio y gestiona las costuras de loop |
| `FramePacer` | Selección `frame = floor(t·fps)`; cuenta drops. Puro, testeado |
| Hilo de presentación propio + `timeBeginPeriod(1)` | Medido en el lab: pacear contra el tick de WPF daba un 10-25 % de drops por aliasing; con hilo propio, 0 |
| `MpvSyncPll` (ruta híbrida) | Tick cada ~400 ms: zona muerta de ±15 ms, corrección de velocidad de ±2 %, frenada hasta -10 %, *hard-seek* por encima de 2 s; con guardas de loop y *cooldown* |
| `ShowClock` (controlador) | Reloj del show (secuencia, pausas, plazos absolutos). **No gobierna la reproducción** (ver [arquitectura-reloj.md](arquitectura-reloj.md)) |

**Lo que falta hoy:** no hay un reloj de salida. El DWM compone a su vsync con lo
que cada capa haya publicado; no hay selección de frame por PTS a nivel de salida,
ni genlock entre salidas. Eso es justo lo que aporta el `OutputCompositor` sin
conectar (`FrameCadence`: el último frame con PTS ≤ T, que da 2:2 para 30p→60 Hz y
3:2 para 24p→60 Hz).

**Timecode externo:** el controlador recibe MTC y hora del día para disparar cues
(`RemoteRouter`), pero el vídeo **no persigue timecode** (no hay *chase*).

---

## 5. Composición, capas y salidas

- **Salidas:** hasta 4 (`Screen 0..3`), cada una con su `VideoOutputWindow` sin
  bordes y a pantalla completa, colocada con píxeles físicos (DPI mixto correcto).
- **Pistas:** 8 por salida (`Layer 0..7`, 0 = arriba). En la misma pista un vídeo
  sustituye al anterior; en pistas distintas conviven.
- **Crossfade en la misma pista:** `HandoverSeconds` mantiene vivo el saliente
  mientras entra el nuevo. Si el saliente termina dentro del fundido, su último
  fotograma se queda congelado (`VisualFreezeRegistry`) para que la mezcla no caiga
  a negro.
- **Opacidad por cue:** `SetCueOpacity(cue, op, fade)` = `DoubleAnimation` de WPF,
  lineal. Es la única transición: no hay wipes, *stingers*, *dip-to-color* ni modos
  de fusión.
- **Modo canvas (estilo Watchout):** el vídeo se rinde una sola vez en un canvas
  global y cada salida muestra un recorte `(x, y, w, h)` (`CanvasView`). En mpv un
  FBO del tamaño del canvas y un `StretchRect` por salida: las salidas quedan
  sincronizadas porque salen del mismo render. **Los motores HAP/ProRes no admiten
  canvas** (el cue cae a mpv).
- **Geometría:** FULL (letterbox + escala) o CUSTOM (traslación, escala, rotación
  **a múltiplos de 90°**). CUSTOM solo lo hace mpv. No hay *corner-pin*, *warping*,
  *edge blending*, máscaras ni *soft edges*.
- **Overlays de operador:** patrones de test (barras EBU, geometría, rejilla),
  identificación de salida, velo de *blackout* con fundido, cartela de respaldo del
  PANIC. Son elementos WPF por encima del vídeo; en la ruta clásica chocan con el
  *airspace* y se resuelven ocultando el host del vídeo.
- **Monitores de la interfaz:** PGM espeja la ventana de salida con un `VisualBrush`
  (lo que se ve es exactamente lo que sale y no decodifica nada). PVW y el visor de
  edición usan un mpv propio.

---

## 6. Arranque y latencia de GO

- **GO instantáneo (standby):** `PrepareStandby` deja el player creado y en pausa
  sobre su primer frame; el GO solo quita la pausa (~0 ms). Solo con compositor.
- **Pools persistentes** de pipelines GPU por (salida, pista, tamaño): crear y
  destruir el interop por cue perdía VRAM con el tiempo (auditoría 05). Por cue
  solo se hace *attach/detach* de la sesión.
- **Precarga:** verificación del fichero en segundo plano; el audio espejo arranca
  por streaming (cientos de ms) en vez de pre-decodificar a RAM.
- `LatencyReport` / `FrameReady` publican la latencia real GO→primer frame.

---

## 7. Audio de los cues de vídeo

En las rutas propias e híbrida, **el audio del vídeo pasa por el motor de audio de
WinCue** (rack completo: EQ paramétrico, compresor, VU, preescucha/PFL, salida
por dispositivo) y actúa de reloj maestro. En la ruta clásica, mpv saca el audio
directo con la misma EQ traducida a filtros lavfi (`MpvAfChain`), pero sin VU, RTA
ni GR por cue. Varispeed (`Rate ≠ 1`) solo por mpv, con corrección de tono
opcional.

---

## 8. Robustez y operación en directo

- Fallbacks en cascada: motor propio → mpv compositor mudo → mpv clásico → `vo`
  `gpu-next` → `null`. Sin libmpv el resto de la aplicación sigue funcionando.
- Interruptores en caliente por variable de entorno (sección 2.2).
- Riesgo asumido: el decode ProRes es nativo y en proceso; un fallo de memoria
  puede tirar la aplicación. Mitigación: validación del contenedor, *padding*
  obligatorio, lista cerrada de formatos de píxel, *fuzzing* (600 iteraciones, 0
  caídas).
- Licencias: FFmpeg solo LGPL con enlace dinámico; la publicación aborta si
  detecta DLL GPL.
- Tests: 145 casos en `WinCue.Video.Tests` (relojes, PLL, cadencia, ProRes, HAP,
  compositor por píxel, congelados). Soak de estrés de la aplicación entera con
  watchdog.

---

## 9. Lo que NO tiene (para que la comparación sea honesta)

| Funcionalidad | Estado |
|---|---|
| Compositor propio en producción | Escrito, sin conectar |
| Grabación, streaming, salida NDI/SDI/Spout | No |
| Entradas en vivo (webcam, captura HDMI/SDI, NDI, captura de pantalla) | No (`CaptureSource` previsto) |
| Títulos/rótulos generados | No (`TitleSource` con Direct2D previsto) |
| Efectos: color, LUT, chroma/luma key, desenfoque, modos de fusión | No |
| Transiciones distintas del fundido (wipe, stinger, dip) | No |
| Mapping: corner-pin, warping, edge blending, máscaras | No |
| Mezcla en luz lineal, 10 bits, HDR | No (fontanería `RGBA16F` lista en el motor nuevo) |
| Genlock entre salidas | No |
| Persecución de timecode (*chase*) | No |
| Clúster de varios PC de render | No: una sola máquina |
| Multiview | No (previsto) |

---

## 10. Plantilla de comparación

Columna WinCue rellenada con lo de arriba. Las demás columnas son un **punto de
partida de memoria, no verificado**: hay que contrastarlas con la versión concreta
de cada producto antes de sacar conclusiones.

| Eje | WinCue (hoy) | OBS Studio | vMix | Resolume Arena | Watchout |
|---|---|---|---|---|---|
| Propósito | Lanzador de cues para teatro/eventos | Producción y *streaming* en directo | Mezclador de directo | VJ, *media server* para eventos | *Media server* de espectáculo con línea de tiempo |
| Modelo de control | Lista de cues + GO, determinista | Escenas + conmutación manual | Inputs + conmutación manual | Composición de capas × clips, disparo manual/BPM | Línea de tiempo + cues |
| API gráfica | GL + D3D11 → D3D9Ex, mezcla del DWM | D3D11 (Windows), un hilo de gráficos propio | D3D11 | Motor GPU propio | Motor GPU propio |
| Reloj de composición | Ninguno propio (DWM) | Tick de vídeo fijo a los fps del proyecto | Fps del proyecto | Fps de salida | Sincronía por red entre nodos |
| Códec «de reproducción» preferido | HAP / ProRes con motor propio | FFmpeg genérico | Propio + FFmpeg | DXV (familia DXT) + HAP | HAP |
| Multi-pantalla | 4 salidas, canvas con recortes | Proyectores por escena | Salidas externas | *Advanced Output* (slices) | Escenario con displays, clúster |
| Mapping/blending | No | Mínimo (transformaciones) | Básico | Warping y edge blending | Warping y edge blending |
| Efectos | Ninguno | Filtros por fuente | Efectos y keys | Cientos de efectos, blend modes | Tweens y efectos |
| Entradas en vivo | No | Sí | Sí | Sí | Sí (captura) |
| Salidas de captura (NDI/record/stream) | No | Sí | Sí | Sí (NDI/Spout) | Limitado |
| Audio | Rack propio, el audio es el reloj del vídeo | Mezclador de audio | Mezclador de audio | Básico | Básico |

### Dónde WinCue tiene diseño diferencial

1. **El audio como reloj maestro del vídeo**, con el rack de audio completo sobre
   el audio de cada vídeo.
2. **Motores propios HAP y ProRes** con conversión de color en shader y sin doble
   decodificación (salida + PGM).
3. **Determinismo de lanzador de cues:** plazos absolutos, GO instantáneo
   precargado, fallbacks por cue.
4. **Diseño del compositor nuevo** (sin conectar): grafo como dato, selección por
   PTS, telemetría de drops desde el principio, mezcla en luz lineal contemplada.

### Dónde está claramente por detrás

Todo lo que depende de tener **un compositor propio en producción**: efectos,
transiciones, mapping, captura de la salida, genlock, entradas en vivo. Es la
fase F2/F3 de [plan-compositor.md](plan-compositor.md).

---

## Mapa de ficheros

| Capa | Ficheros |
|---|---|
| Fachada del motor | `src/WinCue.Video/VideoEngine.cs` (enrutado, pools, standby, fundidos de volumen) |
| Contratos | `src/WinCue.Core/MediaEngineContracts.cs` (`IVideoEngine`, `VideoPlayParams`, `VideoGeometry`, `CanvasView`, `IVideoLayerSurface`) |
| mpv | `MpvPlayer`, `MpvNative`, `MpvRenderNative`, `MpvGpuRenderer`, `GlDxInterop`, `MpvSyncPll`, `MpvAfChain` |
| HAP | `Hap/` (`HapFrame`, `HapFrameSource`, `HapCuePlayer`, `HapGpuPipeline`, `HapMediaClock`, `FramePacer`, `HapShaders`) |
| ProRes | `ProRes/` (`ProResDecoder`, `ProResFrameSource`, `ProResCuePlayer`, `ProResGpuPipeline`, `ColorConvert`, `ColorParams`, `FFmpegLoader`, `LicenseCheck`) |
| Contenedor | `Containers/MovDemuxer.cs` |
| Congelados | `VisualFreezeRegistry.cs` |
| Compositor nuevo (sin conectar) | `Gpu/` (`GpuDevice`, `FrameRing`, `FrameCadence`, `IVideoSource`, `MpvSource`, `HapSource`, `HapRingTarget`, `GlD3D11Interop`, `RenderGraph`, `LayerRenderer`, `CompositorShaders`, `RtPool`, `Ramp`, `OutputCompositor`, `OutputSinks`, `GpuReadback`) |
| Salida en pantalla | `src/WinCue.App/VideoOutputWindow.xaml(.cs)`, `CompositorLayerControl.cs`, `LiveMonitorControl.cs` |
| Enrutado por cue | `src/WinCue.Controller/MainController.cs` (GO), `CanvasLayout.cs` (canvas y geometría) |
| Labs | `tools/hap-lab`, `tools/prores-lab`, `tools/mpv-d3d11-lab` |
