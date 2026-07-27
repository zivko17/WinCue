# prores-lab — ProRes en el motor de vídeo propio (PASO 1: laboratorio)

Hermano de `tools/hap-lab`, con una diferencia de fondo: **HAP se decodifica
solo** (descomprimir Snappy y darle el DXT a la GPU), **ProRes no**. ProRes es
DCT con codificación entrópica y hay que decodificarlo de verdad en CPU.
Escribir ese decodificador desde cero está **descartado** (meses de trabajo y
sería más lento que el de FFmpeg, optimizado en SIMD durante años).

**Decisión de partida, ya tomada antes de este laboratorio:** usar el
decodificador de FFmpeg **en proceso** (bindings, no subproceso) alimentando
NUESTRA tubería. El decodificador es de ellos; el motor sigue siendo nuestro
(demuxer, ring, reloj, pacer, presentación, GPU).

Este laboratorio responde a si eso **funciona, es correcto y aguanta el
directo**, y a qué haría falta para meterlo en `src/` (que será el paso 2).

**Veredicto corto:** sí para 1080p (con muchísimo margen) y para 4K 4:2:2
(hasta 3 simultáneos con ~30 % de margen en el peor caso realista). **4K 4444 a
60 fps se queda en el filo** con 3 capas. Y el cuello de botella se MUEVE: con
HAP era el disco, con ProRes es primero el decode y después —cosa nueva— la
**subida a GPU**, que pasa de 8 MB/frame (BC3 4K) a 32-63 MB/frame.

---

## Cómo usarlo

```powershell
# 1. Bajar las DLL LGPL de FFmpeg (no versionadas)
powershell -File obtener-ffmpeg-lgpl.ps1

# 2. Verificar que son LGPL de verdad (sale 0 solo si lo son)
dotnet run --project ProResLab.csproj -c Release -- --licencia

# 3. Generar la media de benchmark (ffmpeg del PATH; ~3,4 GB, no versionada)
powershell -File generar-media.ps1

# Info de un fichero (demux + primer frame)
dotnet run -c Release -- media\bench-1080-422hq.mov

# Benchmark de decode: 1 hilo / slice / frame / slice+frame
dotnet run -c Release -- media\bench-1080-422hq.mov --bench --threads 24 --repeat 3

# N vídeos simultáneos (la pregunta del directo)
dotnet run -c Release -- media\bench-2160-422hq.mov --concurrentes 3 --threads-por-video 4

# Robustez con bitstream corrompido
dotnet run -c Release -- media\bench-1080-422hq.mov --fuzz 400

# Camino GPU: verificación de imagen y benchmark de la cadena completa
dotnet run --project ProResLab.Gpu -c Release -- media\bench-1080-422hq.mov --psnr --frame 10
dotnet run --project ProResLab.Gpu -c Release -- media\bench-2160-422hq.mov --bench

# Tests (generan su propia media pequeña 320x240 si falta)
dotnet test prores-lab.sln
```

`prores-lab.sln` es una solución APARTE: no toca ni entra en `WinCueSharp.sln`.

---

## 1. Elección de binding

Se compararon los dos candidatos reales del ecosistema .NET. Los dos son
generadores automáticos sobre las cabeceras de FFmpeg (de hecho **Sdcb.FFmpeg
es un fork de FFmpeg.AutoGen**), así que la calidad del binding en sí no
distingue; lo que distingue es licencia, mantenimiento y con qué build de
FFmpeg encajan.

| Criterio | **FFmpeg.AutoGen 8.1.0** ✅ | Sdcb.FFmpeg 7.0.0 |
|---|---|---|
| Licencia del wrapper | **MIT** (relicenciado en 2025; hasta 7.x era LGPL-3.0) | **LGPL-3.0-only** |
| Última versión en NuGet | 8.1.0 → **FFmpeg 8.1** | 7.0.0 → FFmpeg 7.0 |
| Build LGPL prebuilt que empareje | `ffmpeg-n8.1-latest-win64-lgpl-shared` (etiqueta `latest` de BtbN) | n7.0 ya NO está en `latest`: habría que rebuscar en autobuilds antiguos |
| Control de qué DLL se cargan | `ffmpeg.RootPath` → carpeta explícita | igual (heredado) |
| Código nativo dentro del paquete | **ninguno** (solo el ensamblado gestionado) | ninguno |

**Elegido: `FFmpeg.AutoGen` 8.1.0.** Motivos, por orden de peso:

1. **MIT.** Se comprobó leyendo el `LICENSE.txt` del propio paquete en la caché
   de NuGet, no la etiqueta de la web: *"MIT License — Copyright (c) 2025 Ruslan
   Balanukhin (Rationale One)"*. Sdcb.FFmpeg declara `LGPL-3.0-only` en su
   nuspec. Con MIT el wrapper deja de ser una obligación que arrastrar: la
   única carga LGPL que queda es la de las DLL nativas, que es inevitable y ya
   está gestionada.
2. **Empareja con una build LGPL que existe HOY.** La ABI del binding tiene que
   coincidir con la de las DLL (`avcodec-62`, `avutil-60`, `avformat-62` =
   FFmpeg 8.1). Con Sdcb habría que congelarse en FFmpeg 7.0 y buscarse la vida
   para conseguir una build LGPL compartida de esa rama.
3. **Mantenimiento vivo.** AutoGen sigue publicando al ritmo de FFmpeg
   (8.1.0 es de 2026); Sdcb se quedó en 7.0.
4. **Enlace dinámico explícito.** `ffmpeg.RootPath = <carpeta>` decide de dónde
   salen las DLL. No es comodidad: es el requisito de la LGPL (el usuario tiene
   que poder sustituirlas) y además evita el accidente de enlazar sin querer
   el `ffmpeg.exe` **GPL** que hay en el PATH de esta máquina.

`FFmpegLoader.Locate()` busca en este orden: variable `WINCUE_FFMPEG_DIR` →
`ffmpeg-lgpl\bin` junto al ejecutable → subiendo por los directorios padre.
**Deliberadamente NO cae al PATH del sistema.**

---

## 2. Build LGPL de FFmpeg y su verificación

**Origen:** `BtbN/FFmpeg-Builds`, variante `lgpl-shared` de la rama n8.1
(CI pública de GitHub Actions, receta reproducible). Lo baja
`obtener-ffmpeg-lgpl.ps1`, que además deja la procedencia en
`ffmpeg-lgpl/ffmpeg-BUILD.txt` (URL, fecha, SHA256 del zip y SHA256 de cada
DLL). **Las DLL están en `.gitignore`; el fichero de procedencia NO** — la LGPL
obliga a poder ofrecer el fuente EXACTO de la build distribuida.

