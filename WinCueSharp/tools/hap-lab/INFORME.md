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
  con un id espejo negativo (`HapAudioId(cueId) = -20000 − cueId`; hoy se
  llama `VideoAudioId` — la fase híbrida lo generalizó): EQ
  nativa, compresor, VU por cue (remapeado al cue real para el Mixer y el
  inspector, RTA y GR incluidos), limitador — el rack entero. Ese stream es
  el **RELOJ MAESTRO** del vídeo (la App inyecta
  `AudioEngine.GetElapsedThreadSafe` vía `VideoEngine.SetHapClockProvider`).
- **Cualquier otro caso** (HAP en avi/mkv, rate≠1, modo clásico, modo canvas,
  geometría CUSTOM, demux imposible) sigue EXACTAMENTE por la ruta mpv de
  siempre. `WINCUE_HAP_ENGINE=0` desactiva el motor propio en caliente.

## Decisiones y semánticas elegidas

- **Reloj (HapMediaClock, puro y testeado)**: el audio manda mientras
  entregue elapsed COMPATIBLE con el local — cada valor nuevo re-ancla y entre
  búferes se extrapola con el monotónico; desviaciones > 0,25 s cuentan como
  *resync* (traza). Si el externo devuelve null (vídeo sin pista de audio,
  audio terminado antes que el vídeo, espejo caído) se **congela el último
  anclaje y se sigue con Stopwatch desde ahí** — el vídeo nunca se detiene por
  perder su reloj.
- **Audio atrasado (depuración julio 2026)**: el espejo decodifica el `.mov`
  entero a RAM antes de sonar — puede arrancar tarde y entregar elapsed≈0 con
  el vídeo ya avanzado. Re-anclar hacia atrás rebobinaba el vídeo, retrasaba
  el fin natural ese mismo tiempo (Dur "negativa" en la fila, tarjetas
  PLAYING descuadradas) y desalineaba las cadenas AUTO_CONTINUE con
  crossfade. Regla actual: retrocesos ≤ 1 s re-anclan (corrección de
  sincronía legítima); un externo > 1 s POR DETRÁS de la estimación se
  **ignora** (free-run monotónico, el fin llega a su hora) hasta que su valor
  converja con el local ([−0,25 s, +1 s]) — entonces recupera el mando.
- **Loop**: el límite de pasada lo marca el RESET del elapsed de audio (salto
  atrás > 0,5 s con la pasada avanzada, aterrizando CERCA DE 0 — un jitter a
  media pasada no es pasada) y solo si QUEDAN pasadas (jamás en la última);
  red de seguridad: envolvido por tiempo si el audio calla, con guarda
  anti-doble-pasada. `trimStart`/`trimEnd` y `playCount` son aritmética de
  índices (todo intra): el `HapFrameSource` decodifica índices LÓGICOS
  `start + (L % seg)`.
- **Fin de cue**: el fin NATURAL lo marca el VÍDEO (pacer `Finished` en la
  última pasada → `Ended` → mismo camino que el EndFile de mpv, con guarda
  anti-eventos-póstumos POR IDENTIDAD de player). Si el audio acaba antes,
  congelar-y-seguir. **Guardián de fin** en `HapCuePlayer`: en un cue finito,
  si el tiempo de pared ACTIVO (sin pausas) rebasa la duración total + margen
  (máx(1 s, 2 %)), se fuerza el `Ended` — el fin llega SIEMPRE aunque el
  reloj maestro haga cosas raras. El kill del handover de crossfade cubre
  también players HAP (antes solo mpv: el saliente HAP quedaba vivo debajo).
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
- ~~El AudioEngine decodifica el buffer ENTERO a RAM (float32): vídeos muy
  largos (>1 h) pagan cientos de MB.~~ **RESUELTO en el paso 4** (§ «Paso 4 —
  streaming»): el espejo va por ffmpeg → ring acotado (RAM constante); los
  cues de AUDIO normales conservan su pre-decode a RAM (garantía de directo).
