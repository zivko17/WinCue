# Diseño — Secuence: editor de canvas y compositor por puente D3D9→D3D11

*Escrito 2026-08-25, en sesión LOCAL sobre la máquina de Andrés, a partir de
`claude/secuence-hitos-0-1-1occ8p` @ `e1de77cf` (árbol limpio, 1.194 pruebas en
verde).*

El encargo, literal: **«quiero que la parte de la salida de vídeo en los
ajustes sea igual que en WinCue, que también tenga la parte del canvas para
poder escalar las salidas al gusto, estilo Advanced Output de Resolume».**

Preguntado el alcance, Andrés eligió **paridad con WinCue** —ni más ni menos
que lo que WinCue hace hoy— y **que funcione de verdad**: traer el compositor
a Secuence, no poner un interruptor decorativo.

---

## El problema

La pestaña Vídeo de WinCue tiene tres cosas que la de Secuence no:
la casilla **Compositor GPU**, la casilla de **modo canvas** y el
`CanvasEditorControl` — el lienzo con los cuatro recortes arrastrables, con
imanes, campos numéricos y presets de tamaño. Su propia cabecera dice «estilo
Watchout/Resolume».

Copiar la interfaz es media hora. El problema es la otra mitad: **en Secuence
esas casillas no cambiarían un píxel**. `CanvasLayout.CanvasMode` exige
`video_compositor`, y `VideoEngine` solo mira `CanvasOutputs` cuando hay
`_gpuSurfaces`. Secuence nunca llama a `SetGpuLayerSurfaceProvider`: mete mpv
en un HWND hijo y ya está. Y un ajuste que no hace nada es peor que no
tenerlo.

## Reglas que gobiernan este diseño

Las seis del proyecto siguen en pie (ver `TRASPASO-SECUENCE.md`). Las tres que
más mandan aquí:

1. **WinCue no cambia de comportamiento ni de aspecto.** Se está vendiendo.
   Todo lo que se toque de `WinCue.Video` es **aditivo y con implementación
   por defecto**, para que su ruta actual no se entere.
2. **Prohibido copiar código entre productos.** El cálculo del editor sube a
   `WinCue.Presentation` con pruebas, y **WinCue pasa a usarlo**.
3. **Primero se extrae el cálculo, después se pinta.**

Y una cuarta, del plan del compositor: **nada bloquea el hilo de render**.

## Lo que NO se toca

`WinCue.App` — ni una línea. Sigue con su árbol de `D3DImage` y su
`CompositorLayerControl`, salvo el refactor del editor de la sección 2, que va
en su propio commit y no cambia comportamiento.

`MpvGpuRenderer` en lo esencial, el interop GL↔D3D9, el motor de audio, la
máquina de estados de cue y el formato del show. La ruta clásica de Secuence
(mpv en HWND hijo) **sigue viva y es el fallback**.

Fuera de alcance a propósito: genlock entre salidas, soft-edge / solape,
rectángulo de destino dentro de la pantalla física (lo que Resolume sí tiene y
WinCue no), warping, multiviewer y grabación. Nada de eso es paridad.

---

## Hallazgos verificados

Los cinco que sostienen el diseño. Todos leídos en el código, no supuestos:

1. **El modo canvas de WinCue no usa el compositor nuevo.**
   `MpvGpuRenderer.RenderThread` crea **una superficie compartida D3D9 por
   recorte** (`D3D9Minimal.CreateSharedRenderTarget` → `_gl.AddView(...)` con
   su `v.X, v.Y`), y `AcquireGpuSlot` manda cada una al sink de `v.Screen`. El
   canvas es eso: N vistas de un solo render, repartidas por ventanas.

2. **Hay dos superficies por vista**: la del interop (registrada en GL) y una
   de **presentación**, y el copiador (`CopyPresent` → `StretchRect`) va
   *dentro del Lock* del `D3DImage`. Tocar la del interop fugaba VRAM
   (auditoría 05). Este diseño respeta esa separación tal cual.

3. **El handle compartido de la superficie de presentación se tira hoy**
   (`var (present, _) = …`, `MpvGpuRenderer.cs:217`). Es exactamente lo que
   hace falta para abrirla en D3D11.

4. **El device D3D9 ya nace `D3DCREATE_MULTITHREADED`**
   (`GlDxInterop.cs:113`), que es lo que permite que el copiador corra en un
   hilo distinto al de render de mpv. En WPF ese hilo es el de interfaz; aquí
   será el del compositor.

