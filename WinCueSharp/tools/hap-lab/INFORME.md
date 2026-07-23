# hap-lab — PASO 1 del motor de vídeo propio: demux + decode HAP sin FFmpeg

Laboratorio headless que demuestra que WinCue puede leer un `.mov` HAP y llegar
a los datos DXT listos para subir a GPU **sin FFmpeg**: parser propio del
contenedor QuickTime + parser de frames HAP según la [especificación pública de
Vidvox](https://github.com/Vidvox/hap) + descompresión Snappy
([Snappier](https://www.nuget.org/packages/Snappier), MIT).

## Cómo usarlo

```
# Generar la media de benchmark (no versionada; requiere ffmpeg en PATH)
powershell -File generar-media.ps1

# Info de un fichero
dotnet run -c Release -- media/bench-1080-hap1.mov

# Benchmark completo (decodifica todos los frames)
dotnet run -c Release -- media/bench-1080-hap1.mov --bench

# Tests (generan su propia media pequeña 320x240 si falta)
dotnet test hap-lab.sln
```

## Resultados del benchmark

Máquina: AMD Ryzen 9 9900X (12C/24T), 64 GB RAM, Windows 11. .NET 8, Release,
ServerGC. Clips de 300 frames (10 s @ 30 fps) generados con ffmpeg a partir de
`testsrc2`. Los tiempos miden **solo decodificación** (frames comprimidos
precargados en RAM, sin E/S de disco ni subida a GPU). ST = bucle secuencial a
un hilo; MT = `Parallel.For` a nivel de frame con 24 hilos (simula N vídeos a
la vez).

| Clip | Formato | DXT/frame | ST fps | p50 ms | p95 ms | max ms | MT fps |
|---|---|---:|---:|---:|---:|---:|---:|
| 1080p Hap1 | RGB DXT1, Snappy | 0,99 MB | 8 107 | 0,12 | 0,15 | 0,22 | 9 913 |
| 1080p Hap5 | RGBA DXT5, Snappy | 1,98 MB | 4 480 | 0,22 | 0,23 | 0,26 | 13 564 |
| 1080p HapY | YCoCg DXT5, Snappy | 1,98 MB | 4 327 | 0,23 | 0,24 | 0,25 | 12 992 |
| 1080p Hap1 chunked (4) | Complex | 0,99 MB | 4 734 | 0,20 | 0,25 | 2,07 | 16 161 |
| 4K Hap1 | RGB DXT1, Snappy | 3,96 MB | 2 093 | 0,48 | 0,49 | 0,63 | 3 367 |
| 4K Hap5 | RGBA DXT5, Snappy | 7,91 MB | 1 197 | 0,83 | 0,86 | 2,58 | 1 756 |
| 4K HapY | YCoCg DXT5, Snappy | 7,91 MB | 1 129 | 0,87 | 0,98 | 2,50 | 1 721 |
| 4K Hap1 chunked (4) | Complex | 3,96 MB | 2 096 | 0,47 | 0,50 | 2,10 | 7 427 |

- Con chunks (modo Complex de 4 chunks) la descompresión intra-frame en
  paralelo sube de ~4 700 a ~7 244 fps en 1080p y de ~2 096 a ~4 588 fps en 4K
  (frames uno a uno, `Parallel.For` sobre los chunks del frame).
- El throughput ronda los 8-9 GB/s de DXT descomprimido a un solo hilo; en MT
  se llega a 13-29 GB/s, señal de que el límite pasa a ser el ancho de banda de
  memoria, no la CPU.
- Pico de working set en el peor caso (4K HapY): 624 MB, de los cuales 202 MB
  son la precarga de los frames comprimidos del propio benchmark.

### Matiz importante sobre los números

`testsrc2` es contenido sintético muy amable con Snappy (Hap1 1080p comprime a
23 Mbit/s; material real de VJ ronda 10-30× más). Con vídeo real la
descompresión Snappy tocará más datos y bajará quizá a 1-2 GB/s por núcleo.
Aun siendo pesimistas y dividiendo por 10, un frame 1080p Hap1 costaría ~1,2 ms.

## Viabilidad para directo

**Sí, con margen enorme.** El objetivo era 2-3 vídeos 1080p60 simultáneos:

- 3 × 1080p60 = 180 frames/s. Medido: 0,12 ms/frame (sintético) → 2 % de UN
  núcleo. Con el margen pesimista de 10× para contenido real: ~22 % de un
  núcleo. La máquina tiene 24.
- Incluso 3 × 4K60 HapY (el caso más caro: 240 fr/s × ~0,9 ms) cabría en un
  solo núcleo con contenido sintético, y en 2-3 núcleos con real.
- La latencia es plana: p95 ≈ p50 en todos los casos (el max de ~2 ms aparece
  1 vez en 300 frames, atribuible al GC/OS, no al códec).

El cuello de botella real del motor no será el decode CPU sino: (1) la E/S de
disco (HAP real a 4K puede pasar de 800 Mbit/s → SSD obligatorio y lectura con
prefetch), y (2) la subida del DXT a la GPU (siguiente paso del laboratorio:
`ID3D11Texture2D` con formato BC1/BC3 y ring de staging buffers).

## Notas de lo aprendido (spec y contenedor)

- **Cabecera de sección HAP**: 3 bytes de tamaño little-endian + 1 byte de
  tipo. Si los TRES primeros bytes son cero, la cabecera es de 8 bytes y el
  tamaño real va en un uint32 LE después del byte de tipo. Sorpresa: ffmpeg usa
  **siempre** la cabecera extendida en la sección top-level de los frames
  chunked aunque el tamaño quepa de sobra en 24 bits (p. ej. tamaño 5 266 con
  cabecera de 8 bytes).
- **Modo Complex (0xC_)**: dentro va un único Decode Instructions Container
  (0x01) con la Chunk Second-Stage Compressor Table (0x02, un byte por chunk:
  0x0A crudo / 0x0B Snappy) y la Chunk Size Table (0x03, uint32 LE por chunk),
  y después los datos. ffmpeg **no escribe** la Chunk Offset Table (0x04), que
  es opcional: los offsets se derivan acumulando los tamaños. El parser soporta
  ambos casos, pero la ruta ejercitada con media de ffmpeg es la derivada.
- ffmpeg sin `-chunks` emite frames 0xBB/0xBE/0xBF (un solo bloque Snappy);
  con `-chunks N` emite 0xCB/0xCE/0xCF. El nibble bajo (0xB/0xE/0xF) coincide
  con el fourcc del stsd (Hap1/Hap5/HapY).
- **MOV**: ffmpeg escribe el `moov` AL FINAL del fichero (tras el `mdat`) salvo
  `+faststart`, así que el demuxer escanea los átomos top-level en vez de
  asumir orden. Entre `ftyp` y `mdat` mete un átomo `wide` de 8 bytes (relleno
  histórico de QuickTime para poder "ensanchar" el mdat a 64 bits in situ).
- Con vídeo HAP cada `stco` chunk contiene varios samples (stsc con varias
  entradas), así que no vale offset-de-chunk = offset-de-frame: hay que acumular
  los tamaños de stsz dentro de cada chunk. El fps sale de
  `timescale / delta` del stts (ffmpeg usa timescale 15360 y delta 512 para 30 fps).
- El audio PCM 16-bit LE aparece como fourcc `sowt` ('twos' al revés: big
  endian sería 'twos'). Se enumera y se ignora, como pide el paso 1.
- `stco` vs `co64`: soportados ambos; con los clips generados (≤202 MB) ffmpeg
  siempre usó `stco` de 32 bits, así que `co64` queda cubierto solo por código,
  no por test (haría falta un fichero >4 GB).

## Decisiones

- **Analizadores**: el lab compila con el `Directory.Build.props` del repo
  (`AnalysisLevel latest-all`) a **cero warnings**; no hizo falta el escape
  `AnalysisLevel none`.
- **Snappier 1.3.1**: las versiones ≤1.3.0 tienen la advisory
  GHSA-pggp-6c3x-2xmx (bucle infinito en `SnappyStream` con entrada malformada;
  NU1903 en el build). No afecta a la API de bloque que usamos, pero se fija la
  versión parcheada igualmente.
- **Solución aparte** (`hap-lab.sln`): el lab no toca ni entra en
  `WinCueSharp.sln`.
- La API de decode escribe sobre un buffer que aporta el llamador
  (`HapFrame.Decode(frame, output, parallel)`): cero allocaciones por frame en
  régimen, pensando ya en el ring de buffers del player real.
