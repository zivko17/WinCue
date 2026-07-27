# Plan ejecutable — Motor de vídeo con compositor propio

*Escrito 2026-07-26 sobre el verde de [`tools/mpv-d3d11-lab`](../tools/mpv-d3d11-lab/INFORME.md);
reescrito el mismo día para incorporar el grafo de composición.*

Este documento **se basta solo**: qué se construye, en qué orden, cómo se
verifica cada fase y cuándo se puede parar sin dejar el producto roto. El
porqué de cada capa y el diseño de conjunto están en
[arquitectura-motor-video.md](arquitectura-motor-video.md); no hace falta
leerlo para ejecutar esto.

---

## El problema

Hoy **no hay compositor**. Cada cue monta su propia tubería GPU
(`MpvGpuRenderer` o `HapGpuPipeline`), cada una acaba en su propia superficie
D3D9Ex dentro de un `D3DImage`, y **la mezcla la hace el DWM** animando
`Opacity` de WPF. De ahí salen cuatro límites estructurales: la transición no
es nuestra, no hay tick de salida, no hay textura final que capturar, y el
coste escala por capa.

## Reglas que gobiernan el plan

1. **Aditivo y reversible.** La ruta actual sigue viva y por defecto hasta F3.
   Todo lo nuevo, detrás de flag. Esto toca el camino crítico de la salida y la
   beta debe poder salir sobre mpv sin esperarlo.
2. **Grafo desde el primer día, no lista de capas.** Es la única decisión de
   este plan que no se puede deshacer barato.
3. **PTS desde el primer día.** Una fuente publica frames *con marca de
   tiempo*; nadie sincroniza por "el último que llegó".
4. **Nada bloquea el hilo de render.**

## Lo que NO se toca

Motor de audio, relojes existentes (`MpvSyncPll`, `HapMediaClock`,
`FramePacer`), máquina de estados de cue, formato del archivo de show. Y fuera
de alcance: ingesta viva (SDI/HDMI/NDI de entrada), chroma key, VST3.

---

# F0 · Prerrequisitos

*No tocan producción. Deciden si el plan es sólido o una apuesta.*

| Entregable | Detalle |
|---|---|
| Lab en solo-iGPU (Intel) y AMD | Repetir T1–T8. `WGL_NV_DX_interop2` ha dado guerra justo ahí, y el lab solo probó NVIDIA moderno |
| Soak del interop | attach/detach repetido + render sostenido midiendo VRAM. El fallo de la auditoría 05 apareció **con el tiempo**, no en el primer frame |
| Fix de `opengl32` en `GlDxInterop.cs` | Hallazgo nº 4 del informe del lab (ya desglosado aparte) |

**Puerta:** si el iGPU falla, el fallback (decisión 1, abajo) deja de ser
opcional y pasa a ser prerrequisito de F1.

**Criterio de salida:** el lab en verde en tres GPUs distintas y una hora de
soak sin crecimiento de VRAM.

---

# F1 · Fundamento: un device, fuentes con PTS

*La fase más grande. Va primera porque **se verifica entera sin pintar nada en
pantalla** — readback por píxel, igual que el lab.*

> **ESTADO 2026-07-26 — construida y en verde, salvo dos puntos.**
>
> | Entregable | Estado |
> |---|---|
> | `GpuDevice` (device único, multithread-protected, formato parametrizado) | hecho |
> | `FrameRing` (anillo + PTS + conteo de referencias + contadores) | hecho |
> | `FrameCadence` + `IVideoSource` | hecho, 7 pruebas puras en verde |
> | `GlD3D11Interop` promovido del lab | hecho |
> | `MpvSource` | hecho, verificado por píxel (luma 128,60) |
> | `HapSource` (+ extracción de `IHapRenderTarget` y `HapRingTarget`) | hecho, verificado por píxel (luma 92,06) |
> | Pool de queries de GPU | **pendiente** — se mueve a F2, donde ya hay nodos que medir |
> | Soak / ausencia de fuga | **pendiente** (va con el soak de F0) |
>
> Criterio de salida: cumplido salvo la fuga. La prueba
> `DosFuentesDistintas_VivenSobreElMismoDevice` deja HAP y mpv publicando a la
> vez sobre UN device — lo que hoy es imposible en producción.
>
> Nada de esto toca la ruta actual: la app sigue funcionando igual. Pero la
> extracción de `IHapRenderTarget` sí tocó `HapCuePlayer` y `HapGpuPipeline`
> (cambio mecánico, cubierto por las 87 pruebas de vídeo incluida la de
> reproducción HAP real sobre GPU) → **conviene una pasada visual de un cue HAP
> antes de dar por cerrada la fase.**