**Verificación (`--licencia`).** Amplía el enfoque de
`tools\libmpv-lgpl\verificar-licencia` (que solo mira la línea de
configuración): aquí se cruzan **dos fuentes independientes**, y solo se da por
bueno si las dos coinciden.

1. `avcodec_configuration()` — no puede contener `--enable-gpl` ni
   `--enable-nonfree`.
2. `avutil_license()`, `avcodec_license()`, `avformat_license()` — cada
   librería declara la suya.

Además imprime los módulos nativos REALMENTE cargados en el proceso (puede
haber varias `avcodec-*.dll` en el sistema con licencias distintas) y comprueba
que el decodificador `prores` está presente. Sale 0 solo si todo cuadra.

Resultado en esta máquina:

```
RootPath fijado = ...\tools\prores-lab\ffmpeg-lgpl\bin
Módulos nativos REALMENTE cargados en el proceso:
  ...\ffmpeg-lgpl\bin\avcodec-62.dll
  ...\ffmpeg-lgpl\bin\avutil-60.dll
  ...\ffmpeg-lgpl\bin\swresample-6.dll

avutil 60.26.102 · avcodec 62.28.102 · avformat 62.12.102

configuración = --prefix=/ffbuild/prefix ... --enable-version3 --enable-shared
                --disable-static ... --disable-libx264 --disable-libx265
                --disable-libxvid --disable-libdvdread --disable-libdvdnav ...
                (SIN --enable-gpl, SIN --enable-nonfree)

  avutil    license = LGPL version 3 or later
  avcodec   license = LGPL version 3 or later
  avformat  license = LGPL version 3 or later

VEREDICTO: Lgpl — apto para enlace DINÁMICO desde software propietario ✓
decoder 'prores' = presente
```

`--enable-version3` da **LGPLv3**, compatible con enlace propietario — es
exactamente la misma nota que ya está en `tools/libmpv-lgpl/README.md`.

El detector está testeado como función pura (`LicenseCheckTests`, 8 tests),
incluidos los casos que la comprobación de libmpv no cubría: una librería que
declara GPL aunque el `configure` parezca limpio, `nonfree` ganando a `gpl`, y
la trampa obvia de que **"LGPL" contiene "GPL"**.

### Lo que se pierde con la build LGPL (nada que WinCue use)

Los `--disable-` de arriba son encoders GPL (x264/x265/xvid) y DVD. La
DECODIFICACIÓN de ProRes, H.264, HEVC, AV1, VP9 es nativa de FFmpeg y no es
GPL: intacta.

---

## 3. Demuxer: se reutiliza el NUESTRO (el de hap-lab)

La alternativa era usar `libavformat`. Se descartó, y las medidas del
laboratorio lo confirmaron:

| | **MovDemuxer propio** ✅ | libavformat |
|---|---|---|
| Coherencia con HAP | La MISMA tubería para los dos códecs | Dos caminos distintos en el mismo motor |
| `.mxf` y contenedores raros | Fuera (declarado) | Gratis |
| Superficie nativa expuesta a datos hostiles | Solo `avcodec` | `avcodec` **+ avformat**, que es la parte que MÁS contacto tiene con ficheros ajenos |
| Control del ring / prefetch / trim por índices | Total (ya lo tenemos) | Habría que envolver `av_read_frame` |
| Coste de adaptarlo a ProRes | **~60 líneas** | — |

El demuxer de HAP sirvió **casi tal cual**: las tablas `stsz`/`stsc`/`stco`/`stts`
son del contenedor, no del códec. Los tres únicos añadidos:

1. **Fourcc de ProRes**: `apco` (Proxy), `apcs` (LT), `apcn` (422), `apch`
   (422 HQ), `ap4h` (4444), `ap4x` (4444 XQ).
2. **Átomo `colr`** dentro del `stsd` (matriz y rango de color) y campo `depth`
   (24 = opaco, 32 = 4444 con alfa).
3. **`Validate()`**: dimensiones acotadas, tamaños de sample positivos y
   acotados, offsets dentro del fichero. Es la primera línea de defensa antes
   de darle nada al decodificador nativo (§ 7).

### Hallazgo 1 — el `colr` del contenedor NO es la fuente de verdad

ffmpeg **no escribe el átomo `colr`** al codificar ProRes salvo que se le pida
`-movflags +write_colr` (comprobado buscando el átomo en el fichero: no está).
El color viaja en la **cabecera del propio frame ProRes**, y el decodificador
lo rellena en `AVFrame.colorspace` / `AVFrame.color_range`.

Regla adoptada: **manda el bitstream**; el `colr` del contenedor queda de
respaldo para ficheros de otras herramientas (las de Apple sí lo escriben).
El parser de `colr` se validó generando media con `+write_colr` (matriz 9 =
BT.2020 NCL y matriz 1 = BT.709 leídas correctamente).

### Hallazgo 2 (el caro) — sin el fourcc se pierden 2 bits por muestra

El decodificador de FFmpeg deduce la **profundidad de bits del PERFIL**, y el
perfil sale de `avctx->codec_tag`. Sin pasárselo:

```
[prores @ ...] Unknown prores profile 0
decode: 1920x1080 yuva444p10le (10 bits)     ← ProRes 4444 tratado como 10 bits
```

Pasando el fourcc del `stsd` como `codec_tag`:

```
decode: 1920x1080 yuva444p12le (12 bits, perfil FFmpeg 4)   ← correcto
```

Es decir: **el contenedor SÍ aporta algo imprescindible al decodificador**, y
es exactamente una cosa. Está fijado en el constructor de `ProResDecoder` y
cubierto por un test de regresión
(`SinElFourccDelContenedor_ElDecodificadorSeQuedaA10BitsEnUn4444`).

### Formatos de píxel que salen (lista cerrada, `PixelLayout`)

| Perfil | fourcc | Formato nativo | Planos | Bytes/frame 1080p | Bytes/frame 4K |
|---|---|---|---:|---:|---:|
| Proxy / LT / 422 / 422 HQ | `apco` `apcs` `apcn` `apch` | `yuv422p10le` | 3 | 7,91 MB | 31,64 MB |
| 4444 / 4444 XQ | `ap4h` `ap4x` | `yuva444p12le` | 4 | 15,82 MB | 63,28 MB |

`PixelLayout.For()` **rechaza cualquier otro formato**: es también una puerta de
seguridad (mejor fallar en C# con un mensaje claro que interpretar punteros con
el layout equivocado).

**`.mxf` queda fuera del laboratorio**, como estaba previsto. Si algún día hace
falta, la salida natural es un demuxer propio de MXF OP1a (el subconjunto que
usan las cámaras) o, si se acepta la superficie extra, `libavformat` SOLO para
ese caso.

