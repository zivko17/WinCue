# hap-lab — laboratorio del motor de vídeo propio (sin FFmpeg)

- **PASO 1** (`HapLab`, headless): demux `.mov` + decode HAP a DXT en CPU.
  Parser propio del contenedor QuickTime + parser de frames HAP según la
  [especificación pública de Vidvox](https://github.com/Vidvox/hap) +
  descompresión Snappy ([Snappier](https://www.nuget.org/packages/Snappier), MIT).
- **PASO 2** (`HapLab.View`, WPF): presentación GPU de esos frames con la MISMA
  técnica de superficies del compositor real de WinCue (D3D11 → shared handle →
  D3D9Ex → D3DImage). Ver la sección "Paso 2" más abajo.

## Cómo usarlo

```
# Generar la media de benchmark (no versionada; requiere ffmpeg en PATH)
powershell -File generar-media.ps1

# Info de un fichero
dotnet run -c Release -- media/bench-1080-hap1.mov

# Benchmark de decode CPU (paso 1)
dotnet run -c Release -- media/bench-1080-hap1.mov --bench

# Presentación GPU con ventana + overlay de stats (paso 2)
dotnet run --project HapLab.View -c Release -- media/bench-1080-60-hap1.mov
#   [--bench-seconds N]          acabar a los N s (default: clip entero)
#   [--upload dynamic|update]    estrategia de subida de textura (default dynamic)
#   [--pace thread|compositor]   quién marca la cadencia (default thread)
#   [--dump-frame out.png [--dump-index N]]  volcado de verificación del RT

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

---

# Paso 2 — presentación GPU con la técnica de superficies de WinCue

`HapLab.View` (WPF, net8.0-windows) es el **ensayo general de la integración**:
presenta los frames del paso 1 por GPU exactamente con la coreografía del
compositor real (`GlDxInterop` + `CompositorLayerControl`) y mide si aguanta
1080p60 y 4K60 sin drops. **Veredicto: sí, con margen de sobra** — el criterio
de éxito (1080p60 0 drops, 4K60 ≤ 1 %) se cumple.

## Arquitectura

```
hilo decode (hap-decode)      hilo presentación (hap-present)        hilo UI WPF
──────────────────────        ───────────────────────────────        ─────────────────────
.mov → sample → HapFrame  →   ring 4 frames DXT                      D3DImage.Lock()
(E/S real + Snappy,           FramePacer (floor(t·fps))                StretchRect
buffers reutilizados)         Map WRITE_DISCARD → tex BC1/BC3          compartida→present
                              triángulo fullscreen + pixel shader      AddDirtyRect
                              → RT B8G8R8A8 COMPARTIDO → Flush       D3DImage.Unlock()
                              BeginInvoke coalescido ───────────────▶ (patrón InvalidateFrame)
```

- **D3D11** (Vortice.Windows 3.6.2, MIT): textura BC1 (Hap1) / BC3 (Hap5/HapY)
  — la GPU descomprime el DXT al muestrear —, quad fullscreen por `SV_VertexID`
  (sin vertex buffer), pixel shader passthrough o YCoCg→RGB, render target
  `B8G8R8A8_UNorm` con `ResourceOptionFlags.Shared`.
- **D3D9Ex** abre ese shared handle con `CreateTexture(..., D3DFMT_A8R8G8B8,
  Pool.Default, ref handle)` → `GetSurfaceLevel(0)`. WPF **nunca** toca esa
  superficie: hay una superficie de PRESENTACIÓN separada (no compartida) que
  es el back buffer del `D3DImage`, y la copia compartida→presentación
  (`StretchRect`) ocurre SIEMPRE dentro del `Lock` con `AddDirtyRect`
  (contrato de la auditoría 05). Todas las superficies se crean UNA vez.
- **Shader HapY**: conversión Scaled YCoCg→RGB del shader de referencia de
  Vidvox (`ScaledCoCgYToRGBA`): Co=R, Cg=G, escala=B (s = B·255/8 + 1), Y=A,
  offset de croma −128/255. La misma matemática está en `YCoCgReference` (CPU)
  y testeada; el pipeline completo se validó contra el decode de ffmpeg del
  mismo frame (`--dump-frame`): **PSNR 51,6 dB** — solo redondeo del
  descompresor DXT hardware vs software, imagen indistinguible.

## Resultados (clips enteros de 30 s, testsrc2 chunked ×4, subida Dynamic)

Máquina: Ryzen 9 9900X, **RTX 5070 Ti** (driver 575.x), pantalla 1080p @ 99 Hz,
Windows 11, .NET 8 Release. `pace thread` (por defecto). Tiempos en ms.

| Clip | Presentados | Drops | Late | Subida p50/p95 | Render p50 | Copia p50 | Decode p50 | CPU (24C) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 1080p60 Hap1 | 1800/1800 | **0** | 0 | 0,05 / 0,06 | 0,05 | 0,03 | 0,10 | 0,8 % |
| 1080p60 HapY | 1800/1800 | **0** | 0 | 0,09 / 0,10 | 0,05 | 0,09 | 0,17 | 0,6 % |
| 4K60 Hap1 | 1800/1800 | **0** | 0 | 0,20 / 0,22 | 0,06 | 0,12 | 0,37 | 1,3 % |
| 4K60 HapY | 1799/1800 | **1** (0,06 %) | 0 | 0,32 / 0,37 | 0,07 | 0,06 | 0,77 | 1,5 % |
| 4K60 HapY (`--upload update`) | 1797/1800 | 3 (0,17 %) | 0 | 0,31 / 0,37 | 0,08 | 0,05 | 0,65 | 0,7 % |

- El presupuesto por frame a 60 fps es 16,7 ms; el camino caliente completo
  (subida + render + copia) consume **~0,5 ms en 4K** — sobra un orden de
  magnitud. El único drop del 4K60 HapY fue un pico aislado de 22 ms del
  `Flush` (¿compilación de shader del driver/GC?), no sistemático.
- **Subida Dynamic vs Update**: `Map WRITE_DISCARD` sobre textura DYNAMIC
  **funciona con BC1/BC3 en este driver NVIDIA** (no hizo falta el fallback) y
  rinde prácticamente igual que `UpdateSubresource` (p50 0,32 vs 0,31 ms en 4K
  BC3, ~8,3 MB/frame ≈ 26 GB/s efectivos). Nos quedamos con Dynamic (una copia
  menos conceptual); Update queda como `--upload update` y como fallback
  codificado por si otro driver rechaza BC dinámico.

## El hallazgo importante: quién marca la cadencia

El plan original ("todo en `CompositionTarget.Rendering` con reloj Stopwatch")
resultó ser un callejón que merece documentarse — ES el conocimiento del lab:

1. **Stopwatch + tick de WPF = 25 % de drops** en 1080p60 con la GPU aburrida
   (CPU 0,5 %). El timer de render de WPF dispara a ~65-70 Hz con jitter y
   cuantización de ~15,6 ms; contra un reloj de pared, `floor(t·fps)` cae en
   aliasing puro: huecos >33 ms entre ticks aunque la media sobre.
2. **Reloj del compositor (`RenderingEventArgs.RenderingTime`) = ~10 %**. Mejor
   (es la línea de tiempo con la que el DWM presentará el pase), pero el timer
   sigue sin estar enganchado al vsync ni a la cadencia del contenido:
   1609/1800 presentados en el mismo clip. Queda disponible como
   `--pace compositor` para reproducir la medida.
3. **Hilo de presentación propio (`--pace thread`, default) = 0 drops.** La
   arquitectura REAL de WinCue: el motor escribe en la superficie compartida a
   la cadencia del clip (Sleep(1) con `timeBeginPeriod(1)` + spin fino, como
   mpv) y el hilo de UI solo hace la copia dentro del Lock, coalescida
   (patrón `InvalidateFrame` de `CompositorLayerControl`). El UI coalesció
   1 frame de 1800 por corrida (el DWM compone a su ritmo; eso no es un drop
   del motor, es el mismo comportamiento que tiene hoy mpv+WinCue).

Conclusión para la integración: el player propio debe conservar el hilo de
render con reloj propio (como `MpvGpuRenderer` hoy) y NO atar la cadencia del
vídeo al tick de WPF.

## Fontanería del interop (lo que costaba encontrar)

- **D3D11 → D3D9Ex funcionó a la primera** en esta máquina: RT
  `B8G8R8A8_UNorm` + `ResourceOptionFlags.Shared`, handle por
  `IDXGIResource.SharedHandle`, y en D3D9Ex `CreateTexture` con
  `D3DFMT_A8R8G8B8` + `Usage.RenderTarget` + `Pool.Default` pasando el handle
  existente en el parámetro `pSharedHandle`. No hizo falta la ruta inversa
  (D3D9 crea y D3D11 abre, la que usa el WPFDXInterop de Microsoft) ni formatos
  alternativos.
- **iGPU + dGPU**: la máquina tiene una Radeon integrada y la RTX. El device
  D3D11 se crea sobre el adaptador `HighPerformance`
  (`IDXGIFactory6.EnumAdapterByGpuPreference`) y el device D3D9Ex se crea en el
  MISMO adaptador **emparejando por LUID** (`IDirect3D9Ex.GetAdapterLuid`); el
  ordinal 0 de D3D9 no es fiable con dos GPUs. Sin esto el shared handle no
  abre (o abre con copia cruzada entre GPUs).
- **Sincronización**: `Flush` del contexto D3D11 tras el draw, antes del
  `StretchRect` D3D9 (sin keyed mutex; suficiente para superficie compartida
  same-adapter, igual que hace hoy el interop GL↔D3D9 de WinCue con `glFlush`).
- `InvariantGlobalization=true` **rompe WPF** (el font cache lanza
  `CultureNotFoundException`): quitado del proyecto de vista. Efecto colateral:
  CA1303 se reactiva (estaba suprimido por invariant) → desactivado por
  `.editorconfig` local del proyecto con justificación.
- `Thread.Sleep(1)` real requiere `timeBeginPeriod(1)` (winmm): sin él la
  granularidad es ~15,6 ms e imposible clavar 16,7 ms de período.

## Decisiones del paso 2

- **Vortice.Windows 3.6.2** (Vortice.Direct3D11/Direct3D9/D3DCompiler, MIT):
  decisión de LAB para iterar rápido. La integración final decidirá Vortice vs
  P/Invoke crudo (el estilo `D3D9Minimal` de `GlDxInterop.cs`); la superficie
  de API usada es pequeña (device, textura, RT/SRV, Map, StretchRect) y
  portable a mano si se quiere cero dependencias.
- **Shaders compilados en runtime** (D3DCompiler): sin paso de build; la
  integración puede precompilarlos si molesta el coste de arranque (~ms).
- **Selector de frames puro** (`FramePacer`, Peek/Commit): sin dependencias de
  WPF/D3D, testeado en headless (avance normal, drops, clamp al final,
  monotonía, reintento cuando el decoder va tarde). Commit solo si el frame se
  presentó de verdad.
- **Alfa forzado a 1** en el pixel shader: el lab presenta opaco; la mezcla por
  capas es del compositor real.
- Los tests del paso 1 siguen intactos; la suite queda en **42 tests** (22 del
  paso 1 + 20 nuevos de pacing y YCoCg), cero warnings en toda la solución.

## Qué queda para el paso 3 (integración)

- Contenido real de VJ (bitrates 10-30× los de testsrc2) y E/S de disco
  sostenida; el ring de 4 frames absorbió picos de decode de 50 ms, pero con
  bitrates reales el prefetch de disco será el protagonista.
- Varias capas simultáneas (2-3 players sobre el mismo device/ventana) y
  crossfades — el pool de superficies persistentes de la auditoría 05.
- Audio y sincronía A/V (el paso 2 ignora la pista de audio).
- Decidir Vortice vs P/Invoke y dónde vive el hilo de presentación en
  `WinCue.Video` (reutilizar el contrato `IVideoLayerSurface` existente).

---

# Paso 3 — integración en la aplicación real (src/)

El núcleo del lab está PROMOCIONADO a producción en `src/WinCue.Video/Hap/`
(namespace `WinCue.Video.Hap`); el lab de `tools/hap-lab/` queda intacto como
banco de pruebas. Los cues de vídeo **HAP en contenedor .mov, con rate 1.0 y
compositor GPU activo** se reproducen por un player PROPIO (sin mpv):

- **Vídeo**: `HapCuePlayer` (demux propio + decode HAP + pipeline D3D11→D3D9)
  hacia la MISMA superficie por cue del compositor — el
  `CompositorLayerControl` no nota la diferencia (SetSurface + SetFrameCopier
  + FrameRendered→InvalidateFrame, idéntico al pipeline mpv). **Recuperan los
  fundidos de opacidad** que la ruta clásica perdía.
- **Audio**: el controlador arranca la pista del .mov en el **AudioEngine**
  con un id espejo negativo (`HapAudioId(cueId) = -20000 − cueId`): EQ
  nativa, compresor, VU por cue (remapeado al cue real para el Mixer y el
  inspector, RTA y GR incluidos), limitador — el rack entero. Ese stream es
  el **RELOJ MAESTRO** del vídeo (la App inyecta
  `AudioEngine.GetElapsedThreadSafe` vía `VideoEngine.SetHapClockProvider`).
- **Cualquier otro caso** (HAP en avi/mkv, rate≠1, modo clásico, modo canvas,
  geometría CUSTOM, demux imposible) sigue EXACTAMENTE por la ruta mpv de
  siempre. `WINCUE_HAP_ENGINE=0` desactiva el motor propio en caliente.

## Decisiones y semánticas elegidas

- **Reloj (HapMediaClock, puro y testeado)**: el audio manda mientras
  entregue elapsed — cada valor nuevo re-ancla y entre búferes se extrapola
  con el monotónico; desviaciones > 0,25 s cuentan como *resync* (traza). Si
  el externo devuelve null (vídeo sin pista de audio, audio terminado antes
  que el vídeo, espejo caído) se **congela el último anclaje y se sigue con
  Stopwatch desde ahí** — el vídeo nunca se detiene por perder su reloj.
- **Loop**: el límite de pasada lo marca el RESET del elapsed de audio (salto
  atrás > 0,5 s con la pasada avanzada); red de seguridad: envolvido por
  tiempo si el audio calla, con guarda anti-doble-pasada. `trimStart`/`trimEnd`
  y `playCount` son aritmética de índices (todo intra): el `HapFrameSource`
  decodifica índices LÓGICOS `start + (L % seg)`.
- **Fin de cue**: el fin NATURAL lo marca el VÍDEO (pacer `Finished` en la
  última pasada → `Ended` → mismo camino que el EndFile de mpv, con la misma
  guarda anti-eventos-póstumos). Si el audio acaba antes, congelar-y-seguir.
- **Fallback sin audio doble**: si `StartHapPlayback` falla (demux/GPU), el
  engine cae a mpv clásico **MUTEADO** — el audio del cue ya suena por el
  espejo del AudioEngine; sincronía aproximada pero el directo no se queda a
  negro. Un `.mov` sin pista de audio no es error: `OnAudioError` traga los
  ids espejo (queda en el log del show) y el vídeo va con Stopwatch.
- **Pool HAP persistente** (lección auditoría 05): pipeline por (salida,
  pista, spec de superficie) vivo toda la sesión; por cue solo Assign/Release
  del sink y una `HapVideoSession` (ring de 3 texturas BC — recursos
  corrientes, sin interop). Subida `Map WRITE_DISCARD` con fallback
  `UpdateSubresource` si el driver rechaza BC dinámico. El RT compartido es
  del tamaño de la SUPERFICIE (no del vídeo): el shader pinta el rectángulo
  destino (aspecto + escala del cue) sobre fondo negro → los slots se
  reutilizan entre clips de tamaños distintos. mpv y HAP se sustituyen entre
  sí en la misma pista (`_playerSlots` unificado); crossfades con
  `HandoverSeconds` funcionan (segundo slot del pool).
- **Standby**: sin precarga en pausa para HAP (documentado): el GO paga solo
  demux del moov + primer frame (~ms). SÍ se precalienta el buffer del audio
  espejo al poner el playhead (evita el resync inicial del reloj).
- **Vortice 3.6.2 (MIT) en producción**: decisión REVISABLE anotada en el
  csproj — la superficie de API usada es pequeña y portable a P/Invoke crudo
  (estilo `D3D9Minimal`) si se quiere cero dependencias. Snappier fijado a
  1.3.1 (advisory GHSA-pggp-6c3x-2xmx).
- **D3D9Ex sin ventana de foco** (`CreateDeviceEx(..., 0, ...)`): igual que
  `D3D9Minimal.CreateDevice(0)` del interop mpv, probado en producción — el
  lab usaba el hwnd de la ventana, innecesario para un device de superficies.

## Tests (312 → 342, todos en verde; build a cero warnings)

- Controlador (14 nuevos, `HapRoutingTests`, motores falsos + caché de códec
  sembrada): enrutado con/sin compositor, rate≠1, kill-switch, audio espejo
  con params correctos, Stop/PANIC/pausa/fin natural arrastran el espejo,
  volumen/EQ en vivo al espejo, VU remapeado, error del espejo tragado,
  standby precalienta el audio, FADE cues funden el espejo.
- Vídeo (15 nuevos): `HapMediaClockTests` (reloj puro: extrapolación,
  congelar-y-seguir, pasadas por audio y por tiempo, guarda anti-doble,
  pausa, resyncs) y `HapCoreTests` (demux/decode con media ffmpeg real,
  `HapFrameSource` con trim+pasadas verificado contra decode independiente,
  FramePacer promocionado).
- Audio (1 nuevo): `AudioDecoder` extrae la pista de audio de un `.mov` HAP
  real vía FFmpeg (la ruta que usa el espejo).
- Smoke `--smoke`: exit 0.

- **Conversión a HAP in-app disponible** (paso 3/4): menú contextual de la
  tabla («Convertir a HAP» por cue y «Convertir todos los vídeos a HAP»), cola
  secuencial sin bloquear la UI con progreso real (`HapConvertService` +
  receta única `MediaImporter.HapConversionArgs` con `-chunks 4`); al terminar,
  el cue pasa a `<nombre>.hap.mov` y el siguiente GO enruta por el motor propio.

## Límites conocidos / paso 4

- El motor propio NO cubre: modo canvas (multi-salida de un render), rate ≠ 1,
  geometría CUSTOM (translate/rotate), HAP en avi/mkv — todos caen a la ruta
  clásica actual. HapM/Hap7/HapH tampoco (el parser los rechaza → fallback).
- El AudioEngine decodifica el buffer ENTERO a RAM (float32): vídeos muy
  largos (>1 h) pagan cientos de MB — límite heredado de la arquitectura de
  audio, aceptable para clips de directo; revisar si aparece el caso.
- Validación en vivo pendiente con contenido real (bitrates 10-30× testsrc2)
  y varias capas HAP simultáneas + crossfades largos; la telemetría está
  (drops/resyncs en engine-trace.log con WINCUE_MPV_LOG).
- Si el driver no da `EnumAdapterByGpuPreference` o el shared handle no abre,
  el fallback es por cue (mpv muteado): probar en máquinas solo-iGPU.
- Futuro: precarga en pausa real para HAP (primer frame ya subido), P/Invoke
  para quitar Vortice si se quiere, y quizá mover también los códecs
  «normales» al motor propio (fase «motor de vídeo» completa).