5. **El compositor de la fase 2b está construido y verificado por píxel**:
   `LayerRenderer`, `RenderGraph`, `RtPool`, `Ramp`, `OutputCompositor`,
   `SwapChainSink`/`TextureSink` — 2.382 líneas en `src/WinCue.Video/Gpu/` con
   `CompositorTests`. **`SwapChainSink` recibe un HWND**, y Secuence ya crea
   uno por ventana de salida (`HwndHijo`). En modo GPU mpv arranca con
   `vo=libmpv`, sin ventana: ese HWND está libre.

Y uno que cambia lo que Secuence gana: **Secuence no implementa
`SetVideoCueOpacity`** (se queda con la implementación por defecto vacía de
`IShowView`). Hoy, en Secuence, la opacidad y los cruces de vídeo **no hacen
nada**.

## Decisiones tomadas

| Decisión | Elegido | Por qué |
|---|---|---|
| Alcance | Paridad con WinCue | Elección de Andrés |
| Camino | **A — puente D3D9→D3D11** | Deja el renderer de producción intacto; reutiliza el compositor ya construido |
| Fase 2b nativa (`MpvSource`) | **No ahora** | Semanas, exige el F0 que nunca se hizo (`interop2` en iGPU). Queda como paso siguiente: se cambia la fuente y se conserva el grafo |
| Una instancia de mpv por salida | **Descartado** | Cada salida decodificaría por su cuenta: dos proyectores fuera de sincronía. En teatro se ve |
| «Salidas a pantalla completa» | **Fuera** | Esta ventana no tiene modo ventana, por diseño |
| «Fundido del blackout» | **Fuera** | El compositor lo haría posible por primera vez; Andrés prefiere no meterlo en esta tanda |
| PGM | **Espeja el compositor, con selector** | Hoy decodifica el archivo por segunda vez y no enseña lo que sale de verdad. Con varias salidas no existe «lo que sale» en singular, así que lo elige quien mira |

---

## Sección 1 — Arquitectura: dónde vive cada pieza

### El motor compartido — tres cambios, los tres aditivos

(Dos en `WinCue.Video`, uno en `WinCue.Core`.)

**`MpvGpuRenderer`** guarda el handle compartido de cada superficie de
presentación y lo expone: `ViewSharedHandle(i)`. WinCue no lo lee.

**`IVideoLayerSurface`** (en `WinCue.Core/MediaEngineContracts.cs`) gana
`void SetSharedHandle(nint handle) { }` — **con cuerpo vacío por defecto**,
igual que `AssignCue`/`ReleaseCue`. `CompositorLayerControl` de WinCue lo
ignora sin enterarse; el de Secuence lo recibe. `VideoEngine` lo llama junto
al `SetSurface` que ya hace, en los tres sitios donde monta sinks (mpv,
HAP y ProRes).

**`Gpu/SharedSurfaceSource : IVideoSource`** — abre un handle compartido
D3D9Ex como textura D3D11 (`OpenSharedResource`) y la publica al grafo. No
toca mpv ni el interop GL: entra por donde ya sale la imagen.

### `Secuence.App` — dos piezas nuevas

**`SalidaGpu`** — un `OutputCompositor` + `SwapChainSink` por ventana de
salida, presentando sobre el HWND hijo que ya existe. Nace perezosamente, la
primera vez que un cue pide superficie en esa salida.

**`CapaCompositor : IVideoLayerSurface`** — lo que el proveedor entrega a
`VideoEngine`. Guarda superficie, handle, copiador y cue; da de alta y de baja
su nodo en el grafo de su salida, con la z de su pista y su opacidad.

### El enganche

Una línea en `App.xaml.cs`, igual que WinCue:

```csharp
_video.SetGpuLayerSurfaceProvider(
    (screen, layer, seq) => window.ObtenerSuperficieCompositor(screen, layer, seq));
```

`MainWindow` enruta a la `VideoOutputWindow` de `screen`, que devuelve el
`CapaCompositor`. **El canvas no necesita código propio**: `AcquireGpuSlot` ya
crea un sink por vista y lo manda a `v.Screen`.

---

## Sección 2 — El editor de canvas y la pestaña

### Lo que sube a `WinCue.Presentation`

**`CanvasEditorLayout`**, con pruebas:

- **Encaje del lienzo en la vista** — `Scale` y `Origin`: el canvas cabe
  centrado sea cual sea su proporción. 5760×1080 y 1920×2160 son casos
  distintos y hoy no los prueba nadie.
- **Región → rectángulo de vista** y su inversa (lo que convierte un arrastre
  en píxeles de canvas).
- **Los imanes**: bordes y mitades del canvas, más los bordes de las otras
  salidas habilitadas. Con el detalle que se pierde al copiar a ojo: **el
  umbral son 10 px de PANTALLA**, así que se divide por la escala.
- **Los topes** de mover y redimensionar (mínimo 16 px de canvas, nunca fuera
  del lienzo) y la lista de presets de tamaño.
