# Arquitectura del motor de vídeo

*Escrito 2026-07-26. Objetivo declarado: nivel vMix en el motor.*

**Este documento es la referencia de diseño: el porqué y el conjunto.** Para
ejecutar, ir a [plan-compositor.md](plan-compositor.md), que se basta solo y ya
incorpora estas capas en sus fases.

## Respuesta corta a "¿llegamos con el plan anterior?"

**No.** Aquel plan construye los dos primeros pisos bien, pero da por hecha una
**lista plana de capas**, y eso es exactamente la decisión que no se puede
deshacer barato: retrofitear un grafo de composición sobre una lista plana es
reescribir el compositor. Todo lo demás que falta (relojes por PTS, sinks,
espacio de color, fuentes nuevas) **sí** se puede añadir después sin tocar
cimientos.

Conclusión operativa: **un solo cambio en el plan vigente** — que F1/F2
construyan el grafo, no la lista. El resto del plan sigue válido tal cual.

---

## Principios

1. **Todo es una textura en UN device.** Ninguna fuente, efecto ni salida
   existe fuera de él.
2. **El tiempo es explícito.** Cada frame lleva su PTS. Nadie usa "el último
   que haya llegado" como sincronía.
3. **El grafo es dato, no código.** Transiciones, PiP, canvas, multiview y
   efectos son el MISMO mecanismo, no cinco casos especiales.
4. **Nada bloquea el hilo de render.** Los productores publican; el compositor
   toma lo que esté listo.

## Vista general

```
 L5  CONTROL          cue engine → mutaciones del grafo agendadas por timestamp
     ─────────────────────────────────────────────────────────────────────────
 L4  SINKS            SwapChain(pantalla) · Readback→ Record / NDI / Multiview
     ─────────────────────────────────────────────────────────────────────────
 L3  GRAFO            source → transform → effect → mix → output
                      (nodos, RTs intermedias de pool, ping-pong)
     ─────────────────────────────────────────────────────────────────────────
 L2  RELOJ            master clock por salida · selección de frame por PTS
                      audio = maestro, vídeo esclavo · genlock entre salidas
     ─────────────────────────────────────────────────────────────────────────
 L1  FUENTES          Hap · Mpv(GL interop) · Imagen · Título(D2D) · Captura
                      [después] NDI in · DeckLink
     ─────────────────────────────────────────────────────────────────────────
 L0  GPU              ID3D11Device único · pools de textura/staging/query
                      política de formato y espacio de color
```

---

## L0 — Fundamento GPU

- **`GpuDevice`**: un `ID3D11Device`, adaptador HighPerformance, `BgraSupport`,
  **multithread-protected**. Lo tocan el hilo GL, los de decodificación y el de
  render.
- **Pools**: anillo de texturas por fuente, staging para readback, y un pool de
  *queries* para medir coste real por nodo en GPU (sin esto no hay diagnóstico
  posible en máquina de cliente).
- **Espacio de trabajo — la decisión que separa "correcto" de "parece bien".**
  Mezclar en 8 bits sRGB es matemáticamente incorrecto: los fundidos se van
  oscuros a mitad de recorrido. La arquitectura contempla dos modos:
  - `RGBA8` sRGB — barato, lo que hace todo el mundo (vMix incluido).
  - `RGBA16F` **luz lineal** — se linealiza al muestrear, se mezcla, se vuelve
    a codificar al presentar. Un fundido cruzado deja de tener bache en el
    centro. Es el punto donde se puede estar **por encima** de vMix, no a la
    par, y abre 10-bit/HDR sin rediseño.

  Se decide por salida, no global.

## L1 — Fuentes

Contrato único: una fuente **publica frames con PTS** en un anillo de texturas
propio, con índice atómico de "última lista".

El anillo no es optimización, es corrección: mientras GL tiene el lock del
interop la textura es suya y D3D no puede muestrearla (verificado en el lab).

| Fuente | Estado | Notas |
|---|---|---|
| `HapSource` | ya existe | decodificador propio; solo hay que quitarle la mitad D3D9Ex |
| ProRes | **ya existe** (paso 2) | decodificador de FFmpeg en proceso → planos `R16_UNORM` → shader YUV→RGB. Le falta lo mismo que a HAP: quitarle la mitad D3D9Ex y publicar en el anillo. Ver la sección propia más abajo |
| `MpvSource` | probado en lab | GL → `GL_TEXTURE_2D` vía `WGL_NV_DX_interop2` |
| `ImageSource` | trivial | fija |
| `TitleSource` | nuevo, barato | **Direct2D/DirectWrite interoperan nativamente con D3D11**: texto, formas y rótulos se rinden directos a textura. Es el equivalente del GT Title Engine y cuesta poco |
| `CaptureSource` | nuevo, barato | `Windows.Graphics.Capture` entrega texturas D3D11 directamente. Pantalla o ventana. Para corporate AV es una feature real |
| `NdiSource`, `DeckLinkSource` | fuera de alcance | ingesta viva = otro producto (ver abajo) |

