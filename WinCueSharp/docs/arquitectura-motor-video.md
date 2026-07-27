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