- Validación en vivo pendiente con contenido real (bitrates 10-30× testsrc2)
  y varias capas HAP simultáneas + crossfades largos; la telemetría está
  (drops/resyncs en engine-trace.log con WINCUE_MPV_LOG).
- Si el driver no da `EnumAdapterByGpuPreference` o el shared handle no abre,
  el fallback es por cue (mpv muteado): probar en máquinas solo-iGPU.
- Futuro: precarga en pausa real para HAP (primer frame ya subido), P/Invoke
  para quitar Vortice si se quiere, y quizá mover también los códecs
  «normales» al motor propio (fase «motor de vídeo» completa).

## Endurecimiento (soak del motor HAP, Fase 0)

Métrica de Fase 0: **«un show HAP encadenado corre sin cuenta negativa ni
cuelgue»**. Se sometió el motor a estrés real hasta soltar los bugs que le
quedaban, en esta máquina (GPU + ffmpeg en PATH).

**Infraestructura del soak** (repetible con `scripts\soak-hap.ps1`):

- Media de tortura variada (`tests\hap-test\generar-tortura.ps1`, `.mov`
  gitignoreados): 1080p/4K/720p, 30 y 60 fps, HAP y HAP Q, CON y SIN pista de
  audio, y una con `pcm_s16le` a 96 kHz (audio grande → arranque tardío del
  espejo).
- Show encadenado `tests\stress\estres-hap.json`: cadena AUTO_CONTINUE de cues
  HAP con solapes (crossfade en misma pista), cues con y sin audio
  intercalados, cambios de resolución 1080→4K→720→1080, 30/60 fps,
  `trim_start`/`trim_end`, un LOOP parado por STOP y barrido final.
- Watchdog de salud (`HapWatchdog`, activable con `WINCUE_STRESS_WATCH=1`;
  integrado en `StressRunner` sobre `MainController.SnapshotActiveCues` — la
  MISMA info que ve la vista): registra a gritos en `logs\stress-watch-*.log`
  (a) remaining negativo, (b) cue colgado, (c) cadena estancada, (d) fuga de
  working set/handles vuelta a vuelta; cuenta anomalías y resume en stdout al
  cerrar. Soak autodiagnóstico.

**Bug encontrado y arreglado — cuelgue de LOOP en la costura por tiempo.** El
soak (30 vueltas) sacó un `hap presentación: decoder >1 s tarde, abandono`
**una vez por vuelta**, siempre en el cue en LOOP SIN audio maestro y justo tras
`pasada 0→1`. Causa raíz: al envolver la pasada por TIEMPO (vídeo sin reloj de
audio, o audio congelado) el `HapMediaClock` cae en `local≈WrapMargin` (0,25 s),
así que el `FramePacer` pide el frame `WrapMargin·fps` de la pasada nueva (7 a
30 fps, 15 a 60) **saltándose** los primeros. Como el salto supera el ring de 4
del `HapFrameSource` (decodificador SECUENCIAL, gateado por consumo), el frame
de destino quedaba fuera de alcance: el decoder se bloqueaba esperando que se
consumieran los saltados y el presentador esperaba el frame de destino → atasco
mutuo → el guardián de >1 s abandonaba el loop **tras una sola pasada** (el loop
se congelaba). Fix: `HapFrameSource.SkipTo` adelanta el consumo cuando el
presentador salta fuera del ring (costura de loop o resync hacia delante),
desatascando al decoder. Regresión:
`HapCoreTests.FrameSource_CosturaDeLoopMasAllaDelRing_SkipToDesatasca`.

**Falso positivo calibrado — detector de fuga del watchdog.** El working set del
motor RAMPA de forma monótona durante el arranque (pipelines del pool por spec,
decode a RAM del audio espejo, JIT) y no llega a régimen hasta ~10 vueltas; con
un warmup de 2 la ventana de fuga caía dentro de la rampa y gritaba fuga falsa.
Subido a 12 vueltas de warmup (evidencia: a partir de la vuelta ~10 el working
set oscila en sierra por el decode 4K, sin tendencia). Regresión:
`HapWatchdogTests.RampaDeArranque_LuegoRegimen_NoEsFuga`.