## L2 — Reloj y sincronía

Esto es lo que vMix llama *frame sync*, y es la capa que el plan anterior
trataba de refilón.

- **Un master clock por salida**, marcado por el vsync del swapchain.
- Cada frame el compositor no pregunta "¿qué hay?" sino **"¿qué frame
  corresponde al instante T?"**, y cada fuente devuelve el de PTS más cercano.
  Esa única regla es la diferencia entre judder y una cadencia limpia cuando
  hay 30p en una salida de 60 Hz, o 24p en 60 (3:2 correcto, gratis).
- **El audio es el maestro; el vídeo se esclaviza.** Correcto para un lanzador
  de cues: un glitch de audio se oye, un frame repetido no se ve. Es lo que ya
  hacen `MpvSyncPll` y el reloj de HAP — aquí se generaliza como regla del
  motor, no como parche por ruta.
- **Genlock entre salidas**: un solo hilo presenta todas. Dos proyectores
  desincronizados se ven en sala; un frame de latencia, no.

## L3 — El grafo de composición

**El cambio de fondo respecto al plan anterior.** No una lista de capas: un
grafo pequeño evaluado a lista de comandos en cada frame.

Tipos de nodo:

- **source** — una fuente de L1
- **transform** — escala, recorte, rotación, *corner-pin* (proyección sobre
  superficies no planas)
- **effect** — corrección de color, key, desenfoque, LUT
- **mix** — fundido cruzado, wipe, modo de fusión, dip-to-black
- **output** — a un sink de L4

Por qué grafo:

| Con grafo | Con lista plana |
|---|---|
| Una transición es un nodo `mix` | Caso especial en el bucle |
| PiP es un `transform` | Otro caso especial |
| Modo canvas es un `output` con recorte | Otro más |
| Multiview es un grafo que consume nodos del grafo principal | Re-render aparte |
| Efectos encadenables | No encadenables |

Las RTs intermedias salen de un pool con ping-pong. El grafo también **resuelve
solo** el problema del congelado: la vida visual de un cue es la vida de su
nodo, y "congelar" es no retirarlo todavía — `VisualFreezeRegistry` deja de
tener razón de existir.

## L4 — Sinks

- **`SwapChainSink`** — pantalla física, flip-model, `Present(1)`.
- **`ReadbackSink`** — el punto de extracción que hoy no existe. **Obligatorio
  que sea asíncrono**: `CopyResource` a staging + fence, y se mapea N frames
  después. Leer síncrono aquí para el hilo de render — es el error clásico de
  este diseño.
- Encima del readback: **`RecordSink`** (NVENC/AMF/QSV), **`NdiSink`**,
  **`MultiviewSink`**.

`MultiviewSink` sale casi gratis por L3: es otro compositor pintando miniaturas
de nodos del grafo principal. Sin grafo, es un motor de render entero aparte.

## L5 — Control

El motor de cues no pinta: **muta el grafo en un instante dado**. Un GO es una
mutación agendada con timestamp. Eso da **determinismo**, que es justo lo que
un lanzador de cues necesita y lo que vMix —diseñado para conmutación manual en
directo— no ofrece.

## Modelo de hilos

- 1 **hilo de render por salida** (con genlock, uno presenta por todas).
- N **hilos de fuente** (decodificación + subida), cada uno dueño de su anillo.
- 1 **hilo GL** para las fuentes mpv — lo impone el lock del interop.
- El hilo de audio, intacto.
- Regla dura: **ninguna fuente bloquea nunca al compositor**. Si un frame no
  está listo, se repite el anterior y se cuenta como *drop* en la telemetría.

---

## El motor ProRes propio (paso 2 — EN PRODUCCIÓN)

Segundo códec con motor propio, después de HAP. La diferencia de fondo con HAP
hay que tenerla clara porque cambia qué se gana:

- **HAP** se decodifica solo (descomprimir Snappy y darle el DXT a la GPU) y
  además **rompe el interop del compositor con mpv**: sin motor propio, un cue
  HAP se ve pero pierde los fundidos de opacidad.