---

## 4. Benchmark de decodificación

Máquina: **AMD Ryzen 9 9900X** (12C/24T), 62 GB RAM, **RTX 5070 Ti** (driver
32.0.15.9579) + iGPU Radeon, Windows 11, .NET 8 Release, ServerGC.
Frames comprimidos **precargados en RAM nativa** (un solo bloque `av_malloc`
con el padding que exige FFmpeg): se mide DECODE, no disco. La E/S se reporta
aparte como MB/s.

### Sobre la media: por qué hay clips "de ruido" y por qué son los que valen

`testsrc2` es sintético y plano: ProRes lo comprime muy por debajo de su tasa
nominal y su decodificación entrópica toca pocos coeficientes. Los clips
`-ruido` (`noise=alls=90:allf=t+u`) son el peor caso realista, y la prueba de
que son REPRESENTATIVOS es su bitrate: **1080p 422 HQ ruido = 232 Mbit/s**
(nominal de Apple: 220) y **4K 422 HQ ruido = 923 Mbit/s** (nominal: ~880). Los
clips de `testsrc2` se quedan en 75 y 251 Mbit/s.

**Las filas de ruido son la referencia honesta; las de testsrc2 son el mejor
caso.**

### 1080p (300 frames por pasada, ×2 pasadas)

| Clip | Formato | 1 hilo fps | p50 ms | p95 ms | max ms | slice ×12 fps | frame ×12 fps | MB/s bits | pico RAM |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 1080p 422 (`apcn`) | yuv422p10le | 334,1 | 2,94 | 3,41 | 6,33 | **2 400** | 2 590 | 75 | 123 MB |
| 1080p 422 HQ (`apch`) | yuv422p10le | 299,4 | 3,11 | 3,50 | 91,6 | **2 255** | 2 463 | 90 | 145 MB |
| 1080p 4444 (`ap4h`) | yuva444p12le | 111,1 | 8,89 | 9,92 | 13,98 | **864** | 915 | 77 | 274 MB |
| **1080p 422 HQ ruido** | yuv422p10le | **71,9** | **13,70** | 15,02 | 21,90 | **728** | 730 | 673 | 334 MB |

### 4K (150 frames por pasada, ×2 pasadas)

| Clip | Formato | 1 hilo fps | p50 ms | p95 ms | max ms | slice ×12 fps | frame ×12 fps | MB/s bits | pico RAM |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 4K 422 (`apcn`) | yuv422p10le | 85,7 | 11,34 | 13,84 | 19,50 | **646** | 642 | 492 | 196 MB |
| 4K 422 HQ (`apch`) | yuv422p10le | 79,9 | 12,36 | 13,67 | 15,37 | **584** | 591 | 611 | 237 MB |
| 4K 4444 (`ap4h`) | yuva444p12le | 28,6 | 34,21 | 40,23 | 53,95 | **223** | 228 | 463 | 424 MB |
| **4K 422 HQ ruido** | yuv422p10le | **17,1** | **56,99** | 64,46 | 121,8 | **164** | 182 | 603 | 632 MB |

### Con 24 hilos (todos los lógicos de la máquina)

| Clip | slice ×24 | frame ×24 | slice+frame ×24 | pico RAM slice / frame |
|---|---:|---:|---:|---|
| 1080p 422 HQ | 2 495 | **3 624** | 2 599 | 145 MB / 342 MB |
| 1080p 422 HQ ruido | 1 007 | **1 244** | 1 245 | 335 MB / 545 MB |
| 4K 422 HQ ruido | 269 | **304** | 307 | 632 MB / **1 452 MB** |
| 4K 4444 | 328 | **347** | 351 | 424 MB / **1 944 MB** |

### Lectura de los números

- **ProRes escala muy bien por slices**: ×7,5 a ×9,6 con 12 hilos, ×34 con 24
  en el mejor caso. La escalabilidad es prácticamente lineal hasta los núcleos
  físicos y sigue ganando con SMT.
- **Slice vs frame.** `FF_THREAD_FRAME` da un 10-25 % más de throughput, pero
  a un precio que en directo NO compensa: (a) **retarda la salida**
  `thread_count − 1` paquetes (medido: con slice el primer paquete entrega
  frame; con frame, no), lo que estropea el arranque del cue y el seek, y
  (b) **multiplica la RAM por 3-5** (4K 4444: 424 MB con slice, **1 944 MB**
  con frame, porque mantiene `thread_count` frames vivos).
  **Recomendación firme para el paso 2: `FF_THREAD_SLICE`.**
- **El contenido manda**: el clip de ruido cuesta **4,6× más** que el de
  testsrc2 a 1080p y **3,3× más** a 4K, con el mismo perfil. Todo dimensionado
  debe hacerse con las filas de ruido.
- **Latencia plana con slice**: p95 ≈ 1,1-1,2 × p50 en todos los casos. Los
  `max` altos aislados (91,6 ms en una corrida a 1 hilo) aparecen una vez cada
  600 frames y son atribuibles al GC/OS, no al códec — mismo comportamiento que
  vimos con HAP.

### N vídeos SIMULTÁNEOS (decodificadores independientes)

Lo que de verdad se pregunta en directo. Un decodificador por vídeo, cada uno
con sus hilos:

| Escenario | 1 vídeo | 2 vídeos | 3 vídeos | 4 vídeos |
|---|---:|---:|---:|---:|
| 1080p 422 HQ ruido, 1 hilo/vídeo | 72,3 | 68,4 | 66,8 | 65,7 |
| 1080p 422 HQ ruido, 3 hilos/vídeo | 209,4 | 197,6 | 190,4 | 183,3 |
| 1080p 422 HQ limpio, 1 hilo/vídeo | 318,6 | 296,0 | 281,6 | 253,2 |
| 4K 422 HQ limpio, 4 hilos/vídeo | 271,7 | 222,5 | 189,9 | — |
| 4K 4444 limpio, 6 hilos/vídeo | 129,9 | 109,8 | 99,2 | — |

(fps POR VÍDEO; la degradación al añadir capas es de un 8-25 %, no lineal:
la máquina tiene núcleos de sobra y lo que se toca es la caché y el ancho de
banda de memoria.)

**Con un solo hilo por vídeo, un 1080p 422 HQ de bitrate real ya sale a 66 fps
con tres capas** — cabe a 60 fps sin ni siquiera repartir hilos.

### El presupuesto de hilos importa MÁS que el número de capas

Barrido con el peor caso (3 × 4K 422 HQ de bitrate nominal, `slice`):