**Evidencia del soak final (22 vueltas, ~10 min, `artifacts\soak-hap`):**

- Watchdog **SOAK LIMPIO**: 0 remaining negativos, 0 cues colgados, 0 cadenas
  estancadas, 0 fugas candidatas.
- Motor: **44 envolvidos de loop, 0 abandonos** (antes: 1 abandono/vuelta).
- Memoria estable vuelta a vuelta: working set en sierra acotada (~460–2050 MB,
  transitorio del decode 4K) sin tendencia; managed 40–200 MB; handles
  2031→2131; GC gen2 lineal (~1/vuelta). No hay fuga: el audio espejo (decode a
  RAM) se libera al terminar el cue (`InvalidateCache`/`VideoAudioId`), el pool de
  pipelines HAP es acotado por spec (tamaño de canvas fijo, no por resolución).
- Los tests de `HapMediaClock` (cuenta negativa por audio espejo atrasado, ya
  arreglado antes) siguen verdes: no aparecieron primos hermanos por ese lado.

# Fase híbrida — audio de los vídeos mpv por el rack nativo (paso 3b)

Evolución natural del paso 3, REUTILIZANDO sus piezas: los cues de vídeo
NO-HAP (los que reproduce mpv: H.264, ProRes…) también sacan su audio por el
AudioEngine de WinCue (EQ nativa, compresor, VU por cue, limitador de bus,
fades sample-accurate). mpv queda **MUDO** para esos cues (`aid=no` fijado
antes del `loadfile` — sin pista de audio, mpv sincroniza contra su reloj de
sistema) y su vídeo se **disciplina al reloj del audio** con un PLL suave.

## Qué comparte con el paso 3 (deliberadamente TODO)

- El **audio espejo** es el mismo: id `VideoAudioId(cueId) = −20000 − cueId`
  (renombrado desde `HapAudioId` — el concepto ya no es solo HAP) y el set
  `_mirroredAudio` (antes `_hapRouted`) gobierna los ~15 puntos de enrutado:
  stop/StopAll/PANIC/pausa/reanudar/fades/crossfades/SetVolume/SetEq en
  vivo/preload/InvalidateCache y el REMAP del `CueLevelReport` al cueId real.
- El **reloj** es el provider que la App ya inyectaba para HAP
  (`SetHapClockProvider` → `AudioEngine.GetElapsedThreadSafe`): sirve tal
  cual — el PLL lo consume desde el hilo de su timer.
- El **fallback HAP→mpv** del paso 3 (motor propio no puede → mpv muteado)
  ahora también usa `ExternalAudio=true`: mudo por `aid=no` y disciplinado
  por el PLL en vez de free-run.

## Elegibilidad (la decide el controlador, `HybridAudioEligible`)

Vídeo no-HAP con: no-imagen · `rate == 1.0` · duración CONOCIDA y ≤
`video_audio_max_minutes` (setting nuevo, default 20; en Ajustes → Vídeo).
El límite existe porque el espejo decodifica el archivo ENTERO a RAM
(~23 MB/min estéreo 48 kHz float): un largometraje serían GB — el streaming
llega en el paso 4. Varispeed, duración desconocida o excesiva → ruta mpv
clásica CON audio, como siempre. Kill-switch de directo:
`WINCUE_VIDEO_AUDIO=mpv` desactiva el híbrido entero.

## Semántica del PLL (`MpvSyncPll`, lógica pura con 18 tests)

El audio ES el programa; mpv es el esclavo (lo contrario del reloj HAP, donde
el vídeo ignora al audio atrasado). Tick cada 400 ms por cue (timer patrón
`_fadeTimers`; inerte en pausa vía `MpvPlayer.ObservedPaused`; muere en
Stop/Terminate). Con `d = vídeo − audio`:

| Situación                        | Decisión |
|----------------------------------|----------|
| audio null (cargando/sin pista/terminado) | speed 1.0, sin seeks (free-run; JAMÁS rebobinar por falta de reloj) |
| \|d\| < 15 ms                    | zona muerta: no tocar |
| 15 ms ≤ \|d\| ≤ 0,25 s           | proporcional: `clamp(1 − d·0.3, 0.98, 1.02)`; se escribe `speed` solo si cambia ≥ 0.001 |
| vídeo adelantado 0,25–2 s (espejo TARDÍO, la lección cara del paso 3) | FRENADA suave `clamp(1 − d·0.3, 0.90, 1.0)` — converge sin seek atrás visible |
| vídeo adelantado ≥ 2 s           | UN hard-seek atrás a la posición del audio, sostenido 2 ticks (visible una vez, lipsync perfecto) |
| vídeo atrasado > 0,25 s          | hard-seek adelante, sostenido 2 ticks (a 1.02× tardaría >12 s; el salto adelante apenas se ve) |
| \|d\| > min(1 s, dur/2) en cue con LOOP | guarda de frontera: esperar 3 ticks (cruce de pasada vídeo/audio se resuelve solo) |
| tras un hard-seek                | 2 ticks de reposo (mpv asienta el seek; time-pos rancia oscilaría) |

## Red de seguridad (NUNCA un cue sin audio que antes lo tenía)

Si el espejo FALLA en caliente (archivo cuyo audio `AudioDecoder` no traga —
raro, ambos son ffmpeg, pero posible), `OnAudioError` del id espejo pasa de
tragarse el error a: log + cue fuera del set + `RestoreInternalAudio(cueId)`
(pone `aid=auto` y `speed=1` en el player mpv vivo, apaga el PLL) + volumen
real del cue en mpv (el fallback HAP corre con volumen 0). Para un cue HAP
nativo (sin player mpv) es no-op y sigue con su Stopwatch, como en el paso 3.
El strip del Mixer se recrea sin VU (el flag `ActiveCueInfo.MirrorAudio`
cambió) — honesto con la ruta real.

## UI

`ActiveCueInfo.MirrorAudio` (bool, default false al final del record — no
rompe llamadas): el Mixer da **VU real de stream** a los cues de vídeo con
espejo — HAP e híbridos (de paso cierra el hueco del paso 3: los cues HAP
tampoco enseñaban su VU en el strip por ser Type Video) — y reserva la nota
«VU n/d (mpv → dispositivo)» para el vídeo sin espejo. Standby: se prepara
con `ExternalAudio=true` (los parámetros deben coincidir o el GO instantáneo
no promovería) y PRECALIENTA el buffer del espejo.

## Soak de verificación (§ «Fase híbrida»)

Show `tests/stress/estres-hibrido.json` (media de
`generar-hibrido.ps1` + `generar-tortura.ps1`): H.264 CON audio, SIN audio
(fallback en caliente), pcm 96 kHz grande en .mov (espejo tardío), 60 fps,
crossfades híbrido↔HAP en la MISMA pista, loop híbrido parado por STOP.
Corrido con `scripts/soak-hibrido.ps1` (compositor ON, watchdog
`WINCUE_STRESS_WATCH=1`, trazas del PLL en `engine-trace.log`).

**Evidencia (15 vueltas, 8 min 06 s, `artifacts\soak-hibrido\20260724-011955`):**

- Watchdog **SOAK LIMPIO**: 0 remaining negativos, 0 cues colgados, 0 cadenas
  estancadas, 0 fugas candidatas.
- PLL: 75 sesiones (5 cues híbridos × 15 vueltas), **436 correcciones de
  speed y 0 hard-seeks** — el drift observado se movió entre 10 y 66 ms y la
  banda proporcional lo reabsorbió siempre (speeds usadas: 0.980–1.000).