- **ProRes** es DCT con codificación entrópica —hay que decodificarlo de verdad
  en CPU— y **mpv lo renderiza perfectamente**. Aquí el motor propio no sirve
  para "poder verlo", sirve para otras dos cosas:
  1. **Se acaba la doble decodificación.** Hoy un cue ProRes 4K60 se decodifica
     dos veces (el player de la salida y el mini-monitor de PGM) y se lee dos
     veces del disco. Con motor propio + PGM espejo, una sola vez.
  2. **La conversión de color pasa de la CPU a la GPU.** swscale convierte
     YUV→RGB en CPU dentro de mpv; aquí lo hace el shader en 0,02-0,08 ms.

### El decodificador es de FFmpeg, el motor es nuestro

Escribir un decodificador de ProRes desde cero está **descartado**: meses de
trabajo para algo más lento que el de FFmpeg, optimizado en SIMD durante más de
una década. Se usa ese decodificador **en proceso** (bindings, no subproceso).
Todo lo demás sigue siendo nuestro: demuxer, ring, reloj, pacer, presentación y
GPU. La misma tubería que HAP.

```
.mov ──► MovDemuxer (WinCue.Video.Containers, COMPARTIDO con HAP)
          │  tabla de samples + fourcc + colr + depth
          ▼
        ProResFrameSource (hilo de decode, ring de 3)
          │  ProResDecoder (avcodec, FF_THREAD_SLICE) ──► AVFrame
          │  av_frame_move_ref ──► ProResFrameSlot  (SIN copiar)
          ▼
        ProResCuePlayer (hilo de presentación, timeBeginPeriod(1))
          │  reloj = audio espejo del cue (HapMediaClock) · pacer = FramePacer
          ▼
        ProResVideoSession  N texturas R16_UNORM (una por plano)
          ▼
        ProResGpuPipeline   shader YUV→RGB ──► RT B8G8R8A8 COMPARTIDA
          ▼
        D3D9Ex (mismo adaptador por LUID) ──► D3DImage del compositor
```

Del lado del compositor, ProRes y HAP son **indistinguibles**: la misma render
target compartida y la misma superficie de presentación. Por eso un crossfade
ProRes↔HAP funciona sin nada especial.

### Decisiones, y qué las respalda

| Decisión | Por qué |
|---|---|
| **`codec_tag` desde el fourcc del `stsd`** | Sin él, FFmpeg no deduce el perfil y un ProRes 4444 sale como `yuva444p10le`: **se pierden dos bits por muestra**. Es lo ÚNICO imprescindible que el contenedor le aporta al decodificador. Con test de regresión. |
| **`FF_THREAD_SLICE`, nunca `FF_THREAD_FRAME`** | Frame-threading da 10-25 % más de throughput pero retrasa la salida `thread_count−1` paquetes (estropea arranque y seek) y multiplica la RAM por 3-5 — 4K 4444: 424 MB con slice, **1 944 MB** con frame. |
| **Hilos por capa ≈ lógicos/capas, suelo 2, techo 8** | Medido con 3 × 4K 422 HQ: 4 hilos/capa = 58,5 fps (no llega a 60), 6 = 82,5, **8 = 101,4**, 12 (sobresuscrito) = 105,5 (ya no gana). |
| **`Reset()` en cada salto no secuencial** | Tras un drenaje el contexto queda en modo EOF **para siempre**. En producción eso pasa en cada pasada de loop y en cada seek, no es un caso raro. |
| **El color manda desde el BITSTREAM** | ffmpeg **no escribe** el átomo `colr` al codificar ProRes salvo con `-movflags +write_colr`; el color viaja en la cabecera del frame (`AVFrame.colorspace`). El `colr` del contenedor queda de respaldo para ficheros de otras herramientas. |
| **Croma POINT, no lineal** | Es lo que hace swscale (y por tanto mpv). Con muestreo lineal el PSNR contra ffmpeg cae de 48,0 dB a 33,9 dB con máx \|Δ\| = 118. No es peor imagen: es otra decisión de upsampling. Se elige POINT para que el operador vea EXACTAMENTE lo mismo que veía antes. |
| **Un plano = una textura `R16_UNORM`** | Las muestras de FFmpeg ya son `uint16` LE: la subida es copia de filas sin tocar los datos y la GPU normaliza al muestrear. La corrección de profundidad (10/12 bits) y de rango se hace con **una multiplicación y una suma** en el shader. |
| **Los frames NO se copian** | Cada slot del ring posee su `AVFrame` y el decodificador le traspasa la referencia (`av_frame_move_ref`). Copiar costaría un memcpy de **63 MB por fotograma** en 4K 4444, en el hilo que menos margen tiene. |
| **Ring de 3, no de 4 como HAP** | Un slot de HAP es un array DXT (8 MB en 4K); aquí es un frame entero (31,6 MB en 4K 4:2:2, 63,3 en 4444). Tres slots de 4K 4444 ya son 190 MB por cue. |
| **La clave de la sesión lleva el FORMATO DE PÍXEL** | La spec de las texturas no depende solo del tamaño: 4:2:2 de 10 bits son 3 planos con el croma a media anchura; 4444 de 12 son 4 planos completos. El slot del pool cachea su sesión y la reconstruye solo cuando cambia `{W}x{H}|{layout}`. |