### Entregables

**`GpuDevice`** — un solo `ID3D11Device` para el motor: adaptador
HighPerformance, `BgraSupport`, **multithread-protected** (lo tocan el hilo GL,
los de decodificación y el de render). Publica su LUID. El **formato de trabajo
es un parámetro** (`RGBA8` por defecto, `RGBA16F` contemplado) — solo la
fontanería; encender lineal viene en F4, por el motivo de abajo.

**`FrameRing`** — anillo de N texturas + índice atómico de "última lista" +
**PTS por slot**. Reutilizable por todas las fuentes. No es optimización, es
corrección: mientras GL tiene el lock del interop la textura es **suya** y D3D
no puede muestrearla (verificado en el lab).

**`IVideoSource`** — contrato único: `TryAcquire(tiempoDePresentación) → Frame`.
La **selección por PTS más cercano vive aquí**, en un helper compartido, para
que todas las fuentes se comporten igual.

**`HapSource`** — a `HapGpuPipeline` se le quita la mitad D3D9Ex (shared
handle, `IDirect3DSurface9`, `StretchRect`) y publica en un `FrameRing`. Es
sobre todo *borrar*.

**`MpvSource`** — promover `GlD3D11Interop` del lab. Registro como
`GL_TEXTURE_2D` (**no** `GL_RENDERBUFFER`, que es lo que vale con D3D9),
textura `Shared` + `wglDXSetResourceShareHandleNV`, `flip_y=1`. Hilo GL propio.

**Pool de queries de GPU** — para medir coste por nodo. Sin esto no hay
diagnóstico posible en máquina de cliente.

### Cómo se verifica

- Tests headless con readback por píxel (T6–T8 del lab, promovidos a
  `WinCue.Video.Tests`).
- **Test de cadencia**: 30p contra reloj de 60 Hz debe dar 2:2; 24p contra 60,
  3:2. Es la prueba de que el PTS funciona, y es unitaria y determinista.
- Soak: dos fuentes publicando en paralelo, VRAM plana.

**Criterio de salida:** dos fuentes vivas sobre un device, píxeles verificados,
cadencia correcta, sin fuga. **Nada en pantalla todavía.**

---

# F2 · El grafo y una salida

> **ESTADO 2026-07-26 — el motor está construido y verificado por píxel; falta
> enchufarlo a la app.**
>
> | Entregable | Estado |
> |---|---|
> | `RenderGraph` — nodos `source`, `transform`, `mix`, `solid`, `stack` | hecho |
> | `LayerRenderer` + `CompositorShaders` (un solo primitivo de dibujo) | hecho |
> | `RtPool` | hecho (aún sin usar: nadie necesita RT intermedia hasta los nodos `effect`) |
> | `OutputCompositor` — hilo, reloj maestro, `Mutate`, telemetría | hecho |
> | `SwapChainSink` (flip model) y `TextureSink` | hecho |
> | `Ramp` — fundidos en el reloj del compositor | hecho, 7 pruebas puras |
> | `SwapChainHost` (WPF) | hecho, compila |
> | **Rewiring de `VideoEngine` y `VideoOutputWindow`** | **pendiente** |
> | Flag `WINCUE_COMPOSITOR` | pendiente (va con lo anterior) |
> | Pool de queries de GPU | pendiente — la telemetría de CPU ya da el número que importa |
>
> **Medido en una RTX 5070 Ti**: 30 fotogramas en 500 ms (60 Hz exactos),
> **peor fotograma 0,15 ms** contra un presupuesto de 16,7 ms, cero
> *starvation*. Mezcla al 50 % de rojo y azul: B=127 G=0 R=128. Fuente HAP real
> a través del grafo: luma 92,26 con una capa.
>
> **Por qué me paro antes del rewiring:** enchufarlo exige reestructurar los
> pools de slots de `VideoEngine` (1.660 líneas) y sustituir el árbol de
> `D3DImage` de `VideoOutputWindow`. Eso es trabajo de paridad —F3— y es
> justo donde la regla del plan dice que la ruta vieja no se toca hasta tener
> con qué comparar. Hacerlo a ciegas, sin que nadie pueda mirar la pantalla,
> es como se rompe el camino que hoy funciona.

