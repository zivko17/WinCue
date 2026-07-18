# Auditoría WinCue — 04 · Sesión de estrés: RAM, VRAM y handles

**Fecha:** 2026-07-18 · **Rama:** `pruebas-v2` · **Estado:** infraestructura lista; la sesión larga la lanza el operador.

**Por qué tres frentes:** el vídeo va por libmpv (P/Invoke) y el compositor crea superficies D3D9/GL por cue. Los perfiladores de .NET solo ven el heap gestionado — **ni la memoria nativa de mpv/FFmpeg ni la VRAM aparecen ahí**. Hay que medir por separado: (1) heap .NET, (2) proceso completo/nativo/handles, (3) GPU.

---

## 1. Piezas montadas

| Pieza | Qué hace |
|---|---|
| `tests/stress/show-estres.json` | **Show de tortura** (~46 s/vuelta, todo encadenado): 4 crossfades cortos seguidos en la misma pista, cruce entre capas, imagen a corte, crossfade de audio simultáneo, cue FADE que funde y para, vídeo en la **salida 2** (crea/usa segunda ventana), STOP ALL con fundido y WAIT final. Cada vuelta crea y destruye ~10 players de mpv + sus superficies del compositor. Medias: las de `tests/show-prueba` (`tools/generar-show-prueba.ps1`). |
| `--stress <show.json> [--stress-loops N]` | **Modo desatendido** (`StressRunner` en WinCue.App): carga el show, abre la salida 1, hace GO, y cuando detecta 3 s sin nada sonando relanza la vuelta. Con N vueltas cumplidas sale con exit 0. Sin diálogos (como `--smoke`). |
| CSV de la app (cada 30 s) | `WINCUE_STRESS_CSV` o `logs/stress-<fecha>.csv`: `timestamp, elapsed_s, loops, cues_fired, managed_mb, working_set_mb, private_mb, handles, gen0, gen1, gen2`. |
| `scripts/stress.ps1` | Orquesta todo: build Release, lanza la app, `dotnet-counters` en paralelo (CSV cada 10 s), `dotnet-gcdump` al inicio (t+60 s), **cada hora** y al final (con la app aún viva), cierre ordenado. Todo en `artifacts/stress/<fecha>/`. |

### Uso

```powershell
# Una vez:
dotnet tool install -g dotnet-counters
dotnet tool install -g dotnet-gcdump
dotnet tool install -g dotnet-dump          # opcional, para heap nativo/volcados completos
powershell -File tools\generar-show-prueba.ps1

# Sesión de 4 horas (recomendada; ~310 vueltas, ~3000 ciclos de player):
.\scripts\stress.ps1 -Hours 4

# Antes de lanzar: en Settings → Vídeo activa el COMPOSITOR (y modo canvas si
# quieres estresarlo también) — el modo estrés respeta tus settings.
```

---

## 2. Cómo interpretar los resultados (.NET)

### El patrón de una fuga real

La pregunta clave no es "¿sube la memoria?" (siempre sube al principio: cachés, JIT, pools). La pregunta es: **¿baja tras un GC de generación 2?**

- **Fuga real:** en `counters.csv`, `gc-heap-size` dibuja una **sierra cuyo suelo sube**: cada gen-2 (columna `gen-2-gc-count` incrementándose) libera menos de lo que se acumuló. En `proceso.csv`, `managed_mb` correlaciona con `loops` linealmente sin aplanarse nunca.
- **Crecimiento benigno (caché/warm-up):** sube los primeros minutos y se **aplana**; los gen-2 devuelven el heap al mismo suelo una y otra vez. Una caché con techo (p.ej. waveforms) da un escalón que se estabiliza.
- **`loh-size`** (Large Object Heap): aquí viven los buffers de audio decodificados y arrays grandes. Si crece por vuelta sin liberarse → algo retiene buffers de cues ya terminados (sospechoso típico: una lista/diccionario que no se limpia, o un evento suscrito).
- **Comparar los `.gcdump`** (Visual Studio → abrir el archivo → "Compare with baseline", o PerfView → Diff): ordenar por *retained size delta* entre `00-inicio` y `99-final`. Lo que crece proporcional al número de vueltas es la fuga; mirar su **retention path** (quién lo agarra) — eso da el culpable directamente.

### Umbrales orientativos para el veredicto

Tras 4 h del show de tortura: heap gestionado estable ±20 MB tras el warm-up = OK. Crecimiento sostenido > ~5 MB/h correlacionado con `loops` = investigar con los gcdumps.

## 3. La parte NATIVA (libmpv/FFmpeg/GL) — lo que dotnet-counters NO ve