### El cuello de botella se MUEVE

Con HAP el protagonista era el disco. Con ProRes es primero el decode y
después —cosa nueva— **la subida a GPU**:

| | HAP 4K (BC3) | ProRes 4K 422 | ProRes 4K 4444 |
|---|---:|---:|---:|
| Bytes por frame a la GPU | 8,29 MB | **31,64 MB** (3,8×) | **63,28 MB** (7,6×) |
| Subida medida (p50) | 0,32 ms | **1,46 ms** | **3,47 ms** |
| A 60 fps, por capa | 0,5 GB/s | **1,9 GB/s** | **3,8 GB/s** |

El shader es gratis (0,02-0,08 ms sobre un presupuesto de 16,7 ms). La subida no.

### Límites conocidos (medidos, no estimados)

| Objetivo | Veredicto |
|---|---|
| 2-3 × 1080p60, cualquier perfil 4:2:2 | ✅ con margen enorme (×6 en el peor caso realista) |
| 2-3 × 1080p60 4444 | ✅ |
| 2 × 4K60 4:2:2 | ✅ cómodo |
| 3 × 4K60 4:2:2 | ✅ ~30 % de margen. Requiere NVMe: 3 × 220 MB/s ≈ 660 MB/s sostenidos |
| 2 × 4K60 4444 | ⚠️ al filo, sin colchón para picos |
| **3 × 4K60 4444** | ❌ **no, y la culpa NO es del códec**: son 11,4 GB/s de subida contra los ~10,3 GB/s que sostiene el bus de esta máquina. El decode aguanta de sobra (99 fps por capa). |

Si algún día hace falta, la palanca es la SUBIDA, no el decodificador: empaquetar
a `R10G10B10A2` (−37 % de bytes) a costa de CPU, subir el alfa solo cuando el cue
lo use (−25 %), o convertir a RGBA8 en un compute shader y cachear.

### Elegibilidad y apagados

Un cue entra por el motor propio solo si: **ProRes en `.mov`** + `rate == 1.0` +
compositor GPU activo + **sin canvas** + geometría **FULL**. Todo lo demás va por
mpv como siempre.

Y hay **dos interruptores**, los dos a propósito:

- **Ajustes → Vídeo → «Motor propio para ProRes»** (encendido por defecto). No es
  capricho: con mpv el decodificador iba por subproceso y no podía tirar la
  aplicación; **aquí es nativo y en proceso, y sí puede**. El operador tiene que
  poder quitarlo de en medio sin tocar variables de entorno.
- **`WINCUE_PRORES_ENGINE=0`** — red de seguridad de directo, como la de HAP: se
  apaga en caliente sin abrir Ajustes.

Con cualquiera de los dos apagado, ProRes vuelve a mpv exactamente como antes y
**sin perder el audio por el rack** (la ruta híbrida sigue recogiéndolo).

**Fallback por cue**, no global: si falla el demux, faltan las DLL, el fourcc no
es ProRes o el pipeline GPU no arranca, ESE cue cae a mpv **muteado**
(`ExternalAudio`) y el resto del show sigue con motor propio. El audio ya suena
por el espejo: nunca audio doble, nunca negro. A diferencia de HAP **no** se
fuerza la ruta clásica, porque mpv sí sabe renderizar ProRes: el cue conserva
sus fundidos de opacidad.

### El riesgo que no se puede eliminar

**Hoy ffmpeg va por subproceso y no puede tirar la aplicación; en proceso, sí
puede.** Un fallo de memoria dentro del código nativo no es capturable desde C#.
Lo que sí se hace:

- **Validación del contenedor antes de tocar nada nativo** (`MovDemuxer.Validate`:
  dimensiones acotadas, tamaños de sample positivos y acotados, offsets dentro
  del fichero).
- **Padding obligatorio**: `avcodec_send_packet` exige `AV_INPUT_BUFFER_PADDING_SIZE`
  bytes a cero detrás de los datos. Por eso los paquetes solo se pueden construir
  desde `PaddedBuffer` (memoria nativa `av_malloc`, alineada para SIMD).
