# WinCue frente a OBS, vMix, Resolume y Watchout — cómo están construidos y qué copiar

*Escrito 2026-10-26. Complementa a [motor-video-ficha-tecnica.md](motor-video-ficha-tecnica.md),
que describe WinCue por dentro.*

**Objetivo de este documento:** que la reproducción de **HAP y ProRes** en WinCue
sea tan robusta como la de los productos profesionales. Por eso no compara
funciones sueltas: compara **cómo está construido cada motor** y saca de ahí qué
hay que copiar.

---

## Cómo se ha verificado (y qué no)

| Producto | Fuente | Fiabilidad |
|---|---|---|
| **OBS Studio** | **Código fuente**, commit `72e8665` de `obsproject/obs-studio` (23-10-2026). Ficheros citados con ruta | Alta: es lo que hace el programa |
| **FFmpeg** (lo usan OBS, vMix y mpv) | Código de `libavcodec/hapdec.c` (master) | Alta |
| **Watchout 7** | Documentación oficial de Dataton (`docs.dataton.com`, `knowledge.dataton.com`) leída a través del buscador | Media-alta. Algunas páginas de Dataton se contradicen entre versiones; se indica cuando pasa |
| **Resolume Arena** | Páginas de soporte de Resolume y respuestas del equipo de Resolume en su foro | Media. **No hay documentación pública** de cómo decodifica HAP por dentro |
| **vMix** | Ayuda y base de conocimiento oficiales | Media sobre arquitectura. **No he encontrado** ninguna fuente que confirme soporte de HAP |

Limitación del entorno: la política de red de esta sesión bloquea la descarga
directa de `docs.dataton.com`, `knowledge.dataton.com`, `resolume.com` y
`www.vmix.com`. Las páginas se han leído a través del buscador, no completas.
Vale para afirmaciones concretas, no para matices finos.

**Lo que ninguna documentación cuenta** (y no voy a inventar): los detalles
internos de vMix, Resolume y Watchout (hilos, colas, cómo eligen frame). De ellos
solo sabemos lo que publican y lo que se mide desde fuera.

---

## Respuesta corta

1. **En HAP, WinCue ya está construido como los media servers (Resolume, Watchout)
   y mejor que OBS.** El DXT va directo a la GPU y la GPU lo descomprime. OBS (y
   cualquier programa que use el decodificador HAP de FFmpeg, incluido mpv)
   descomprime el DXT **en la CPU** a RGBA y luego lo sube: el triple de bytes por
   el bus y CPU quemada. Esto está verificado en el código.
2. **En ProRes, WinCue está a la par de lo mejor que hay en Windows:** el
   decodificador de FFmpeg (el que usa casi todo el mundo) más la conversión de
   color en la GPU. Watchout, por defecto, **ni siquiera reproduce ProRes**: lo
   transcodifica a NotchLC al importarlo.
3. **Donde WinCue no está a la altura no es en los códecs, sino en todo lo que hay
   alrededor de ellos.** Los cuatro productos tienen algo que WinCue no tiene:
   - **un reloj de composición propio**, que presenta en el vsync y está fuera del
     hilo de la interfaz;
   - **un disco que se da por supuesto**: NVMe exigido y lectura por delante;
   - **contadores de caídas a la vista del operador**;
   - en el caso de Watchout, **el render en otro proceso** distinto del de la
     interfaz.

   Hoy, en WinCue, la copia final de cada frame a la pantalla se hace **en el hilo
   de interfaz de WPF**: si la interfaz se atasca, el vídeo de la sala se congela.
   Ese es el punto más frágil, y no lo arregla ningún códec.

---

## 1. OBS Studio — verificado en el código

### Cómo está construido