La memoria de mpv (buffers de decodificación, colas de frames) es nativa. Señales y herramientas:

1. **`private_mb` vs `managed_mb` (proceso.csv):** si `private_mb` crece y `managed_mb` está plano, **la fuga es nativa**. Es LA gráfica que separa los dos mundos. (`working_set` es ruidoso — el SO recorta; usar privada como referencia.)
2. **`handles` (proceso.csv):** los wait handles/HWNDs/archivos son fugas de handle, no de bytes. Debe estabilizarse; +N handles por vuelta = fuga (el fix de `MpvGpuRenderer._wake/_ready` de la fase 1 era exactamente esto). Para desglosar por tipo: **Process Explorer** (Sysinternals) → propiedades del proceso → pestaña *Performance* (Handles/GDI/USER) o handle.exe.
3. **VMMap** (Sysinternals): abre el proceso y mira *Private Data* y *Heap* nativos entre el minuto 5 y la hora 4 — si crece, "Trace" señala qué asignaciones. Para atribución fina de heap nativo, una segunda sesión con **umdh** (Windows SDK: snapshot inicial/final de stacks de asignación) o **Visual Studio → Performance Profiler → Memory Usage con "Native"**.
4. **`dotnet-dump collect -p <pid>`** + `!address -summary` (WinDbg) como alternativa si VMMap no está disponible.
5. Pista de diseño: cada vuelta crea/destruye ~10 `MpvPlayer`. Si la privada sube ~X MB fijos por vuelta, buscar en mpv: ¿`mpv_terminate_destroy` llegó a ejecutarse? (un `Join` de 2 s vencido en `MpvPlayer.Dispose` dejaría cores vivos — visible también como hilos acumulándose en Process Explorer → *Threads*).

## 4. La VRAM (compositor GPU) — RenderDoc / PIX

La VRAM tampoco la ve nada de lo anterior. Dos niveles:

### Nivel 1 — ¿hay fuga? (sin capturas)

- **Administrador de tareas → Rendimiento → GPU → "Memoria de GPU dedicada"** del proceso WinCue.App (o `nvidia-smi --query-compute-apps` / GPU-Z con más detalle). Anotar al minuto 5, hora 1 y hora 4. Con el show de tortura la VRAM debe oscilar (cada cue crea/destruye superficies) alrededor de un nivel **estable**; una rampa sostenida = recursos D3D/GL sin liberar.

### Nivel 2 — ¿qué se fuga? (capturas)

- **PIX on Windows** (mejor para D3D9Ex que RenderDoc, que ya no soporta D3D9): *Attach* al proceso → pestaña **Memory** → snapshots de recursos vivos. Tomar snapshot tras la vuelta 1 y tras la vuelta 50: comparar la lista de **IDirect3DSurface9 / render targets** vivos. Lo esperable: nº de superficies ∝ cues SONANDO AHORA (± las del standby). Si crece con el nº de vueltas → superficies no liberadas; el nombre/tamaño (p.ej. 1280×720 ARGB por cada vista) delata si son las `CreateSharedRenderTarget` de `D3D9Minimal`.
- La pata **OpenGL** (contexto de mpv) no la ve PIX: para esa, la señal es indirecta — hilos "mpv-gpu" vivos en Process Explorer tras terminar sus cues (cada `MpvGpuRenderer` tiene el suyo; deben morir con su cue), y la VRAM total del punto anterior.
- Qué buscar exactamente entre cues de vídeo: por cada cue terminado deben desaparecer (a) su render target compartida por vista, (b) su renderbuffer GL, (c) su FBO, (d) el device D3D9Ex del renderer. `GlContext.Dispose`/`CleanupOnThread` los liberan **en el hilo de render**; si el `Join(4000)` de `MpvGpuRenderer.Dispose` expira, esa vuelta se los queda — correlación: fuga VRAM + hilo zombi.

## 5. Protocolo sugerido de la primera sesión

1. `.\scripts\stress.ps1 -Hours 4` con compositor activado (y una segunda sesión con modo canvas + 2 salidas).
2. Durante la sesión: apuntar VRAM (nivel 1) a los 5 min / 1 h / 4 h; dejar Process Explorer abierto con la columna de handles y threads.
3. Al terminar, traer para análisis: `proceso.csv`, `counters.csv`, los `.gcdump`, y las 3 lecturas de VRAM/handles/threads.
4. Con eso se decide: fuga gestionada (gcdumps la nombran), nativa (VMMap/umdh la localizan) o VRAM (PIX la enseña) — y el parche mínimo correspondiente.