- **Lista cerrada de formatos de píxel**: mejor una excepción clara que
  interpretar punteros con el layout equivocado.
- **Log nativo bajo control**: `av_log_set_level(AV_LOG_ERROR)` al cargar.
- **Fuzzing** (en el laboratorio): 600 iteraciones con bitstream corrompido,
  0 caídas — el decodificador devuelve `AVERROR_INVALIDDATA` y viaja como
  excepción de C#.
- Los dos interruptores de arriba.

Opción nuclear si algún día aparece inestabilidad real en campo: mover el decode
a un **proceso aparte** con memoria compartida. Encarece la latencia y complica
el ring; no se hace hasta que haga falta.

### Licencia: no es un detalle de empaquetado

FFmpeg se distribuye **como bibliotecas** (`avcodec-62`, `avutil-60`,
`swresample-6`), no como ejecutable, y eso cambia la obligación legal. Solo vale
**LGPL con enlace dinámico**:

- `FFmpegLoader` fija `ffmpeg.RootPath` a `ffmpeg-lgpl\bin` y **NO cae al PATH
  del sistema** (donde suele haber un ffmpeg GPL). `WINCUE_FFMPEG_DIR` permite
  apuntar a otra carpeta y es **estricta**: si no tiene avcodec, falla en vez de
  buscar en otro sitio.
- `publish.ps1` copia las DLL y su procedencia (`ffmpeg-BUILD.txt` con SHA256 por
  DLL) y **ejecuta `tools/verificar-ffmpeg` sobre el paquete**; si el veredicto
  no es LGPL, borra las DLL y aborta la publicación.
- El `ffmpeg.exe`/`ffprobe` del PATH sigue usándose por **subproceso** (conversión
  a HAP, sondeos, formas de onda) y eso no contamina: es un caso distinto.

Detalle completo en `docs/legal/checklist-distribucion.md`.

### Qué queda por hacer

1. **Publicar en el anillo de L1** en vez de dibujar a su propia RT compartida
   (lo mismo que le queda a `HapSource`): entonces el compositor propio podrá
   mezclar ProRes con el resto sin pasar por D3D9Ex.
2. **Prefetch de disco de verdad.** El decode lee con `FileStream` síncrono. Con
   3 × 4K60 422 HQ de bitrate nominal son ~660 MB/s sostenidos y vuelve a ser el
   protagonista, igual que pasó con HAP.
3. **Máquina solo-iGPU**: la subida de 32-63 MB/frame sobre memoria compartida se
   comporta distinto que sobre una dGPU con PCIe dedicado. Sin medir.
4. **`.mxf` queda fuera**, declarado: el demuxer es de QuickTime. Si algún día
   hace falta, la salida natural es un demuxer propio de MXF OP1a.
5. **CUSTOM (pan/zoom/rotación) sigue siendo de mpv.** El motor propio solo sabe
   letterbox + escala sobre la superficie FULL.

---

## Lo que esto NO incluye, y por qué

**Ingesta viva** (SDI, HDMI, NDI de entrada) y **chroma key** quedan fuera. No
por dificultad —son SDKs públicos— sino porque son el corazón de *otro*
producto: un mezclador de directo. La arquitectura los admite (serían fuentes
de L1 y un nodo de efecto de L3) sin rediseño, que es lo que importa: la puerta
queda abierta, no atravesada.

Y una cosa que ninguna arquitectura compra: **la cola larga de fiabilidad**. El
activo real de vMix son casi dos décadas de "en este portátil con este driver
pasa esta cosa rara". Eso se acumula en campo, no se diseña. Por eso mpv sigue
siendo el fallback universal y por eso la telemetría de drops de L2 y las
queries de L0 están en la arquitectura desde el principio: son el instrumento
para acumular esa cola long tail deprisa.

## Dónde se construye cada capa

| Capa | Fase del plan |
|---|---|
| L0 · GPU | F1 (`GpuDevice`, pool de queries, formato como parámetro) |
| L1 · Fuentes | F1 (`FrameRing`, `HapSource`, `MpvSource`) · F4 (título, captura) |
| L2 · Reloj | F1 (selección por PTS) · F2 (master clock) · F3 (genlock) |
| L3 · Grafo | F2 (source/transform/mix/output) · F4 (nodos `effect`) |
| L4 · Sinks | F2 (swapchain) · F4 (readback asíncrono, multiview, record, NDI) |
| L5 · Control | F2–F3 (GO = mutación agendada del grafo) |

El espacio de trabajo lineal (`RGBA16F`) queda en F4 a propósito: cambia el
aspecto de todos los fundidos y encenderlo antes de tener paridad haría
imposible comparar la ruta vieja con la nueva.