| Hilos por vídeo | Total de hilos | fps por vídeo (×3) | ¿60 fps? |
|---:|---:|---:|---|
| 4 | 12 | **58,5** | ❌ por poco |
| 6 | 18 | 82,5 | ✅ ×1,4 |
| **8** | **24** (= lógicos) | **101,4** | ✅ **×1,7** |
| 12 | 36 (sobresuscrito) | 105,5 | ✅ ×1,8 (ya no gana) |

Conclusión operativa: **el reparto correcto es ≈ `lógicos / capas`**, y con 4
hilos por capa el caso más duro se queda justo por debajo. Sobresuscribir no
duele (el techo agregado se queda en ~316 fps), pero tampoco aporta. Esto es
una decisión de diseño para el paso 2, no un detalle.

---

## 5. Camino GPU de 10/12 bits (la parte nueva de verdad)

Hoy el pipeline del motor propio solo maneja **BC1/BC3 de 8 bits**, donde la
GPU descomprime al muestrear y no hay conversión de color que hacer (salvo el
YCoCg de HapY). ProRes obliga a algo distinto.

### Diseño elegido

- **Un plano = una textura `R16_UNORM`** (`Texture2D<float>` en el shader).
  Es lo que mejor encaja: las muestras de FFmpeg ya vienen como `uint16`
  little-endian, así que **la subida es una copia de filas sin tocar los
  datos** (nada de desempaquetar ni reescalar en CPU) y la GPU normaliza al
  muestrear.
- **Corrección de profundidad y rango con UNA multiplicación y una suma.** La
  GPU devuelve `código/65535` pero el código útil es de 10 o 12 bits alineado a
  la derecha. Con `escala = 65535/(alto − bajo)` y `offset = −bajo/(alto − bajo)`
  se resuelven a la vez la profundidad Y el rango limitado/completo. Las
  constantes se calculan en CPU con la MISMA aritmética que la referencia
  (`ColorConvert`), y hay un test que verifica que GPU y CPU no divergen.
- **Matriz correcta**: BT.709, BT.601 y **BT.2020 NCL** derivadas de (Kr, Kb),
  no copiadas de una tabla. La elige el bitstream (`AVFrame.colorspace`), con
  el `colr` del contenedor de respaldo y BT.709 por defecto.
- **Croma POINT por defecto** (replicación). Es lo que hace swscale de FFmpeg
  —y por tanto mpv— al convertir 4:2:2 a RGB. `--croma lineal` ofrece
  interpolación bilineal por hardware (imagen discutiblemente mejor, pero deja
  de ser comparable con ffmpeg: ver PSNR).
- **Render target `B8G8R8A8_UNorm` COMPARTIDO**, exactamente igual que en
  `hap-lab\HapLab.View`: el lado D3D9Ex → `D3DImage` del compositor real **no
  nota la diferencia entre HAP y ProRes**.
- Device sobre el adaptador `HighPerformance` (esta máquina tiene iGPU AMD +
  RTX), ring de 3 texturas por plano, `Map WRITE_DISCARD`.

**Alternativas descartadas:** `R16_UINT` (obliga a `Load` en vez de `Sample`,
adiós al filtrado por hardware del croma) y empaquetar a `R10G10B10A2` en CPU
(coste por píxel justo en el hilo que menos sobra).

**Lo que NO se ha repetido aquí:** la presentación D3D11 → shared handle →
D3D9Ex → `D3DImage` con `StretchRect` dentro del `Lock`. Ya quedó validada en
`hap-lab` (0 drops a 1080p60 y 4K60) y **no cambia con ProRes**: el render
target es idéntico. Por eso `ProResLab.Gpu` es headless — lo que cambia, y lo
que se mide, es la subida y la conversión.

### Verificación de imagen (PSNR)

Mismo método que el lab de HAP, refinado: el frame se compara contra el decode
+ conversión del **mismo frame** hecho por `ffmpeg.exe` (subproceso), pidiendo
`-f rawvideo -pix_fmt bgra` en vez de PNG — así no se mete de por medio el
codificador de PNG ni su redondeo. Se mide además contra la referencia de CPU
del laboratorio, que aísla "error de matemática" de "error de precisión de
textura y filtrado".

| Clip | Formato | PSNR GPU vs ffmpeg | máx \|Δ\| | PSNR GPU vs CPU del lab |
|---|---|---:|---:|---:|
| 1080p 422 (`apcn`) | yuv422p10le | **48,00 dB** | 3 | 75,06 dB |
| 1080p 422 HQ (`apch`) | yuv422p10le | **48,01 dB** | 3 | 74,45 dB |
| 1080p Proxy (`apco`) | yuv422p10le | 47,99 dB | 3 | 74,07 dB |
| 1080p 422 HQ **ruido** | yuv422p10le | 46,56 dB | 4 | 63,70 dB |
| 1080p **BT.2020** | yuv422p10le | **48,72 dB** | 3 | 76,22 dB |
| 4K 422 HQ | yuv422p10le | 48,02 dB | 3 | 76,16 dB |
| 1080p 4444 (`ap4h`) | yuva444p12le | **74,77 dB** | 1 | 75,32 dB |
| 1080p 4444 XQ (`ap4x`) | yuva444p12le | 74,77 dB | 1 | 75,32 dB |
| 1080p 422 HQ, `--croma lineal` | yuv422p10le | 33,93 dB | 118 | 34,06 dB |

Lectura:

- **48 dB con 4:2:2 y máx \|Δ\| = 3**: imagen indistinguible. Comparable al
  51,6 dB que dio el lab de HAP (allí el error era el redondeo del
  descompresor DXT por hardware; aquí es el redondeo del propio swscale).
- **Los 4:4:4 dan 74,8 dB con máx \|Δ\| = 1**: sin ampliación de croma no queda
  prácticamente ningún error. Eso confirma que **los 48 dB de 4:2:2 son enteros
  de la ampliación de croma**, no de nuestra matemática.
- **GPU vs CPU del lab: 74-76 dB, máx \|Δ\| = 1** en todos los casos → el shader
  ejecuta la fórmula correcta; la diferencia con la referencia es el último bit
  de redondeo de `float32`.
- **`--croma lineal` cae a 33,9 dB con máx \|Δ\| = 118**: confirmación
  experimental de que **swscale replica el croma horizontalmente y no
  interpola**. No es un fallo, es una decisión de upsampling distinta; el
  laboratorio se queda con POINT para ser idéntico a lo que el operador ve hoy
  con mpv.

### Benchmark de la cadena completa (decode + subida + shader)

Un stream, decodificador con `slice ×24`, con valla de GPU (`ID3D11Query`)
antes de parar el cronómetro:

| Clip | Subida/frame | decode p50 | subida p50 / p95 | shader p50 | **fps cadena** | MB/s a GPU |
|---|---:|---:|---:|---:|---:|---:|
| 1080p 422 HQ | 7,91 MB | 0,41 | 0,28 / 0,30 | **0,02** | **1 302** | 10 300 |
| 1080p 422 HQ ruido | 7,91 MB | 1,01 | 0,31 / 0,44 | 0,03 | **686** | 5 428 |
| 1080p 4444 | 15,82 MB | 0,98 | 0,64 / 0,83 | 0,04 | **579** | 9 152 |
| 4K 422 HQ | 31,64 MB | 1,44 | 1,46 / 1,62 | 0,06 | **328** | 10 365 |
| 4K 422 HQ ruido | 31,64 MB | 3,63 | 1,52 / 2,38 | 0,05 | **180** | 5 685 |
| 4K 4444 | 63,28 MB | 3,19 | 3,47 / 5,36 | **0,08** | **135** | 8 555 |

**El shader es gratis** (0,02-0,08 ms; el presupuesto a 60 fps es 16,7 ms).
**La subida no lo es**, y es el cambio de régimen respecto a HAP:

| | HAP 4K (BC3) | **ProRes 4K 422** | **ProRes 4K 4444** |
|---|---:|---:|---:|
| Bytes por frame a la GPU | 8,29 MB | **31,64 MB** (3,8×) | **63,28 MB** (7,6×) |
| Subida p50 medida | 0,32 ms | **1,46 ms** | **3,47 ms** |
| A 60 fps, por capa | 0,5 GB/s | **1,9 GB/s** | **3,8 GB/s** |

Se llegó a ~10,3 GB/s efectivos de subida sostenida sobre PCIe, así que **una
capa de 4K 4444 a 60 fps consume ya un 37 % de ese ancho de banda**.

---

## 6. ¿Aguanta el directo?

### Medida directa: 3 procesos simultáneos con la cadena completa

Cada proceso con su propio device D3D y su decodificador a 24 hilos (es decir,
**72 hilos sobre 24 lógicos: la configuración PESIMISTA**, sin reparto de
presupuesto).

| Escenario ×3 | fps por vídeo | ¿60 fps? | Margen |
|---|---|---|---|
| 1080p 422 HQ **ruido** | 388 / 372 / 417 | ✅ | **×6,2** |
| 4K 422 HQ limpio | 137 / 132 / 147 | ✅ | ×2,2 |
| **4K 422 HQ ruido** | 78 / 80 / 86 | ✅ | **×1,3** |
| **4K 4444** (12 bits) | 56 / 57 / 59 | ⚠️ **no** | ×0,95 |

(referencia: un solo stream 4K 422 HQ limpio da 332 fps)

### Veredicto

| Objetivo | Veredicto |
|---|---|
| **2-3 × 1080p60 (cualquier perfil 4:2:2)** | ✅ **Sí, con margen enorme** (×6 en el peor caso realista). Ni siquiera hace falta repartir hilos: 1 hilo por vídeo ya da 66 fps con 3 capas. |
| **2-3 × 1080p60 4444** | ✅ Sí (864 fps de decode con 12 hilos; 579 fps de cadena completa a 1 stream). |
| **2 × 4K60 4:2:2** | ✅ Sí, con margen cómodo. |
| **3 × 4K60 4:2:2** | ✅ Sí, con ~30 % de margen en el peor caso (bitrate nominal de Apple). Requiere **NVMe**: 3 × 220 MB/s ≈ 660 MB/s sostenidos. |
| **2 × 4K60 4444** | ⚠️ Al filo. Sale, pero sin colchón para picos. |
| **3 × 4K60 4444** | ❌ **No**, y la causa NO es el decode. |
| 4K30 en cualquier perfil, 3 capas | ✅ Sí en todos los casos. |

### Por qué falla 3 × 4K60 4444: la culpa es de la SUBIDA, no del códec

Merece señalarse porque cambia dónde hay que optimizar. El decode de 4K 4444
aguanta de sobra tres capas (**99 fps por capa** con 6 hilos cada una). Lo que
no cabe es el bus:

- Un frame 4K `yuva444p12le` son **63,28 MB** (cuatro planos completos de 16
  bits). A 60 fps: **3,8 GB/s por capa**.
- Tres capas = **11,4 GB/s**, por encima de los ~10,3 GB/s de subida sostenida
  medidos en esta máquina.
- Coincide con lo observado: la cadena completa cae a **56-59 fps por capa**
  aunque el decode diera 99.

Si algún día hace falta 3 × 4K60 4444, la palanca es la subida, no el
decodificador: empaquetar a 10 bits en la textura (`R10G10B10A2`, −37 % de
bytes) a costa de CPU, subir el alfa solo cuando el cue lo use (−25 %), o
directamente convertir a RGBA8 en un compute shader y cachear. Ninguna de las
tres hace falta para el objetivo declarado (2-3 × 1080p60).

**Advertencias honestas:**

- Los 3 procesos concurrentes tenían **cada uno** 24 hilos de decode
  (sobresuscripción 3:1). Según el barrido de arriba, con 8 hilos por capa el
  resultado es igual o mejor.
- Cada proceso tenía **su propio device D3D11**. En WinCue el pool comparte
  device por (salida, pista, spec), así que la contención de subida podría ser
  algo distinta.
- No se ha medido la presentación completa (D3D9Ex + `D3DImage` + DWM). En
  `hap-lab` esa parte costaba ~0,15 ms en 4K y no depende del códec.

### Contraste honesto con mpv (lo que cuesta HOY el mismo clip)

mpv usa **este mismo decodificador** de FFmpeg, así que la comparación no mide
"quién decodifica mejor" sino cuánto cuesta nuestra tubería frente a la suya.
mpv medido con `--vo=null --untimed --no-audio --loop-file` (incluye su demux;
la nuestra excluye la E/S de disco: **la comparación favorece ligeramente a la
nuestra y hay que leerla con esa reserva**).

| Clip | **prores-lab** (mejor modo, 24 h) | mpv | Diferencia |
|---|---:|---:|---:|
| 1080p 422 HQ | **3 624** | 2 097 | +73 % |
| 1080p 422 HQ ruido | **1 244** | 892 | +39 % |
| 4K 422 HQ | 606 (12 h) / — | 595 | ≈ igual |
| 4K 422 HQ ruido | **304** | 226 | +34 % |
| 4K 4444 | **351** | 247 | +42 % |

**No ganamos por decodificar mejor** (es el mismo código). Se gana por dos
cosas concretas, ya demostradas en el paso 3 de HAP:

1. **Se acaba la doble decodificación.** Hoy un cue ProRes 4K60 se decodifica
   dos veces (el player de la salida y el mini-monitor de PGM) y se lee dos
   veces del disco. Con motor propio + PGM espejo, una sola vez — medido en el
   lab de HAP: **17,1 s de CPU → 7,8 s** con un solo cue 4K60.