- Fallback: **15/15 `RestoreInternalAudio`** (el clip sin audio, una vez por
  vuelta) sin marcar el cue en error ni colgar la cadena.
- Memoria estable: working set vuelta 1 = 522 MB → última = 603 MB (sierra
  del decode, sin tendencia); handles 2014 → 2004; el runner cerró solo con
  exit 0.
- Ajuste del watchdog con causa raíz: un cue en LOOP vive legítimamente más
  que su duración declarada (que la elegibilidad híbrida ahora NECESITA en el
  show) → `StressRunner` le reporta duración 0 (= sin chequeo, la convención
  documentada del watchdog).

## Límites conocidos (además de los del paso 3)

- ~~El espejo decodifica a RAM → `video_audio_max_minutes` (default 20). El
  provider de audio en streaming es la pieza del paso 4 que lo elimina.~~
  **RESUELTO en el paso 4** (§ «Paso 4 — streaming», al final): el espejo va
  por streaming (ffmpeg → ring) y el default pasa a 0 = sin límite.
- Varispeed (`rate ≠ 1`) sigue por mpv con su audio: replicar el retimado
  (resample + pitch) en el espejo no está hecho.
- El PLL corrige el reloj, no el género: con `aid=no` mpv pierde su A/V sync
  interno y pasa a reloj de sistema disciplinado — en esta máquina el drift
  real nunca pasó de 66 ms entre ticks; máquinas muy cargadas podrían ver el
  hard-seek (está trazado en `engine-trace.log` si ocurre).

# Paso 4 — streaming del audio espejo (ffmpeg → ring acotado)

El AUDIO ESPEJO de los cues de vídeo (motor HAP propio y ruta híbrida
mpv+PLL) deja de decodificar el archivo ENTERO a RAM antes de sonar y pasa a
STREAMING: un subproceso ffmpeg decodifica a `f32le` por pipe y un hilo
lector llena un RING ACOTADO de ~3 s. Esto ataca la RAÍZ de la familia de
bugs de campo del «arranque tardío del espejo» (existía porque el pre-decode
tardaba): el audio ahora llega en ~centenas de ms y las mitigaciones (reloj
HAP que ignora audio atrasado, frenada del PLL) pasan a ser redes de
seguridad que casi nunca actúan. Y elimina el límite de duración del
híbrido: un largometraje ya puede llevar el rack de audio nativo.

## Diseño elegido: fuente abstracta, UN solo provider

Se extrajo de `CueSampleProvider` una interfaz mínima de fuente
(`ICueAudioSource`: SampleRate/Channels/TotalFrames, `ReadFrames` secuencial
no bloqueante, `PassEnded`, `PrepareNextPass`/`BeginNextPass`) con dos
implementaciones:

- **`RamAudioSource`** — el AudioBuffer entero con el trim resuelto a
  índices. Los cues de AUDIO normales siguen EXACTAMENTE igual (verificado
  muestra a muestra por los 33 tests previos del provider, intactos): misma
  garantía de directo (cero I/O en PLAYING).
- **`StreamingAudioSource`** — `ffmpeg -ss/-t -i src -vn -f f32le -ac 2 -ar
  {mixRate} pipe:1` a un ring SPSC (índices Volatile, sin locks en el hilo
  RT) con backpressure natural (ring lleno → el lector espera → ffmpeg se
  frena por el pipe). La salida ya está en el formato del mixer (los espejos
  van SIEMPRE a rate 1.0): sin resampleo en proceso.

Se DESCARTÓ el `StreamingCueSampleProvider` paralelo: habría duplicado la
envolvente de fades, el loop, el VU y el elapsed (la EQ ya vivía aparte en
`EqChain`, pero el resto no). El diff real del provider es el bucle Read
consumiendo la fuente en vez de indexar el array — la matemática de ganancia
es la misma línea a línea (se aplica in-place tras leer).

## Semántica del ring / underrun / fin