- El **formateador** de `"x,y,w,h"`, al lado del parser que ya existe:
  `CanvasLayout.ParseRegion` en `WinCue.Controller`, que Presentation ya
  referencia. No se reimplementa.
- **`OutColors`**, hoy duplicado en `CanvasEditorControl` y en
  `Secuence.App/MainWindow.xaml.cs`: la misma lista de cuatro colores de
  identidad de salida.

`CanvasEditorControl` de WinCue **pasa a usarlo**. Refactor sin cambio de
comportamiento, en su propio commit, aislado, para poder revertirlo solo.

### El editor de Secuence

XAML plano: un `Canvas` con un `Border` por salida, rótulo `S1..S4` y asa de
redimensionar en la esquina inferior derecha. **Sin Win2D**: son rectángulos
con texto, y los elementos nativos ya hacen hit-test y captura de puntero.
Debajo, los campos numéricos por salida y el tamaño del canvas con presets,
como en WinCue.

Ojo con lo ya pagado: los mínimos del tema de WinUI (`TextBox` 64,
`CheckBox` 120) descuadran anchos, y el `Padding` por defecto es (6,4) contra
(4,2) de WPF.

### La pestaña

En el orden de WinCue: casilla **Compositor GPU** con su nota, y grupo
**CANVAS** con la casilla de modo, su nota y el editor debajo. El registro de
campos de `SettingsPanel` ya sabe leer y escribir por clave; las claves son
las mismas que usa WinCue (`video_compositor`, `video_canvas_mode`,
`surface_width/height`, `video_output_region[2-4]`,
`video_output_enabled[2-4]`), y el motor ya las entiende.

---

## Sección 3 — Flujo, hilos y fallos

### Un GO en modo GPU

Sin una línea nueva en el motor: `MainController` → `CanvasLayout.ParamsFor`
(con `CanvasOutputs` si el canvas está activo) → `AcquireGpuSlot` crea el
`MpvGpuRenderer` con sus vistas y, por cada vista, pide superficie al
proveedor con `screen = v.Screen`. Secuence devuelve un `CapaCompositor`, que
al recibir el handle abre la textura D3D11 y se da de alta como nodo en el
grafo de su salida.

### Los hilos

- El hilo de render de mpv **solo levanta una bandera** en `InvalidateFrame`.
  Nada lo bloquea.
- El hilo del compositor, despertado por el vsync del swapchain, llama al
  `copyFrame()` de cada capa visible (el `StretchRect` interop→presentación),
  muestrea y presenta. El contrato dice que el copiador va «dentro del Lock»;
  **aquí el Lock es ese hilo**: nadie más toca la superficie de presentación.
- Entre salidas, la sincronía es la de hoy: un solo decodificado y un solo
  render alimentan las N vistas, y cada salida presenta a su vsync. **Sin
  genlock** — eso es de la ruta nativa, y no se vende como hecho.

### Fundidos

Secuence implementa `SetVideoCueOpacity` por primera vez, con `Ramp` (7
pruebas puras ya en verde) sobre el reloj del compositor. Es la primera vez
que un crossfade de vídeo hace algo en Secuence.

### Cuando falle

`VideoEngine` ya tiene `_gpuFailed` y cae solo a la ruta clásica. Si no hay
D3D11, si `OpenSharedResource` falla o si el swapchain no nace, Secuence lo
señala y el vídeo **sigue saliendo por el HWND de siempre**.

`Blackout`, `PANIC` e `identificar` no cambian: ocultan el HWND hijo, y con él
el swapchain. HAP y ProRes entran por el mismo contrato de sink y siguen
forzando `CanvasOutputs = null`, igual que en WinCue.

### El PGM

Pasa a espejar el compositor, **con un selector de qué mira**: `LIENZO`,
`SALIDA 1`, `2`, `3`, `4`. Va en la cabecera de color que el monitor ya tiene
(`PgmHeader`), así que no hay disposición nueva que inventar. Solo se ofrecen
las salidas **habilitadas**, y `LIENZO` solo cuando el modo canvas está
puesto — con una sola salida, lienzo y Salida 1 son la misma imagen. Por
defecto: `LIENZO` en modo canvas, `SALIDA 1` si no. La elección **se guarda**
(`pgm_monitor_source`), como el alto del panel inferior.

Esto es interfaz que WinCue **no** tiene. Es la única desviación deliberada de
la paridad en toda la tanda, y sale de que Secuence puede tener cuatro salidas
y un lienzo, donde el PGM de WinCue solo tenía una imagen que espejar.