2. **Se recuperan los fundidos de opacidad por GPU y el compositor real** (que
   la ruta mpv clásica pierde), sin pagar conversión de color en CPU: swscale
   convierte a RGB en CPU dentro de mpv; aquí lo hace el shader en 0,02-0,08 ms.

---

## 7. Riesgo de estabilidad: un decodificador nativo EN PROCESO

Es la parte delicada del cambio y hay que decirlo claro: **hoy ffmpeg va por
subproceso y no puede tirar la aplicación; en proceso, sí puede.** Un fallo de
memoria dentro del código nativo no es capturable desde C#.

### Lo que sí se ha hecho (y medido)

**1. Validación de entrada antes de tocar nada nativo** (`MovDemuxer.Validate`):
dimensiones en `[1, 16384]`, tamaño de sample en `(0, 256 MB]`, y offsets que
caben dentro del fichero. Tres tests de regresión.

**2. Padding obligatorio**. `avcodec_send_packet` exige
`AV_INPUT_BUFFER_PADDING_SIZE` bytes a CERO detrás de los datos (los lectores
de bits de FFmpeg leen de más por diseño). Con memoria gestionada eso es un
accidente esperando a ocurrir, así que el laboratorio usa memoria NATIVA
(`av_malloc`, que además alinea para SIMD) y **nunca** entrega un puntero sin
su padding: la única forma de enviar un paquete es `PaddedBuffer.AsPacket()` o
`NativeFrameStore[i]`.

**3. Lista cerrada de formatos de píxel.** `PixelLayout.For()` lanza con
cualquier formato que no sea uno de los seis de ProRes: mejor una excepción
clara que interpretar punteros con el layout equivocado.

**4. Log nativo bajo control.** Por defecto FFmpeg escupe a `stderr`; el loader
fija `av_log_set_level(AV_LOG_ERROR)` al cargar. En producción esto tiene que
ir al log del show, no a la consola.

**5. Fuzzing (`--fuzz`).** Se corrompen de 1 a 64 bytes al azar de un frame real
(saltándose la cabecera, que solo probaría el rechazo temprano), con un
contexto NUEVO por iteración, y se **tocan los planos resultantes** para
provocar cualquier acceso ilegal:

```
fuzz sobre bench-1080-422hq.mov: 400 iteraciones
  frames tolerados (imagen dañada pero válida): 330
  errores devueltos y capturados en C#        : 70
  el proceso sigue vivo: sin caída del decodificador nativo.

fuzz sobre bench-1080-4444.mov: 200 iteraciones
  frames tolerados: 181 · errores capturados: 19 · proceso vivo.
```

**600 iteraciones, 0 caídas.** El decodificador se queja por su log
(`ac tex damaged`, `invalid plane data size`, `error decoding picture header`) y
devuelve `AVERROR_INVALIDDATA`, que viaja como excepción de C#. Es la evidencia
de que el decodificador de ProRes de FFmpeg —que lleva más de una década
expuesto a ficheros del mundo real y a los fuzzers de OSS-Fuzz— aguanta.

### Lo que NO cubre esto, y qué hacer en el paso 2

- Un fallo de verdad (corrupción de heap, desbordamiento no detectado) **se
  lleva la aplicación**. No hay forma de capturarlo dentro del proceso.
- **Interruptor en Ajustes para desactivar el motor ProRes** (lo pidió Andrés)
  con fallback a mpv. Debe existir también como variable de entorno para
  poderlo apagar EN CALIENTE en un directo, igual que `WINCUE_HAP_ENGINE=0`.
- **Fallback por cue, no global**: si un archivo falla al demuxar/abrir el
  decodificador, ese cue cae a mpv y el resto del show sigue con motor propio
  (patrón ya implementado para HAP).
- **Un contexto por cue, nunca compartido.** `AVCodecContext` no es reentrante;
  además un contexto que ha visto basura queda en estado indefinido y hay que
  recrearlo (o al menos `Reset()`).
- Opción nuclear si algún día hace falta: mover el decode a un **proceso
  aparte** con memoria compartida. Encarece la latencia y complica el ring;
  solo si aparece inestabilidad real en campo.

### Trampa de la API que ya nos mordió

Tras `avcodec_send_packet(ctx, NULL)` (drenaje) el contexto queda en modo EOF
**para siempre** y el siguiente paquete devuelve `AVERROR_EOF`. Hay que llamar a
`avcodec_flush_buffers` (`ProResDecoder.Reset()`). **En producción esto pasa en
cada pasada de LOOP y en cada seek.** Cubierto por
`TrasElDrenaje_HayQueResetearParaVolverAUsarlo`.

---

## 8. Tests

**85 tests, todos en verde, 0 warnings** con los analizadores del repo
(`AnalysisLevel latest-all`, heredado del `Directory.Build.props`). **No hizo
falta bajar el nivel de análisis ni suprimir nada** — igual que en `hap-lab`.

| Fichero | Qué cubre |
|---|---|
| `LicenseCheckTests` (8) | El veredicto de licencia como función pura: `--enable-gpl`, `--enable-nonfree`, nonfree ganando a gpl, una librería que declara GPL con `configure` limpio, indeterminado = no apto, y la trampa de que "LGPL" contiene "GPL". Más un test de integración contra las DLL realmente cargadas. |
| `MovDemuxerTests` (20) | Los cinco perfiles por fourcc, dimensiones/fps/frames, `depth` 24 vs 32, pista de audio enumerada (`sowt`), `colr` leído cuando existe y NO inventado cuando no, tabla de samples dentro del fichero y en orden, un `.mp4` de H.264 que se demuxa pero no es ProRes, un fichero basura rechazado, y las tres ramas de `Validate()`. |
| `ProResDecodeTests` (24) | Formato de píxel esperado por variante (422 → `yuv422p10le`; 4444/XQ → `yuva444p12le` con alfa), tamaños de plano, **la regresión del `codec_tag`** (sin él, 4444 sale a 10 bits), perfil que llega al decodificador, matriz del bitstream mandando sobre el contenedor, BT.2020, los cuatro modos de hilos entregando los mismos frames, frame-threading reteniendo hasta el drenaje, **el mismo frame idéntico bit a bit con 1 hilo y con 12**, ProRes como todo-intra (frame 20 suelto == decodificado en secuencia), `Reset()` tras drenaje, y el rechazo de entradas imposibles. |
| `ColorConvertTests` (18) | La matemática de color como función pura contra **valores de referencia de las recomendaciones**: negro y blanco limitados exactos, coeficientes de BT.709/601/2020 a 6 decimales, rojo primario, rango completo, 12 bits, traducción de códigos ISO 23001-8, cuantización. Y que **las constantes del shader reproducen la normalización de CPU** en las cuatro combinaciones de profundidad y rango. |
| `CpuReferenceTests` (15) | La referencia de CPU (negro/blanco, replicación de croma en 4:2:2, sin replicación en 4:4:4, alfa del cuarto plano), el PSNR (idénticas = ∞, error de 1 nivel = 48,13 dB, el alfa fuera), y el layout (bytes por frame, dimensiones impares, `MaxSample`). |