- `ReadFrames` (hilo RT) JAMÁS bloquea. Ring vacío con proceso vivo =
  **UNDERRUN** → el provider emite silencio SIN avanzar la posición: el
  reloj del vídeo (ElapsedSeconds) se CONGELA y el contenido retoma donde
  iba — el PLL ve «audio atrasado» y frena suave (la red de seguridad ya
  existente). Contador `UnderrunCount` para diagnóstico.
- Fin real: ffmpeg sale (exit 0) + ring drenado → `PassEnded` → el mixer
  retira el input → STOPPED (la misma semántica de siempre).
- Muerte MALA del proceso (exit ≠ 0 sin Dispose, p. ej. kill externo):
  evento `FatalError` con el stderr → el engine CORTA el espejo al instante
  (el ring aún tendría ~3 s decodificados: cortarlo evita el solape) y emite
  ErrorOccurred → la red de seguridad del controlador restaura el audio por
  mpv (`RestoreInternalAudio`). Un cue HAP nativo sigue con su Stopwatch.
- Dispose SIEMPRE mata los subprocesos (kill del árbol) — cero zombis,
  verificado por PID en los tests (incluida la carrera spawn-async/Dispose).

## Loop / play_count: RELANZAR el subproceso (no -stream_loop)

Cada pasada es UN subproceso idéntico (mismo comando determinista → los
MISMOS samples, verificado numéricamente). El provider pre-spawnea la pasada
siguiente 4 s antes del final (`PrepareNextPass`, ring de 3 s llenándose en
paralelo) y conmuta en el wrap (`BeginNextPass`): el empalme lee del ring ya
lleno — hueco CERO salvo pasadas más cortas que el pre-spawn (~100 ms de
spawn cubiertos por el silencio de underrun, que congela el reloj: el vídeo
en loop espera al audio, no al revés). Se descartó `-stream_loop` porque el
elapsed POR PASADA (el reloj del vídeo depende de ese reset) habría exigido
contar frames módulo una longitud estimada — con el relanzo el reset es
natural al conmutar. El fin real de cada pasada lo dicta la FUENTE
(PassEnded), no la estimación de ffprobe: sin drift acumulado.

## Enrutado / fallbacks / kill-switch

- `AudioPlayParams.Streaming` (nuevo, default false): lo fija el CONTROLADOR
  para los espejos (ambas rutas, HAP e híbrida). Los cues de AUDIO normales
  no cambian.
- Cascada: spawn de streaming FALLA (sin ffmpeg, sin pista de audio…) →
  reintento por la ruta RAM clásica → si también falla, el fallback
  existente (error → RestoreInternalAudio / Stopwatch).
- `GetElapsedThreadSafe` funciona igual: es el MISMO CueSampleProvider
  registrado en `_clockProviders`.
- Preload/standby: `Preload(id, path, params)` pre-spawnea y APARCA la
  fuente con el ring lleno SIN consumir; el GO con los mismos trims la toma
  tal cual (arranque instantáneo). Aparcado incompatible → se mata y se
  relanza. `InvalidateCache`/`Cleanup`/`Stop` matan aparcados y spawns en
  vuelo (tokens de relevo). Poda FIFO a 2 aparcados.
- Kill-switch de directo: `WINCUE_MIRROR_STREAMING=0` fuerza la ruta RAM
  también para los espejos (y restaura el techo clásico de 20 min).

## Elegibilidad: adiós al límite de duración

`video_audio_max_minutes` pasa a **0 = SIN LÍMITE (nuevo default, en
SettingsManager)** y solo actúa como OPT-OUT explícito (> 0 → techo en
minutos). Con el kill-switch (ruta RAM, ~23 MB/min) vuelve el techo prudente
de 20 min salvo ajuste del usuario. Ajustes → Vídeo actualizado (el spin
admite 0 y la nota documenta la semántica). OJO: settings guardados por
versiones anteriores pueden tener 20 persistido — actúa como opt-out.

## Tests (421 en verde, 0 warnings latest-all)