```
 hilo de decodificación por fuente          hilo GRÁFICO único (libobs/obs-video.c)
 (shared/media-playback/media.c)            ┌──────────────────────────────────────┐
  libavformat + libavcodec                  │ cada intervalo de 1/fps del proyecto: │
  hwdec opcional ─► av_hwframe_transfer_data│  tick_sources()                       │
     (¡vuelve a la CPU!)                    │  ├─ por fuente: get_closest_frame(T)  │
  swscale si el formato no es nativo        │  ├─ sube planos (gs_texture_set_image)│
  obs_source_output_video(frame + PTS) ───► │  │   y convierte YUV→RGB en shader    │
     (cola async_frames por fuente)         │  render_main_texture (escenas)        │
                                            │  output_frames → encoders/outputs     │
                                            │  video_sleep(): duerme hasta el       │
                                            │  siguiente intervalo; si llega tarde, │
                                            │  cuenta "lagged_frames"               │
                                            └──────────────────────────────────────┘
```

- **Un solo hilo gráfico con reloj fijo** a los fps del proyecto, no al vsync de
  ningún monitor (`video_sleep`, `libobs/obs-video.c:805`). Si un frame tarda más
  que el intervalo, se salta los intervalos perdidos y **los cuenta**
  (`lagged_frames += count - 1`). Ese contador es el «frames perdidos por retraso
  de renderizado» que ve el usuario.
- **Selección de frame por marca de tiempo** (`ready_async_frame` /
  `get_closest_frame`, `libobs/obs-source.c:4124-4228`). Cada fuente deja frames
  con PTS en una cola. En cada tick, OBS avanza su reloj de fuente con el tiempo
  de sistema y descarta los frames que ya han pasado. Además:
  - **evita duplicados innecesarios**: si el siguiente frame está a menos de 2 ms,
    espera a él;
  - **trata los saltos de marca de tiempo** (más de 2 s, `MAX_TS_VAR`) como una
    discontinuidad y **se reancla**, sin intentar alcanzar el tiempo perdido.

  Es la misma idea que el `FrameCadence` sin conectar de WinCue, pero con años de
  campo encima.
- **El reloj de referencia es el de sistema** (`os_gettime_ns`), no el audio. El
  audio y el vídeo se alinean porque los dos llevan marcas en ese mismo reloj.
- **La conversión de color va en shader:** los planos YUV se suben tal cual y un
  efecto los convierte. Formatos nativos (`media.c:30-75`): 4:2:0, 4:2:2,
  4:2:2 10 bits, 4:4:4 12 bits con y sin alfa, P010…
- **La decodificación por hardware no es zero-copy:** el frame decodificado en GPU
  se copia de vuelta a RAM (`av_hwframe_transfer_data`, `decode.c:309`) y se
  vuelve a subir.

### HAP y ProRes en OBS

- **HAP:** el decodificador HAP de FFmpeg **descomprime el DXT en la CPU** y
  entrega `RGB0`/`RGBA` (`hapdec.c:364-397`). OBS no tiene `RGB0` entre sus
  formatos nativos, así que además pasa por `swscale` en la CPU antes de subir.
  Pierde exactamente la ventaja de HAP. **Variantes:** Hap1, Hap5, HapY, HapA y
  HapM (HAP Q Alpha) sí, porque FFmpeg las decodifica todas.
- **ProRes:** FFmpeg → `yuv422p10le` / `yuva444p12le`, formatos nativos de OBS →
  subida de planos + shader. **Es la misma arquitectura que el motor ProRes de
  WinCue.** Diferencia: OBS usa los hilos por defecto de FFmpeg (`thread_count=0`,
  `decode.c:88`), mientras que WinCue fija *slice threading* y un presupuesto de
  hilos medido.

### Qué copiar de OBS

| Lección | Dónde está en OBS | Cómo aplicarla en WinCue |
|---|---|---|
| Hilo gráfico con reloj propio y contador de retrasos | `obs-video.c` `video_sleep` | Es el `OutputCompositor`: conectarlo (F2/F3) |
| Selección por PTS con histéresis de 2 ms y reanclaje tras un salto | `obs-source.c` `ready_async_frame` | Añadir a `FrameCadence` la regla del salto (reanclar, no alcanzar) |
| Telemetría siempre visible | Barra de estado: frames perdidos por render, por codificación y por red | Mostrar `CompositorStats` y los drops de `FramePacer` al operador |
| Caché de bucle en RAM para clips cortos | `media-playback/cache.c` | Opcional para loops cortos de HAP |

**Qué NO copiar:** su ruta de HAP (CPU) ni su hwdec con copia de vuelta.

---

## 2. vMix — documentación oficial