### Entregables

**`RenderGraph`** — nodos evaluados a lista de comandos en cada frame. Juego
mínimo para alcanzar lo que hoy ya se hace:

| Nodo | Para qué |
|---|---|
| `source` | una fuente de F1 |
| `transform` | escala y recorte (es lo que hace hoy el modo canvas) |
| `mix` | fundido cruzado por opacidad |
| `output` | a un sink |

Los nodos `effect` **no entran aquí** — el grafo los admite, pero F2 solo debe
igualar lo existente. Añadir efectos antes de tener paridad impide comparar.

**`RtPool`** — render targets intermedias con ping-pong.

**`OutputCompositor`** — swapchain flip-model (`FLIP_DISCARD`) sobre el HWND de
salida, hilo de render propio, objeto de espera de latencia de frame,
`Present(1)`. Es el **master clock**: cada frame evalúa el grafo en el instante
T y pide a cada fuente el frame que corresponde a T.

**Telemetría** — contador de repeticiones y *drops* por fuente, y tiempo de GPU
por nodo. Desde el principio: es el instrumento con el que se acumula la cola
larga de fiabilidad deprisa.

**`VideoOutputWindow` hospeda el swapchain** en vez de un árbol de `D3DImage`.
Esto **elimina de raíz** la clase de fallos de la auditoría 05 (dirty-rects,
renaming del driver, escribir fuera del `Lock`).

**Slate y velo de blackout se vuelven nodos** (`source` + `mix`). Hoy son
elementos WPF con `Opacity`; encima de un swapchain darían problemas de
*airspace*, y como nodos son triviales.

**Los fundidos pasan al reloj del compositor.** `SetCueOpacity(cue, op, fade)`
deja de ser `BeginAnimation(OpacityProperty)` y se evalúa por frame.

**El congelado desaparece como concepto.** `VisualFreezeRegistry` existe porque
al morir el saliente su superficie se esfumaba y la mezcla caía a negro. Con
grafo, la vida visual de un cue es la vida de su nodo: congelar es no retirarlo
todavía. Un flag.

Todo detrás de `WINCUE_COMPOSITOR=1`. Ruta vieja intacta y por defecto.

### Cómo se verifica

Un cue arranca, un cruce corre, **en paralelo con la ruta vieja y comparado a
ojo**. Más el test de cadencia de F1, ahora extremo a extremo.

**Criterio de salida:** indistinguible de la ruta actual, con el flag puesto.

---

# F3 · Paridad y cambio de defecto

*Mayormente verificación. Nada de esto es opcional para cambiar el defecto.*

| Entregable | Notas |
|---|---|
| Modo canvas y multi-salida | nodos `output` con recorte |
| Geometría | `transform` con corner-pin |
| PANIC, GO instantáneo/standby | comportamiento idéntico |
| Handover | vida de nodo (ver F2) |
| **Genlock entre salidas** | un solo hilo presenta todas (ver decisión 2) |
| Soak de 4 h | sin fuga ni crash |
| Métrica FASE 0 del roadmap | show HAP encadenado 30 min, cero cuentas negativas, cero cuelgues, sobre la ruta nueva |
| **Verificación visual de Andrés** | nada está hecho hasta que lo veas |