- `StreamingAudioSourceTests` (ffmpeg real, se saltan sin él): paridad
  numérica con la ruta RAM sobre wav float conocido (muestra a muestra),
  trims honrados (-ss/-t == índices RAM), empalme de pasadas (pasada 2 ==
  pasada 1 exacta), elapsed que resetea por pasada vía provider, kill del
  ffmpeg a medias (silencio + fin limpio + FatalError + sin zombi por PID),
  underrun con fuente lenta simulada (Read no bloquea, silencio sin avanzar,
  el contenido retoma sin perder frames).
- `AudioEngineStreamingTests` (fábrica inyectable, sin ffmpeg): el Play con
  Streaming usa la fábrica y libera la fuente al terminar, spawn fallido →
  fallback RAM sonando, preload aparca y el GO lo consume (sin re-spawn),
  aparcado incompatible se mata, InvalidateCache mata el pre-spawn, Stop
  durante el spawn descarta y libera, kill-switch fuerza RAM (0 spawns).
- `HybridAudioRoutingTests`: espejo con Streaming=true, cue de 2 h elegible
  (sin límite), opt-out explícito, kill-switch con techo restaurado, standby
  que precalienta con los trims reales.

## Soak con cue LARGO (la evidencia del paso 4)

(pendiente: se rellena con los números del soak)

---

# Verificación visual del motor nuevo — dos fallos reportados y arreglados

Andrés probó el motor en pantalla y reportó dos cosas. Ambas eran reales.

## Fallo A — la EQ/compresor de VÍDEO estaba bloqueada en la UI

**Causa.** El editor de la pestaña `🔊 Audio` (`EqEditorControl`) gateaba por
`CueType.Audio` en tres sitios (`SetCue`, `RefreshParams` y el tick del RTA):
con un cue de VÍDEO seleccionado solo se veía «La EQ paramétrica es para cues
de AUDIO» y, con el editor oculto, desaparecía también el fader de GANANCIA
(que es el `gain_db` de ese mismo editor). El MOTOR, en cambio, lleva desde el
paso 3/3b aplicando el rack a los vídeos: por la cadena `af` de mpv
(`MpvAfChain` + `VideoEngine.SetEq`) y, con AUDIO ESPEJO, por la EQ NATIVA del
rack. Era un fallo de UI puro: el enrutado ya estaba entero (verificado en
`ApplyLiveEdits`: `gain_db` entra en `IsEqKey` y va a mpv Y al espejo).

**Arreglo.** El editor se abre para AUDIO y para VÍDEO; el mensaje de «sin
audio» queda para los tipos que de verdad no lo tienen. Y como los MEDIDORES
sí dependen del enrutado, el controlador expone
`CueHasAudioAnalysis(cueId)`:

- AUDIO → siempre (rack nativo).
- VÍDEO → solo si su audio va por el ESPEJO. Antes del GO se PREDICE con la
  misma decisión que toma `Trigger` (`WillMirrorAudio`: HAP → motor propio
  elegible; no-HAP → híbrido elegible); con el cue sonando manda el set real
  `_mirroredAudio` (si el espejo muere en caliente, el analizador se pierde
  con él).
- Resto de tipos → no.

Sin analizador NO se pinta un RTA muerto: se sigue viendo la curva (que es
matemática de los parámetros, siempre válida) y una nota honesta encima del
área del analizador. La columna VU del inspector aparece también en los vídeos
espejados, y el tooltip del VU de PGM deja de afirmar que el sonido de los
vídeos «no es medible aquí».

**Evidencia.** Con `tests/hap-test/show-eq-video.json`: un cue H.264 espejado
sonando muestra fader de ganancia (+3 dB), curva con la banda 1 a 90 Hz +6 dB,
VU del cue, RTA vivo y medidor GR del compresor moviéndose; el mismo archivo a
rate 1.5 (audio por mpv) muestra los mismos controles con la nota de «sin
analizador» y sin columna VU; un MEMO muestra el mensaje de «este tipo de cue
no tiene audio». Tests: `AudioAnalysisTests` (13) cubren el predicado en todas
sus ramas y que `gain_db`/compresor de un cue de VÍDEO llegan al motor (af de
mpv) y al espejo.