### Cómo está construido (lo que publican)

- **Toda la composición en la GPU.** La CPU se reserva sobre todo para codificar
  al grabar y al emitir ([base de conocimiento de vMix](https://www.vmix.com/knowledgebase/article.aspx/227/what-do-the-usage--performance-statistics-in-the-bottom-left-corner-of-vmix-mean)).
- **Reloj fijo a los fps maestros del proyecto.** Todas las entradas se adaptan a
  ese ritmo. Un desajuste de ritmo (30 frente a 29,97) provoca drops visibles;
  la solución que da el propio equipo de vMix es igualar el ritmo maestro
  ([foro de vMix](https://forums.vmix.com/posts/m43270-Amcrest-IP2M-841B-Framerate-Mismatch)).
- **Telemetría por etapas:** el tiempo de render por frame (si pasa de 20 ms de
  forma sostenida, la GPU está al límite) y **contadores de drops en varios puntos
  de la cadena de salida**
  ([rendimiento](https://www.vmix.com/help28/Performance.html),
  [estadísticas de salida](https://forums.vmix.com/posts/m55225-How-to-read-the-output-row-in-Statistics-page)).
- **Ficheros:** la reproducción va con un FFmpeg integrado propio (no depende de
  los códecs instalados en Windows), según su soporte
  ([foro](https://forums.vmix.com/posts/m117491-MP4-Codecs)). La guía cita
  contenedores (AVI, WMV, MPEG, MXF, MP4, QuickTime), no códecs
  ([guía de vMix 28](https://www.vmix.com/help28/vMixUserGuide.pdf)).
- **Códec propio de producción:** el vMix Video Codec, «calidad similar a ProRes
  con muy poca CPU».

### HAP y ProRes en vMix

- **HAP: no he encontrado ninguna fuente que confirme que vMix lo soporte.** Si lo
  hiciera a través de su FFmpeg, se aplicaría lo mismo que a OBS (decodificación en
  CPU).
- **ProRes:** hay usuarios que lo reproducen, incluido 4444; el cuello que citan es
  el disco. Sin datos oficiales de rendimiento.

### Qué copiar de vMix

1. **Avisar del desajuste de ritmo** entre el contenido y la salida: vMix lo
   resuelve fijando un ritmo maestro. En WinCue, avisar al cargar un cue si su fps
   no encaja con el refresco de la salida (por ejemplo, 25p sobre 60 Hz).
2. **Contadores por etapa** (decodificación → subida → composición → presentación)
   y el **umbral de alarma** (tiempo de render sostenido por encima del
   presupuesto).
3. **Elegir la GPU de alto rendimiento explícitamente:** vMix lo documenta como
   causa de drops en portátiles con dos GPU. WinCue ya lo hace (`HighPerformance` +
   emparejado por LUID).

---

## 3. Resolume Arena — documentación y personal de Resolume

### Cómo está construido (lo que publican)

- **Composición con un ritmo configurable que funciona como techo.** En «auto» se
  sincroniza con el refresco del monitor; con vsync activado, el techo lo marca la
  pantalla más lenta, y el equipo de Resolume desaconseja desactivar el vsync
  ([foro, personal de Resolume](https://resolume.com/forum/viewtopic.php?p=33035)).
- **Su recomendación para que vaya «sedoso»: que el ritmo de la composición
  coincida con el del contenido** (25 fps a 25). Si no coinciden, se repiten
  frames ([soporte](https://www.resolume.com/article/13)).
- **Su propio códec, DXV**, de la familia DXT, igual que HAP: «la descompresión se
  hace directamente en la tarjeta de vídeo», y esa aceleración solo existe dentro
  de Resolume ([Rendering to DXV](https://resolume.com/article/67)).
- **Salida avanzada** con *slices*, *warping* y *edge blending*.

### HAP y ProRes en Resolume

- **HAP:** soportado, variantes incluidas, desde Resolume 6. El propio personal lo
  describe como «no el códec más optimizado, pero mejor que H.264»: DXV es su ruta
  rápida ([foro](https://resolume.com/forum/viewtopic.php?t=19524)). Que
  descomprima HAP en la GPU **no está documentado públicamente**.
- **ProRes:** decodificación nativa; «si reproduces unos pocos a la vez, no hay
  problema» ([foro](https://resolume.com/forum/viewtopic.php?p=39823)). Sin cifras.
- **Disco:** en HAP Q Alpha, un solo flujo 4K60 satura un SSD SATA
  ([guía de códecs para VJ](https://limeartgroup.com/must-have-video-codecs-every-vj-has-to-install/);
  cifras orientativas, no de Resolume).

### Qué copiar de Resolume

1. **El ritmo de salida manda y el contenido se adapta a él**, con vsync activado
   siempre. Es exactamente lo que da el `OutputCompositor` con `Present(1)`.
2. **El ritmo de salida y el del contenido deben coincidir:** en WinCue, permitir
   fijar el refresco de cada salida (50/60 Hz) y avisar cuando el contenido no
   encaja.
3. **Tener un formato de primera clase y empujar al usuario hacia él.** Resolume
   empuja a DXV; WinCue ya tiene `HapConvertService`. Falta convertirlo en un flujo
   de ingesta (ver Watchout).

---

## 4. Watchout 7 — documentación oficial de Dataton

### Cómo está construido

```
  Producer (edición)        Director (1 por show)          Runner (1 por máquina de salida)
  ─ no reproduce el show ─► ─ servidor NTP del show ─────► ─ motor de render de audio y vídeo
                            ─ difunde el estado de          ─ sigue reproduciendo si el
                              reproducción en tiempo          Director se desconecta
                              real                          ─ reloj del timeline por NTP
  Asset Manager ────────────────────────────────────────► ─ trae los medios ya optimizados
  ─ almacena y OPTIMIZA (transcodifica) al importar
```

([Arquitectura](https://docs.dataton.com/watchout-7/.architecture.html),
[sincronía de tiempo](https://docs.dataton.com/watchout-7-new/watchout/network-setup/time-synchronization.html),
[migración desde WO6](https://docs.dataton.com/watchout-7-new/watchout/getting-started/architectural-differences-watchout-6-vs-7.html))

- **El render vive en un proceso aparte** (Runner) de la edición (Producer) y del
  control (Director). **Los Runners siguen reproduciendo aunque se caiga el
  Director.** En instalaciones desatendidas, Dataton exige que el Director corra
  en una de las máquinas de Runner, para que el show no dependa del Producer.
- **Sincronía:** NTP gestionado por Watchout (el Director es la referencia). Para
  alinear frames con *edge blending*, **genlock/framelock por hardware**, que exige
  NVIDIA RTX PRO con tarjeta Sync; las GeForce no lo admiten
  ([preguntas frecuentes de hardware](https://www.dataton.com/downloads/faq-watchout-7/what-hardware-can-i-run-watchout-7-on),
  [sincronía frame a frame](https://knowledge.dataton.com/knowledge/achieving-frame-accurate-sync-between-display-servers)).
  Con LTC, el puente reenvía la posición cada segundo para corregir la deriva.
- **Requisitos que se dan por supuestos:** el disco mínimo es un SSD SATA con más
  de 500 MiB/s sostenidos y el recomendado un **NVMe Gen4 con más de 3500 MiB/s**;
  16-32 GiB de RAM; CPU con AVX2/FMA; Vulkan 1.3 en el Producer
  ([requisitos del sistema](https://docs.dataton.com/watchout-7-new/watchout/getting-started/system-requirements.html)).
  Su guía de problemas da una cifra útil: **HAP Q 4K ≈ 40 MB de VRAM por frame**.

### HAP y ProRes en Watchout

- **HAP es su formato base:** la licencia gratuita cubre «HAP y fotos fijas»; ProRes
  y H.264 piden licencia
  ([licencias](https://knowledge.dataton.com/knowledge/how-many-watchout-7-licenses-do-i-need-in-my-setup)).
  Si el contenido ya es HAP, la optimización al importar casi no hace nada. HAP
  normal descarta el alfa: para transparencia hay que usar HAP Alpha
  ([formatos y códecs](https://docs.dataton.com/watchout-7-new/watchout/assets-asset-manager/formats-codecs.html)).
- **ProRes:** Dataton lo recomienda como **formato de entrega** desde postproducción,
  pero **por defecto lo transcodifica a NotchLC** (decodificado en GPU, ~10 bits)
  al importar. Hay usuarios que dicen que ProRes ya se reproduce mejor porque
  «Dataton controla ahora su propio decodificador», y un hilo del foro de partners
  critica la calidad de esa transcodificación
  ([foro](https://forum.dataton.com/topic/5215-best-video-format-for-playback/)).
  Las páginas de Dataton no aclaran del todo cuándo se reproduce ProRes nativo.

### Qué copiar de Watchout (lo más importante de todo el documento)

1. **Separar el render de la interfaz.** En Watchout, que la interfaz se cuelgue no
   para la sala. En WinCue, hoy, el último paso de cada frame (copiar a la
   superficie dentro del `Lock` del `D3DImage`) se ejecuta **en el hilo de interfaz
   de WPF**. Pasos:
   - **(a)** compositor con hilo y swapchain propios (F2/F3 del plan): la interfaz
     deja de estar en el camino del frame;
   - **(b)** más adelante, el render en un **proceso aparte** controlado por la
     aplicación, que sobreviva a un cuelgue de la interfaz (y aísle también el
     riesgo del decode nativo de ProRes, ya documentado en
     [arquitectura-motor-video.md](arquitectura-motor-video.md)).
2. **Ingesta con optimización y validación.** El Asset Manager convierte al formato
   de reproducción **antes** del show, no durante. Para WinCue, un paso de
   «preparar show» que, por cada medio:
   - compruebe el códec y la variante;
   - compruebe que el fps encaja con la salida;
   - compruebe el tamaño de los *chunks* HAP;
   - mida el bitrate pico frente al disco de la máquina;
   - ofrezca convertir lo que no sea HAP o ProRes.
3. **Requisitos de hardware escritos y comprobados al arrancar:** disco (prueba de
   lectura sostenida), GPU, refresco de la pantalla. Watchout los publica; WinCue
   debería medirlos y avisar.
4. **Cuando llegue la multi-máquina**, NTP gestionado por la aplicación + genlock
   opcional por hardware. Hoy WinCue es una sola máquina, y ahí la regla es un solo
   hilo que presenta todas las salidas (decisión ya tomada en
   [plan-compositor.md](plan-compositor.md)).

---

## 5. Comparativa centrada en HAP y ProRes

| | **WinCue** | OBS | vMix | Resolume | Watchout 7 |
|---|---|---|---|---|---|
| HAP: dónde se descomprime el DXT | **GPU** (decodificador propio) | CPU (FFmpeg) + swscale | Sin confirmar que lo soporte | No documentado (DXV en GPU) | Formato base, optimizado al importar |
| HAP Q Alpha (`HapM`) | **❌ no soportado** | ✅ vía FFmpeg | — | ✅ (variantes) | ✅ (HAP Alpha documentado) |
| HAP R / HAP HDR | ❌ | Según FFmpeg | — | No documentado | No documentado |
| ProRes: decode | FFmpeg en proceso, slice threading con presupuesto medido | FFmpeg, hilos por defecto | FFmpeg integrado | Nativo | **Transcodifica a NotchLC por defecto** |
| ProRes: color | Shader propio (709/601/2020, 10/12 bits) | Shader | No documentado | No documentado | — |
| Reloj de presentación | **DWM** (sin reloj propio) | Hilo gráfico a fps del proyecto | Fps maestro del proyecto | Composición ≤ refresco, vsync | Timeline por NTP + genlock HW |
| El frame pasa por el hilo de interfaz | **Sí** (copia dentro del `Lock` del `D3DImage`) | No | No (por diseño) | No documentado | No: proceso aparte |
| Lectura de disco | `FileStream` síncrono en el hilo de decode, sin lectura por delante | libavformat | — | — | NVMe Gen4 recomendado |
| Contadores de drops a la vista | No (solo en trazas) | Sí | Sí, por etapa | Fps de la composición | NTP Diff por nodo |
| Ingesta / conversión | `HapConvertService` manual | No | Conversor aparte | Alley (DXV) | Asset Manager automático |

---

## 6. Plan para que HAP y ProRes vayan a la perfección

Ordenado por lo que más robustez da por esfuerzo. Los puntos 1 y 2 son los que
separan «funciona en mi PC» de «funciona en directo en el PC de un cliente».

| # | Qué | Por qué | Referencia |
|---|---|---|---|
| 1 | **Conectar el `OutputCompositor`** para HAP y ProRes primero (`HapSource` ya existe; falta `ProResSource`). Swapchain por salida, `Present(1)`, un hilo que presenta todas | Saca el frame del hilo de interfaz, da vsync real, sincroniza las capas y el genlock entre salidas | Resolume, OBS, vMix |
| 2 | **Lectura de disco asíncrona por delante** en `HapFrameSource` y `ProResFrameSource` (`RandomAccess.ReadAsync` con varios frames en vuelo; valorar `FILE_FLAG_NO_BUFFERING`) | Hoy, un pico de latencia del disco es un frame perdido. Watchout pide NVMe por algo | Watchout |
| 3 | **Telemetría visible**: drops por etapa (decode, subida, composición, presentación), peor frame y alarma cuando se pasa del presupuesto | Sin esto no se aprende de los fallos en campo | vMix, OBS |
| 4 | **Ritmo de salida frente a contenido**: fijar el refresco por salida y avisar de los desajustes (25p en 60 Hz). La cadencia correcta (2:2, 3:2) ya la da `FrameCadence` | Es la recomendación número uno de Resolume y vMix | Resolume, vMix |
| 5 | **HAP Q Alpha** (`HapM`: BC3 YCoCg + BC4 alfa, dos texturas) y, después, HAP R (BC7) | Es la variante de alta calidad con alfa. Hoy cae a mpv, que la descomprime en CPU | OBS/FFmpeg, Resolume |
| 6 | **«Preparar show»**: validar cada medio (códec, variante, fps, *chunks*, bitrate frente a una prueba de disco) y ofrecer conversión | Mover los fallos de la función al ensayo | Watchout |
| 7 | **Selección por PTS con reanclaje tras un salto** (regla de `MAX_TS_VAR` de OBS) en `FrameCadence` | Robustez frente a discontinuidades (loops, *seeks*) | OBS |
| 8 | **Render en un proceso aparte** (después de 1) | Un cuelgue de la interfaz o un fallo nativo de ProRes no apagan la sala | Watchout |

---

## 7. Sobre usar código de OBS y la ingeniería inversa

### Código de OBS

- **El núcleo de OBS (`libobs`) y casi todo el proyecto son GPLv2 o posterior.**
  Copiar código de ahí obliga a que el programa que lo incluye se distribuya bajo
  GPL.
  - El `LICENSE` del repositorio de WinCue es **GPLv3**, que es compatible.
  - Pero `docs/legal/checklist-distribucion.md` trabaja con la idea de **distribuir
    WinCue cerrado** (por eso tanto esfuerzo en que FFmpeg y libmpv sean LGPL).
  - **Si el plan es cerrado, no se puede copiar código de `libobs`.** Leerlo,
    entenderlo y reimplementar las ideas sí: las ideas y los algoritmos no tienen
    copyright, el texto del código sí.
- **Excepción útil:** `shared/media-playback/` (el reproductor de ficheros de OBS
  sobre FFmpeg: decode, colas, sincronía, caché de bucle) lleva **licencia ISC**,
  que es permisiva. Se puede usar incluso en un producto cerrado, conservando el
  aviso de copyright. Para HAP no interesa (decodifica en CPU), pero es una buena
  referencia para la ruta genérica (H.264/HEVC) si algún día se quiere sustituir
  mpv.
- **Para HAP y ProRes, OBS no tiene nada mejor que lo que WinCue ya tiene.** Lo que
  vale la pena estudiar de OBS es la temporización (`obs-video.c`,
  `obs-source.c`), no sus decodificadores.

### Ingeniería inversa de vMix, Resolume o Watchout

No sé qué es exactamente «REA»; si es una herramienta o un servicio concreto,
dímelo y lo tengo en cuenta. En general:

- Sus licencias de usuario suelen **prohibir descompilar**. La ley española
  (art. 100 de la Ley de Propiedad Intelectual, que transpone la Directiva
  2009/24/CE) solo permite descompilar **para lograr interoperabilidad** con un
  programa creado de forma independiente, y **no** para copiar el funcionamiento
  interno de un competidor. Antes de seguir por ahí, conviene consultarlo con
  alguien de propiedad intelectual.
- **Lo que sí es limpio y da casi todo el valor: medir desde fuera** (caja negra).
  Usar los mismos clips HAP y ProRes en cada producto y comparar:
  - drops con una cámara de alta velocidad o un contador de frames incrustado en
    el vídeo;
  - CPU, GPU y disco con PresentMon y el Monitor de rendimiento;
  - latencia del GO;
  - comportamiento con el disco saturado o la interfaz bloqueada.

  Eso dice qué hacen bien sin tocar su código, y sirve además como banco de
  regresión para WinCue.
- Los formatos de fichero abiertos (HAP: especificación pública de Vidvox) se
  pueden implementar sin problema, como ya se hace.

---

## Fuentes

**Código** (OBS en el commit `72e8665`):
- `libobs/obs-video.c` y `libobs/obs-source.c` — [obsproject/obs-studio](https://github.com/obsproject/obs-studio)
- `shared/media-playback/` (ISC)
- [`libavcodec/hapdec.c`](https://github.com/FFmpeg/FFmpeg/blob/master/libavcodec/hapdec.c)

**Watchout:**
- [Arquitectura de Watchout 7](https://docs.dataton.com/watchout-7/.architecture.html)
- [Sincronía de tiempo](https://docs.dataton.com/watchout-7-new/watchout/network-setup/time-synchronization.html)
- [Sincronía frame a frame entre servidores](https://knowledge.dataton.com/knowledge/achieving-frame-accurate-sync-between-display-servers)
- [Requisitos del sistema](https://docs.dataton.com/watchout-7-new/watchout/getting-started/system-requirements.html)
- [Formatos y códecs](https://docs.dataton.com/watchout-7-new/watchout/assets-asset-manager/formats-codecs.html)
- [Buenas prácticas de codificación](https://knowledge.dataton.com/knowledge/video-encoding-best-practices-for-watchout)
- [Licencias](https://knowledge.dataton.com/knowledge/how-many-watchout-7-licenses-do-i-need-in-my-setup)
- [Hardware (FAQ)](https://www.dataton.com/downloads/faq-watchout-7/what-hardware-can-i-run-watchout-7-on)
- [Puente LTC](https://docs.dataton.com/watchout-7-new/watchout/external-control/ltc-bridge.html)
- [Foro: mejor formato de reproducción](https://forum.dataton.com/topic/5215-best-video-format-for-playback/)

**Resolume:**
- [Rendering to DXV](https://resolume.com/article/67)
- [Ritmo de composición](https://www.resolume.com/article/13)
- [Foro: vsync y fps](https://resolume.com/forum/viewtopic.php?p=33035)
- [Foro: HAP en Resolume 7](https://resolume.com/forum/viewtopic.php?t=19524)
- [Foro: DXV y ProRes](https://resolume.com/forum/viewtopic.php?p=39823)
- [Guía de códecs para VJ (cifras de disco)](https://limeartgroup.com/must-have-video-codecs-every-vj-has-to-install/)

**vMix:**
- [Rendimiento](https://www.vmix.com/help28/Performance.html)
- [Estadísticas de uso](https://www.vmix.com/knowledgebase/article.aspx/227/what-do-the-usage--performance-statistics-in-the-bottom-left-corner-of-vmix-mean)
- [Guía de usuario de vMix 28](https://www.vmix.com/help28/vMixUserGuide.pdf)
- [Foro: desajuste de ritmo](https://forums.vmix.com/posts/m43270-Amcrest-IP2M-841B-Framerate-Mismatch)
- [Foro: estadísticas de salida](https://forums.vmix.com/posts/m55225-How-to-read-the-output-row-in-Statistics-page)
- [Foro: códecs MP4](https://forums.vmix.com/posts/m117491-MP4-Codecs)
- [Base de conocimiento: optimizar rendimiento](https://www.vmix.com/knowledgebase/article.aspx/32/optimising-performance)