Los tests de GPU con device D3D **no se incluyen** (headless sin GPU no es
fiable): la corrección del camino GPU se verifica con `--psnr`, que sí necesita
GPU y está en la tabla de la § 5.

---

## 9. Qué haría falta para el paso 2 (integración en `src/`)

Casi todo el andamiaje ya existe: el paso 3 de HAP dejó el ring, el reloj, el
pacer, el pool de superficies y el enrutado del controlador. Lo que falta es
específico de ProRes.

**Núcleo (promocionar del lab):**

1. `WinCue.Video/ProRes/` con `ProResDecoder`, `PixelLayout`, `ColorConvert`,
   `ColorParams` y la ampliación del `MovDemuxer` **compartido** con HAP
   (hoy hay dos copias: la de `src/WinCue.Video/Hap/` y la del lab; la
   integración debe unificarlas — el demuxer no es del códec).
2. `FFmpegLoader` en producción con la carpeta de DLL junto al ejecutable, y
   `publish.ps1` copiando `ffmpeg-lgpl\bin\*.dll` (+ el `ffmpeg-BUILD.txt` y
   los avisos de licencia en `THIRD-PARTY-NOTICES.md` y `licenses/`).
3. `ProResVideoSession` análoga a `HapVideoSession`: ring de 3 slots ×
   N texturas `R16_UNORM`, con el pipeline en el pool persistente por
   (salida, pista, spec). **Ojo:** la spec de una textura de plano depende del
   formato de píxel (3 planos a media anchura vs 4 a anchura completa), así que
   el pool necesita una clave más que con HAP.
4. Shader YUV→RGB con su constant buffer, junto a los de HAP.

**Decisiones que este laboratorio deja tomadas:**

- `FF_THREAD_SLICE`, **nunca** `FF_THREAD_FRAME` (§ 4).
- Presupuesto de hilos por capa, no `ProcessorCount` por capa: reparto
  ≈ `lógicos / capas_activas`, con suelo de 2 y techo de 8 (medido en § 6).
- `codec_tag` desde el fourcc del `stsd`: obligatorio (§ 3).
- Color desde `AVFrame`, con `colr` de respaldo (§ 3).
- Croma POINT, para ser idéntico a lo que se ve hoy con mpv.

**Legal / empaquetado:**

5. Documentar en `docs/legal/checklist-distribucion.md` la nueva dependencia:
   FFmpeg LGPLv3 **enlace dinámico**, con la oferta de fuente de la build
   exacta (el `ffmpeg-BUILD.txt` ya lleva URL y SHA256). Nada de firmar o
   sellar la carpeta de DLL: el usuario tiene que poder sustituirlas.
6. Añadir `--licencia` (o su equivalente) al **smoke de publicación**: que un
   error humano no meta jamás una DLL GPL en un instalador.

**Seguridad de directo:**

7. Interruptor en Ajustes → Vídeo («motor propio para ProRes») **y** variable de
   entorno para apagarlo en caliente, con fallback a mpv por cue (§ 7).
8. Elegibilidad estricta, como con HAP: solo `.mov`, `rate == 1.0`, compositor
   GPU activo, geometría no CUSTOM. Todo lo demás por mpv.

**Lo que queda por medir antes de dar por bueno el paso 2:**

9. **Tres capas sobre UN solo device D3D11** (aquí fueron tres procesos con
   tres devices) y con presupuesto de hilos repartido.
10. **E/S de disco real sostenida**: 3 × 4K60 422 HQ son ~660 MB/s. Con HAP el
    prefetch de disco ya era el protagonista; con ProRes de bitrate nominal
    vuelve a serlo. Hace falta un prefetch de verdad, no `FileStream` síncrono.
11. **Soak** con el patrón de `scripts/soak-hap.ps1`: cadena encadenada con
    crossfades, loops, cambios de resolución y perfiles mezclados, con el
    `HapWatchdog` vigilando fugas — un decodificador nativo que no libere sus
    frames es una fuga que crece rápido (un frame 4K 4444 son 63 MB).
12. **Máquina solo-iGPU**: la subida de 32-63 MB/frame sobre memoria compartida
    se comporta distinto que sobre una dGPU con PCIe dedicado.

---

## 10. Decisiones del laboratorio

- **Solución aparte** (`prores-lab.sln`): no toca ni entra en `WinCueSharp.sln`.
- **`ProResLab.Gpu` headless** en vez de un `.View` con WPF: la presentación ya
  está validada en `hap-lab` y no cambia; medir sin ventana es más repetible.
  Efecto colateral bueno: sin WPF no reaparece el problema de
  `InvariantGlobalization` que documentó el lab de HAP.
- **Vortice.Windows 3.6.2 (MIT)**, misma decisión de lab que en `hap-lab`: la
  superficie de API usada es pequeña y portable a P/Invoke crudo.
- **Shaders compilados en runtime** (D3DCompiler): sin paso de build.
- **Un solo bloque nativo para los frames precargados** (`NativeFrameStore`) en
  vez de un `av_malloc` por frame: una allocación, mejor localidad y cero ruido
  de memoria dentro de la medición.
- **La media no se versiona** (3,4 GB) ni las DLL; sí la procedencia de la build
  y los scripts que lo reconstruyen todo.

---

# PASO 2 — Integración en `src/` (hecho)

El laboratorio queda intacto como banco de pruebas. Lo que sigue es lo que
**de verdad se llevó a producción**, en qué se desvió del plan del § 9 y qué
queda abierto. La arquitectura, los límites medidos y el riesgo del
decodificador en proceso están documentados en
`docs/arquitectura-motor-video.md` (sección «El motor ProRes propio»); aquí solo
las diferencias respecto a lo previsto.

## Qué se integró