## Fallo B — los HAP 4K60 no se veían en el monitor PGM

**Causa.** Los mini-monitores (`LiveMonitorControl`) son instancias **mpv
propias y mudas** que cargaban el archivo por su cuenta:
`MainWindow.UpdateProgramMonitor` sondeaba cada 500 ms `GetProgramVideo()` y
llamaba a `ShowVideo(path, elapsed)`. Con un HAP 4K60 eso significa
**decodificar el archivo DOS veces** (motor propio + monitor) y **leerlo dos
veces del disco**, y el monitor además lo hace **en software y a resolución
completa** — su propio log lo dice: `Decoder format: 3840x2160 rgb0` +
`VO: [gpu] 3840x2160 rgb0` — con re-sincronización por drift cada 500 ms
encima. Con 1080p la máquina lo aguanta; con 4K60 real, no.

Reproducción y medida en esta máquina (RTX + NVMe): clip HAP 4K60 de ruido
**incompresible** (1,19 GB / 5 s = **237 MB/s**, `generar-tortura.ps1`, cue en
bucle, compositor activo, salida a pantalla completa en el monitor 2):

| Escenario | CPU del proceso (misma ventana) | mpv del monitor |
|---|---:|---|
| 1 cue 4K60, monitor por mpv (antes) | 17,1 s | `mpv-monitor2.log` decodifica el archivo entero |
| 1 cue 4K60, monitor ESPEJO (después) | **7,8 s** | no existe |
| 2 cues 4K a la vez, antes | 21,5 s | idem |
| 2 cues 4K a la vez, después | **11,8 s** | no existe |

**Opción elegida: la 1 (espejo de la salida real).** Con el compositor GPU
activo la salida ya vive en superficies D3D dentro del árbol WPF
(`CompositorLayerControl` con su `D3DImage`), así que el monitor las pinta con
un `VisualBrush` del contenido de la ventana de salida. No se toca el contrato
del `D3DImage` (auditoría 05): no se crean ni destruyen superficies, no se
escribe fuera del `Lock` — el espejo es un consumidor más del mismo visual, y
se refresca solo cuando la salida se refresca. Ventajas sobre la opción 2
(abaratar el mpv del monitor):

- **Correcto por definición**: PGM = lo que va a la sala. Ahora se ve la pila
  ENTERA de pistas con sus opacidades (antes solo el archivo del cue
  superior: con dos cues cruzando, el monitor mentía), la cartela del PANIC y
  el velo del blackout.
- **Gratis**: cero decodificación extra, cero lecturas extra de disco, y se
  acabó el re-sync por drift (no hay dos relojes que casar: es el mismo
  fotograma).
- **Sin límites de rendimiento**: da igual el códec, la resolución o el
  bitrate; el coste del monitor es un blit escalado.

Límite conocido y deliberado: en la **ruta clásica HWND** (compositor
desactivado) WPF no puede ver el vídeo de mpv (airspace), así que ahí el
monitor sigue reproduciendo el archivo por su cuenta, exactamente como antes
—comportamiento verificado sin regresión—. La elección es por cue y por
salida: `GetProgramVideo()` devuelve ahora también el id del cue y su salida, y
`VideoOutputWindow.HasCompositorSurface(cueId)` decide. Si la ventana de salida
está oculta, también se cae al modo clásico.

**Evidencia.** `tests/hap-test/show-pgm-4k60.json` y
`show-pgm-4k60-doble.json`. Muestreo del recuadro del PGM 16 veces cada 400 ms:
14-16 fotogramas distintos (monitor vivo), y la captura lado a lado del PGM
contra la salida real muestra el MISMO fotograma compuesto, mezcla de pistas
incluida. Tests que quedan cubiertos sin GPU: `GetProgramVideo` devuelve cue y
salida (`TransportAndChainTests`).