**Y esto es lo que gobierna los hilos.** Un mismo device D3D11 para toda la
app (es el sentido de `GpuDevice`), y **un compositor con su hilo y su
swapchain por salida**. El PGM no tiene hilo propio: dibuja **dentro del
fotograma del compositor de la Salida 1**, como sink secundario.

- Con una salida seleccionada, muestrea la textura de esa salida.
- Con `LIENZO`, coloca cada vista en su sitio (`v.X, v.Y, v.W, v.H`)
  reescalado al hueco del monitor. **Cada vista ya llega recortada** —
  `MpvGpuRenderer` renderiza el recorte de cada salida en su propia
  superficie—, así que el lienzo se reconstruye sin decodificar ni renderizar
  nada extra: donde no cubre ninguna región, negro.

**El copiador lo llama SIEMPRE el compositor dueño de esa salida**, nunca el
PGM. El PGM solo muestrea, en lectura. Eso deja una ventana de tearing
teórica al mirar salidas que no son la 1, mientras su dueño hace el
`StretchRect`. Se acepta a propósito: **es un monitor, no la sala** — y el
precio de evitarlo sería sincronizar los hilos de las salidas entre sí, que es
justo lo que no se debe hacer en el camino que va a pantalla.

`LiveMonitorControl` recupera así la rama que su propia cabecera dejó anotada:
«si el compositor llega a Secuence, esa rama sube entonces». Cuando el cue en
pantalla no tiene superficie de compositor (ruta clásica, HAP y ProRes
aparte), el PGM sigue decodificando su copia muda, exactamente como hace
WinCue en ese caso.

---

## Sección 4 — Cómo se verifica

**Puro, en `WinCue.Presentation.Tests`** — `CanvasEditorLayout`: encaje en
proporciones raras, umbral del imán en píxeles de pantalla dividido por la
escala, topes de mover y redimensionar, ida y vuelta del texto `"x,y,w,h"`.
Es lo que impide que los dos editores diverjan.

**Por píxel y sin ventana, en `WinCue.Video.Tests`** — la prueba que decide si
el puente existe: crear una superficie compartida D3D9Ex, pintarla de un color
conocido, abrirla con `OpenSharedResource`, dibujarla con `LayerRenderer`
sobre un `TextureSink` y **leer el píxel de vuelta**. Misma convención que
`CompositorTests` y `GpuSourceTests`: sin GPU utilizable, la prueba se salta
en vez de mentir.

Y una para el espejo: dos `TextureSink` sobre el mismo compositor devuelven
**el mismo fotograma**.

**Puertas de aceptación:** build con `TreatWarningsAsErrors=true` a 0 avisos,
las 1.194 pruebas existentes en verde, guardián de paleta OK, CI en verde
sobre `windows-latest`.

### `## PENDIENTE — verificación en pantalla (Andrés)`

Lo que ninguna máquina ve:

- Un vídeo cruzando **dos pantallas** con el canvas puesto.
- Un **crossfade con opacidad real**, que en Secuence no ha existido nunca.
- **Blackout y PANIC** con el swapchain vivo.
- **Identificar salida** (el rótulo XAML sobre el HWND oculto).
- El **PGM espejando**, y su selector: `LIENZO` con el canvas puesto (que el
  lienzo se reconstruya entero y encaje), y cada salida por separado.
- **Apagar el compositor** y comprobar que se vuelve a la ruta clásica de
  siempre.
- Y en WinCue, tras el refactor del editor: que la pestaña Vídeo sigue
  **exactamente igual**.

---

## Riesgos, por orden

1. **La sincronización de la superficie compartida.** D3D9Ex→D3D11 por handle
   heredado no trae keyed mutex; la protección es la misma que usa hoy el
   `D3DImage`: copiar a la superficie de presentación justo antes de
   muestrear, en un solo hilo. Si aparece tearing, se ve enseguida y el
   fallback es inmediato (apagar el compositor).
2. **La primera vez que Secuence pinta con un swapchain propio.** El HWND
   hijo ya existe y ya se redimensiona; lo nuevo es quién pinta dentro. El
   riesgo real es el ciclo de vida: recrear el swapchain al cambiar de tamaño
   o de monitor.
3. **Regresión en WinCue por el refactor del editor.** Mitigado con commit
   aislado, pruebas puras y la mirada de Andrés.
4. **DPI distinto entre monitores** — nunca se ha verificado (los dos de
   Andrés están al 100 %). No lo resuelve este diseño; queda anotado igual que
   estaba.

## Lo que esto desbloquea (y no entra aquí)

El grafo en Secuence deja a tiro, sin volver a tocar la fontanería: el
**multiviewer** (otro sink dibujando miniaturas de los nodos), el fundido del
blackout, las cartelas como nodos, y la ruta nativa `MpvSource` — que se hace
cambiando la fuente y conservando todo lo demás.