| Pieza | Dónde vive ahora |
|---|---|
| Demuxer MOV | `src/WinCue.Video/Containers/MovDemuxer.cs` — **unificado**, ya no hay dos copias |
| Decodificador, layouts, color, loader, licencia | `src/WinCue.Video/ProRes/` (namespace `WinCue.Video.ProRes`) |
| Sesión + pipeline GPU | `ProResGpuPipeline.cs` (`ProResVideoSession`, `IProResRenderTarget`) |
| Ring de decode y player | `ProResFrameSource.cs`, `ProResCuePlayer.cs`, `ProResFrameSlot.cs` |
| Enrutado | `VideoEngine.StartProResPlayback` + pool `_proresPool` |
| Elegibilidad e interruptores | `MainController.ProResEngineEligible` |
| Guardián de licencia | `tools/verificar-ffmpeg`, llamado por `publish.ps1` |
| Obtención de las DLL | `tools/ffmpeg-lgpl/obtener-ffmpeg-lgpl.ps1` |
| Media y show de tortura | `tests/prores-test/`, `tests/stress/estres-prores.json` |
| Soak | `scripts/soak-prores.ps1` |

## Desvíos respecto al plan del § 9

1. **El demuxer no se «amplió»: se MOVIÓ.** El § 9 pedía unificar las dos copias.
   Se hizo promocionando la versión del laboratorio —superconjunto estricto— a
   `WinCue.Video.Containers` y retirando la de `Hap/`. El motor HAP gana de
   rebote el `colr`, el `depth` y `Validate()`. Los 105 tests de vídeo previos
   siguieron en verde sin tocar ninguna aserción.

2. **La clave del pool NO se amplió; se amplió la de la SESIÓN.** El § 9 decía
   «el pool necesita una clave más que con HAP». Meter el formato de píxel en la
   clave del POOL habría creado un device D3D11 completo por cada combinación de
   tamaño y perfil en la misma pista — diez resoluciones distintas, diez
   devices. Lo que de verdad depende del formato es el ring de texturas, no el
   pipeline. Solución: el pool sigue con `(salida, pista, "WxH" de superficie)`,
   y **cada slot cachea su sesión con clave `{W}x{H}|{layout}`**, reconstruyéndola
   solo cuando no coincide. Cumple el requisito y además evita crear y destruir
   hasta 190 MB de texturas en cada GO cuando los clips comparten formato, que es
   el caso normal de un show.

3. **Los frames no se copian.** El § 9 no lo mencionaba. Con HAP el ring guarda
   arrays gestionados y el decode escribe directo en el slot; con ProRes el frame
   sale en memoria de FFmpeg y el decodificador solo retiene UNO. Copiarlo eran
   63 MB de memcpy por fotograma en 4K 4444. Se resolvió con
   `av_frame_move_ref`: cada slot posee su `AVFrame` y el decodificador le
   traspasa la referencia. Coste cero. Contrapartida asumida y documentada: un
   slot sin liberar es una fuga de 63 MB, así que `ProResFrameSource.Dispose`
   solo suelta los slots **si el hilo decodificador murió de verdad** (liberar
   con el hilo vivo sería un use-after-free dentro del código nativo).

4. **El ring de decode es de 3, no de 4.** Con HAP cuatro slots son 32 MB en 4K;
   aquí serían 253 MB.

5. **`FramePacer` y `HapMediaClock` se reutilizan TAL CUAL**, sin moverlos ni
   renombrarlos. Son puros y no dependen del códec; conservan el prefijo `Hap`
   por su origen. Moverlos solo para renombrarlos habría metido churn en el motor
   HAP, que es justo lo que este paso no debía tocar.

6. **Solo se distribuyen tres DLL.** El § 9 hablaba de copiar `ffmpeg-lgpl\bin\*.dll`.
   El zip trae siete; `avformat`, `avfilter`, `avdevice` y `swscale` suman 68 MB y
   **no se usan** (el demuxer es nuestro, no hay filtros y la conversión de color
   va en el shader). Se distribuyen `avcodec-62`, `avutil-60` y `swresample-6`.
   Efecto colateral: `LicenseCheck.Inspect()` tuvo que volverse tolerante a una
   librería ausente — y FFmpeg.AutoGen no lanza `DllNotFoundException` sino
   `NotSupportedException` cuando la DLL no está.

7. **El fallback NO fuerza la ruta clásica** (a diferencia de HAP). mpv sí sabe
   renderizar ProRes, así que un cue que cae a mpv conserva sus fundidos de
   opacidad; solo se le quita el audio (`Volume = 0`, `ExternalAudio = true`)
   porque el espejo ya está sonando.

8. **Apareció un fallo en el propio guardián de licencias**, y merece contarse
   porque es del tipo que deja una comprobación pasando en verde sin comprobar
   nada: `dotnet run` no conoce `--nologo` y **reenvía a la aplicación todo lo
   que no entiende**, así que el verificador recibía `"--nologo"` como carpeta y,
   gracias al respaldo de `FFmpegLoader`, acababa inspeccionando el árbol de
   desarrollo en vez del paquete. Dos arreglos: la carpeta se pasa por
   `WINCUE_FFMPEG_DIR`, y esa variable pasa a ser **estricta** (si la carpeta no
   tiene avcodec, se falla en vez de buscar en otro sitio).

## Lo que el § 9 dejaba «por medir» y ahora está medido

- **Tres capas sobre un solo device D3D11 y con presupuesto de hilos repartido**:
  el show de tortura mantiene hasta tres cues de vídeo vivos (loop de fondo +
  entrante + saliente congelado) sobre el pool real. Sin drops reportados por el
  pacer ni anomalías del watchdog.
- **Soak con el patrón de `soak-hap.ps1`**: hecho, con media mezclada. Ver abajo.
- **E/S de disco real sostenida** y **máquina solo-iGPU**: siguen sin medir. Son
  los dos huecos honestos del paso 2.

## Tests traídos del laboratorio

De los 85 del lab se trajeron los que siguen valiendo en producción, adaptados a
los tipos nuevos: la regresión del `codec_tag` (los 12 bits), el `Reset()` tras
drenar, ProRes como todo-intra, el mismo frame con 1 hilo y con 8, los formatos
de píxel por variante, el `colr` leído y no inventado, `Validate()`, la
matemática de color contra los valores de las recomendaciones, la equivalencia
entre las constantes del shader y la referencia de CPU, y los siete casos del
veredicto de licencia (incluida la trampa de que «LGPL» contiene «GPL»).

Se quedaron fuera los que solo tenían sentido en el banco: los benchmarks, el
fuzzing (600 iteraciones no caben en un `dotnet test`) y las comparaciones PSNR
contra `ffmpeg.exe`.

Añadidos que el laboratorio no podía tener: el enrutado completo con sus dos
apagados, el fallback a mpv con mpv real, el presupuesto de hilos, y seis tests
de reproducción GPU de verdad (422 y 4444 hasta el fin natural, loop cruzando la
costura, trim + playCount, reloj inyectado mandando sobre el Stopwatch, y ocho
cues seguidos sobre la misma sesión).