Al cambiar el defecto, la ruta vieja se queda **una versión más** como
fallback. No se borra.

---

# F4 · Lo que esto desbloquea

*Ya sobre cimiento sólido. Incremental y en cualquier orden.*

- **`ReadbackSink` asíncrono** — `CopyResource` a staging + fence, mapeado N
  frames después. Síncrono aquí ahoga el hilo de render: es el error clásico.
- **`MultiviewSink`** — sale casi gratis del grafo: otro compositor pintando
  miniaturas de nodos del principal. (Está en FASE 3 del roadmap.)
- **`RecordSink`** (NVENC/AMF/QSV) y **`NdiSink`**.
- **Nodos `effect`**: dip-to-black real, wipes, modos de fusión, corrección de
  color, LUT.
- **Espacio de trabajo lineal (`RGBA16F`).** Va aquí y no antes **a propósito**:
  cambia el aspecto de todos los fundidos, así que encenderlo antes de F3 haría
  imposible comparar viejo contra nuevo. Después de la paridad, es el paso que
  quita el bache oscuro del centro de los cruces.
- **Fuentes nuevas baratas**: `TitleSource` (Direct2D/DirectWrite interoperan
  nativamente con D3D11) y `CaptureSource` (`Windows.Graphics.Capture` entrega
  texturas D3D11 directas).

---

## Superficie de código

**Nuevo** — `src/WinCue.Video/Gpu/`: `GpuDevice`, `FrameRing`, `IVideoSource`,
`GlD3D11Interop` (promovido del lab), `MpvSource`, `RenderGraph`, `RtPool`,
`OutputCompositor`, sinks.

**Modificado** — `HapGpuPipeline` (se le quita D3D9Ex), `VideoEngine` (pools de
slots → nodos del grafo), `VideoOutputWindow` (árbol de `D3DImage` → swapchain).

**Retirado al final** — la mitad D3D9 de `GlDxInterop`, `MpvGpuRenderer`
(o conservado como fallback), `VisualFreezeRegistry`.

## Riesgos, por orden

1. **iGPU sin `interop2`.** Único riesgo que puede invalidar el diseño. Lo
   resuelve F0 antes de escribir código de producción.
2. **Modelo de hilos y propiedad de texturas** entre el lock del interop, los
   hilos de fuente y el compositor. Se ataca con `FrameRing` en F1, no como
   parche posterior.
3. **Regresión de paridad**: la ruta vieja lleva meses de casos reales. La red
   es el flag y el soak de F3.

## Decisiones abiertas (de Andrés)

1. **Fallback si el iGPU falla.** (a) mantener viva la ruta `D3DImage` actual
   como fallback en runtime; (b) render por software de mpv
   (`MPV_RENDER_API_TYPE_SW`) — universal pero con coste de CPU.
   *Recomendación:* (a); es código que ya funciona y ya está pagado.

2. **Sincronía entre salidas.** ¿Genlock o independientes?
   *Recomendación:* genlock — en teatro dos proyectores desincronizados se ven;
   un frame de latencia extra, no.

3. **Cuándo.** Material de FASE 2/3 del roadmap, **no** de la beta. La
   recomendación de la [hoja de ruta](hoja-de-ruta.md) sigue en pie: sacar la
   beta sobre mpv y arrancar el bucle de feedback.

## Dónde se puede parar

Cada fase deja el producto en un estado coherente:

- **Tras F0** — no se ha tocado nada; se sabe si el diseño es viable.
- **Tras F1** — código nuevo aislado, sin efecto en la app.
- **Tras F2** — ruta nueva usable con flag; la de siempre, intacta y por defecto.
- **Tras F3** — la nueva es el defecto y la vieja el paracaídas.
