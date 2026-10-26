# Motor de vídeo como plataforma — documento de diseño

*Escrito 2026-10-26 a partir de la sesión de brainstorming con Andrés.
Estado: **pendiente de revisión por Andrés**. No hay código escrito para este
diseño.*

Documentos de contexto:
- [motor-video-ficha-tecnica.md](../../motor-video-ficha-tecnica.md) — cómo es el
  motor actual por dentro.
- [comparativa-motores-video.md](../../comparativa-motores-video.md) — cómo están
  construidos OBS, vMix, Resolume y Watchout, con fuentes.

---

## 1. Propósito

Construir **un motor de reproducción de vídeo independiente, en su propio
proceso, que sirva de base a una familia de productos**:

- desde un reproductor muy sencillo;
- pasando por un lanzador de cues (WinCue, el primer cliente);
- hasta un media server completo, comparable a Resolume, Millumin, Pixera o
  Watchout.

El motor **no es exclusivo de WinCue** ni conoce el concepto de «cue». Ofrece un
modelo neutro (salidas, lienzos, grafo, medios, transporte y reloj) que cada
producto traduce desde su propio vocabulario.

**Prioridad absoluta:** reproducción de **HAP y ProRes** fiable en directo, con
la robustez de un media server profesional.

**Criterio de diseño acordado:** lo mejor posible, sin atarse al código
existente. Lo que haya que reescribir desde cero se reescribe.

## 2. Decisiones acordadas

| # | Decisión | Elección | Razón principal |
|---|---|---|---|
| D1 | Carga objetivo | 3-4 salidas o lienzo (canvas), varias capas 4K60, incluido ProRes 4444 con alfa | Caso más exigente de Andrés |
| D2 | Hardware | Especificación recomendada para shows grandes + degradación elegante en cualquier PC con GPU dedicada: portátil gaming, sobremesa con RTX gaming o RTX profesional | Versatilidad, sin servidores a medida |
| D3 | Medios | Conversión automática al importar, conservando el original | Lo caro se paga antes del show (modelo Watchout) |
| D4 | Formato de reproducción | **HAP Q Alpha** por defecto (HAP Q sin alfa); **«alta fidelidad»** por medio = ProRes original, contabilizado en el presupuesto | Estándar abierto, decodificación en GPU; 10/12 bits solo donde importa |
| D5 | Degradación en directo | Audio intocable → sincronía entre salidas intocable → prioridad por pista → repetir frame en la capa afectada, con aviso desde el primer frame perdido | Orden en el que el público nota los fallos |
| D6 | Arquitectura | Motor en **proceso propio** desde el primer día, con supervisor | Aislamiento de fallos (interfaz, decode nativo, render) |
| D7 | Alcance del protocolo | De red y preparado para **clúster** de varias máquinas | Pasar de 4 salidas a N sin rehacer el protocolo |
| D8 | Reloj | **Un único reloj maestro: la hora del motor.** El audio se esclaviza con remuestreo adaptativo | Un solo modelo de tiempo para vídeo con o sin audio, imágenes, salidas y máquinas |
| D9 | Lenguaje del motor | **C++20** (WinCue sigue en C#/WPF) | Ecosistema profesional nativo, sin recolector, mejores herramientas |
| D10 | API gráfica | **Direct3D 12** con capa de abstracción propia | Cola de copia, memoria explícita, presentación DXGI nativa, PIX. Nodos solo Windows (no hay intención de nodos Linux) |
| D11 | Códecs genéricos | FFmpeg con decode `d3d12va` dentro del motor; **mpv sale del motor** | Una sola tubería y un solo reloj para todos los formatos |
| D12 | Versión 1 | Transporte completo + efectos + mapping + Spout/NDI (alcance C), construido por subproyectos con el núcleo primero | Base para cualquier producto futuro |
| D13 | Espacio de trabajo | **RGBA16F en luz lineal** | Mezclas correctas, HDR y salidas de 10 bits |
| D14 | Audio | Pasa **dentro del motor** (C++) | Un media server necesita audio por capa, enrutado por salida y varispeed |

## 3. Arquitectura por capas

```
 PRODUCTOS   WinCue · reproductor simple · app VJ · media server con mapping/3D
 ─────────────────────────────────────────────────────────────────────────────
 SDK         cliente C++ · cliente C# · adaptador OSC · API pública versionada
 ─────────────────────────────────────────────────────────────────────────────
 SERVICIO    proceso del motor · supervisor · protocolo · telemetría · caja negra
 E/S         swapchains · Spout · NDI · (capturadoras y SDI más adelante)
 GRAFO       nodos: fuente · composición anidada · efecto · mezcla · mapping ·
             escena 3D · salida  +  ABI de plugins (C) y shaders ISF
 TIEMPO      hora del motor · transporte · parámetros animados · reloj de clúster
 MEDIOS      lectura por delante · demux · HAP / ProRes / FFmpeg d3d12va · caché
 AUDIO       motor de audio esclavo de la hora (mismo proceso)
 GPU         capa de abstracción propia sobre D3D12
```

Regla de dependencias: cada capa solo usa las de debajo. El grafo no conoce
D3D12 (usa la abstracción). Los productos solo conocen el SDK.

## 4. Procesos, protocolo y reloj

### 4.1 Procesos

```
 MÁQUINA DEL CLIENTE (p. ej. WinCue)          CADA MÁQUINA DE SALIDA (puede ser la misma)
 ┌──────────────────────────┐                 ┌───────────────────────────────────────┐
 │ Producto (interfaz,      │   protocolo     │ Supervisor (proceso mínimo)            │
 │ lógica propia: cues,     │ ──────────────► │   lanza, vigila y relanza el motor     │
 │ clips, timeline…)        │ estado deseado  │   ventana de respaldo por salida       │
 │ SDK cliente              │ + mutaciones    │ Motor (sin interfaz)                   │
 │                          │   con hora      │   1 device D3D12, todas sus salidas    │
 │                          │ ◄────────────── │   medios, grafo, audio, swapchains     │
 └──────────────────────────┘  telemetría,    │   caché local de medios convertidos    │
                               estado, errores└───────────────────────────────────────┘
 Servicio de medios (proceso aparte): ingesta, conversión, caché, prueba de máquina.
```

### 4.2 Protocolo declarativo

- El cliente no manda órdenes («play», «stop»). Manda **el estado deseado**:
  grafo, medios por nodo, posición, transporte y parámetros. Cada envío lleva
  **versión** y **hora de aplicación absoluta** en la hora del motor.
- El motor **converge** al estado deseado. Consecuencias:
  - un mensaje perdido se corrige con el siguiente;
  - un motor relanzado recibe el estado completo y retoma.
- Para no reenviar el grafo entero en cada cambio, hay mutaciones incrementales
  (diferencias sobre una versión), además de instantáneas completas.
- El canal de vuelta lleva estado aplicado, telemetría, errores y posiciones
  de reproducción.
- Transportes, con el mismo esquema de mensajes:
  - **local:** tuberías con nombre + memoria compartida;
  - **red:** TCP para control + UDP multicast para el reloj.
- El esquema es **independiente del lenguaje y versionado**. Decisión abierta
  del subproyecto 3: FlatBuffers, Cap'n Proto o Protocol Buffers.
- Un solo cliente con control por motor a la vez; los demás pueden conectarse
  en modo lectura (monitores, telemetría).

### 4.3 La hora del motor (reloj único)

- **Un único eje de tiempo** para todo: vídeo con y sin audio, imágenes,
  rampas, parámetros, todas las salidas y todas las máquinas.
- **En local:** el contador de alto rendimiento de Windows (QPC), común a todos
  los procesos.
- **En clúster:** una máquina es la referencia de hora y las demás estiman su
  desfase continuamente. El error estimado es visible por nodo (equivalente al
  «NTP Diff» de Watchout).
- **Reloj de sala opcional:** si existe (wordclock, Dante, genlock, LTC), la
  hora del motor se disciplina a él.
- **Audio esclavo:** cada dispositivo de audio sigue la hora del motor mediante
  remuestreo adaptativo (corrige la deriva del cristal de la tarjeta, típicamente
  20-50 ppm ≈ hasta 180 ms/hora). Ver la sección 9.
- **Elección de frame:** cada capa calcula la posición del medio **en el instante
  en que el frame será visible** (predicho con las estadísticas de vsync de
  DXGI), no en el instante actual. Posición = `pos0 + velocidad × (t − t0)`.
  Regla de selección: el último frame cuyo PTS no sea posterior a ese instante
  (cadencias 2:2 y 3:2 correctas sin casos especiales), con reanclaje tras una
  discontinuidad (loop, salto) en lugar de intentar alcanzar el tiempo perdido.

### 4.4 Sincronía entre salidas

- **Misma máquina:** todas las salidas eligen frame con la misma hora del motor;
  el contenido queda alineado al mismo vsync.
- **Entre máquinas:** alineación del orden de milisegundos por el reloj de red.
- **Sincronía exacta de barrido** (*edge blending* entre proyectores): solo con
  genlock/framelock por hardware (NVIDIA RTX profesional + tarjeta Sync).
  Soportado como opción, nunca requerido.
- **Modo determinista** de cambio de estado: hora de aplicación = ahora + 1
  frame, para que todas las salidas y máquinas cambien en el mismo vsync.

## 5. Dentro del motor

### 5.1 Pila técnica

C++20 · MSVC · CMake + vcpkg · D3D12 (`ComPtr`) · FFmpeg (LGPL, enlace dinámico)
· Snappy · GoogleTest. Licencias verificadas una a una en el subproyecto 1
(ver la sección 13).

### 5.2 Capa de abstracción GPU

Una capa propia y pequeña (texturas, buffers, heaps, colas, fences,
swapchains, *pipelines*) sobre la que se escribe todo lo demás. Objetivos:

- concentrar la dificultad de D3D12 (barreras, residencia) en un sitio
  probado;
- permitir una implementación Vulkan en el futuro sin tocar el grafo.

Siempre activos en CI: capa de depuración de D3D12, validación en GPU y DRED.

### 5.3 Hilos (por máquina)

| Hilo | Hace | Nunca hace |
|---|---|---|
| **E/S** (por fuente) | Lecturas asíncronas sin búfer del sistema, varios frames en vuelo por delante del reloj | Decodificar |
| **Decode** (por fuente; ProRes con hilos de FFmpeg y presupuesto **global**) | Escribe el frame **directamente en memoria de subida mapeada de forma permanente** (Snappy descomprime ahí; FFmpeg decodifica ahí con su callback de reserva de buffers). Cero copias en CPU | Tocar las colas de la GPU |
| **Copia** (envía a la cola de copia D3D12) | Sube a VRAM el frame N+1 mientras se compone el N | Esperar al render |
| **GPU / presentación** (uno, dueño de la cola gráfica) | Espera a la vez a los objetos de espera de todas las swapchains, elige frames por la hora del motor, compone y presenta la salida que toca | Esperar a una fuente: si falta un frame, repite el anterior y lo cuenta |
| **Audio** | Mezcla y remuestreo adaptativo hacia la hora del motor | Depender del vídeo |
| **Control** | Aplica el estado deseado en su hora | Bloquear a la GPU |

Prioridades: hilo de GPU y de audio en MMCSS («Playback» y «Pro Audio»); E/S y
decode por encima de lo normal.

### 5.4 Memoria

- **Todo se reserva al armar, nada durante la reproducción:** heaps de texturas,
  anillos de subida, buffers de lectura y de decode.
- Ninguna asignación de memoria en el camino caliente; verificado por test.
- Al cargar un show se calcula **el presupuesto de VRAM, bus y disco** y se
  compara con el perfil medido de la máquina (sección 7.3).

### 5.5 Topología de GPU por salida

- Al arrancar, el motor detecta **qué adaptador gobierna cada pantalla**.
- En portátiles con gráficos híbridos (NVIDIA Optimus), un puerto cableado a la
  iGPU obliga a una copia entre adaptadores en cada frame: se avisa y se
  recomienda el puerto correcto o el modo MUX (solo dGPU).
- Se detecta el funcionamiento con batería y la limitación térmica, y se
  muestran como alertas de salud.

## 6. Modelo neutro: grafo, transporte y parámetros

### 6.1 El grafo es un dato del cliente

El cliente construye el grafo; el motor lo evalúa en cada frame.

| Tipo de nodo | Ejemplos | Subproyecto |
|---|---|---|
| **Fuente** | Medio (HAP, ProRes, FFmpeg), imagen, color sólido, Spout de entrada, NDI de entrada | 1, 2, 6 |
| **Composición anidada** | Grupo de capas cuya salida es una textura (grupo de Resolume, *board* de Millumin) | 5 |
| **Mezcla** | Fundido, modos de fusión, *wipes* | 1 (fundido), 5 |
| **Efecto** | Corrección de color, LUT, *blur*, *key*, shaders ISF | 5 |
| **Mapping** | Corner-pin, malla de deformación, máscaras, *soft edge* | 6 |
| **Escena 3D** | Mallas, proyectores virtuales, calibración | 9 |
| **Salida** | Swapchain de pantalla, Spout de salida, NDI de salida, recorte de lienzo | 1, 6 |

Evaluación: nodos que pintan en destino; las texturas intermedias salen de un
pool reservado al armar.

### 6.2 Transporte por fuente

Reproducir, pausar, velocidad variable (incluida negativa, marcha atrás),
*scrub*, salto exacto a frame, *loops* (normal, *ping-pong*, N pasadas), trims
de entrada y salida, y velocidad ligada a un tempo (BPM). HAP y ProRes son
todo-intra: el acceso aleatorio es barato.

### 6.3 Parámetros direccionables y animables

- Todo parámetro del grafo tiene una dirección estable
  (p. ej. `/salida/1/capa/3/opacidad`).
- Se anima con **conductores** evaluados en cada frame sobre la hora del motor:
  rampa, curva de fotogramas clave, LFO, tempo.
- La línea de tiempo de Millumin/Watchout, el tempo de Resolume y el fundido de
  WinCue son el **mismo mecanismo**.

### 6.4 Color

- Espacio de trabajo **RGBA16F en luz lineal**: se linealiza al muestrear cada
  fuente y se codifica al presentar.
- Decodificación YUV→RGB en shader (BT.709/601/2020, rango limitado o completo,
  10/12 bits), con la matemática ya verificada en el motor C#.
- Salidas de 8 bits sRGB y de 10 bits; HDR como extensión.

### 6.5 Extensiones

- **Plugins nativos:** ABI en C, estable entre compiladores, versionada.
  Decisión abierta del subproyecto 5: si los plugins de terceros corren en el
  proceso del motor o en uno aparte.
- **Shaders:** formato **ISF** (lo usan Millumin y VDMX), traducido a HLSL. FFGL
  (el estándar de Resolume) es OpenGL y no se soporta directamente.

## 7. Medios

### 7.1 Servicio de medios

Proceso aparte, ni en el motor ni en la interfaz: la conversión consume CPU y GPU
durante minutos y no puede competir con el directo.

### 7.2 Ingesta y caché

| Entrada | Salida en caché |
|---|---|
| Vídeo (todo lo que lea FFmpeg) | **HAP Q Alpha** (HAP Q si no hay alfa), *chunked* para descompresión paralela. Codificador propio o sobre una biblioteca BC permisiva (FFmpeg no codifica HAP Q Alpha) |
| Su audio | WAV PCM sin comprimir aparte |
| Imagen fija | Textura BC7 (o sin comprimir si es pequeña), subida una sola vez al armar |
| Medio marcado «alta fidelidad» | Sin conversión: se usa el ProRes original |

- El original no se modifica nunca.
- Clave de caché = huella del contenido + versión del codificador + parámetros.
  Si cambia cualquiera, se regenera.
- Clúster: caché local por nodo. La distribución entre nodos se diseña en el
  subproyecto 8.

### 7.3 Prueba de la máquina

Una vez y al cambiar el hardware: lectura sostenida del disco de medios, ancho
de banda de subida a la GPU, descompresión HAP y decode ProRes por segundo, y
topología de salidas. Se guarda como perfil de la máquina.

### 7.4 «Preparar show»

1. Valida cada medio: variante soportada, resolución, fps frente al refresco
   de su salida y uso real del alfa.
2. **Simula la secuencia** que le describe el cliente para encontrar el **pico
   de carga simultánea**: los fundidos cruzados duplican capas.
3. Compara el pico con el perfil de la máquina (disco, bus, VRAM, decode) y
   devuelve verde, ámbar o rojo, con el momento y la causa. En rojo, propone
   soluciones concretas.

El motor ofrece el cálculo; cada producto decide cómo enseñarlo.

## 8. En la función

### 8.1 Ciclo de vida de una fuente

`declarada → ARMADA → en el aire → retirándose → liberada`

- **Armar** = fichero abierto, recursos tomados de los heaps, primeros frames
  decodificados y en VRAM, audio en búfer. **Lo pide el cliente** (WinCue: los
  siguientes cues; una app VJ: un banco de clips). El motor aplica el
  presupuesto de VRAM y avisa si no cabe.
- **Objetivo de latencia:** una fuente armada se ve en el primer vsync a partir
  de su hora de aplicación.
- **PANIC y STOP** son estados del propio motor (rampa a negro con hora).

### 8.2 Degradación (D5)

1. El audio nunca se degrada.
2. Ninguna salida espera a otra ni muestra contenido desfasado; a una capa sin
   frame se le repite el anterior.
3. Con sobrecarga sostenida, las pistas de menor prioridad bajan primero a media
   cadencia (solo se decodifican los frames que se van a ver). La prioridad la
   asigna el cliente; por defecto, el orden de apilado.
4. Aviso desde el primer frame perdido, con capa, salida y causa (disco, decode,
   subida o GPU).

### 8.3 Fallos y recuperación

| Fallo | Respuesta |
|---|---|
| Cae el proceso del motor | El motor publica el último frame de cada salida en una textura compartida que **el supervisor mantiene abierta**; la ventana de respaldo del supervisor lo congela en pantalla. Nunca se ve el escritorio. El supervisor relanza el motor, que retoma en el instante actual (< 1 s) |
| Dispositivo GPU eliminado (TDR) | Se recrea el dispositivo y se vuelve a armar el estado; la ventana del supervisor cubre la salida mientras tanto |
| Tirón de disco | Colchón de lectura por delante, dimensionado con el perfil de la máquina. Si se agota: repetir frame y contar |
| Desaparece un medio | Esa capa congela su último frame y avisa; el resto sigue |
| Se desconecta una pantalla | Las demás siguen; al reconectar se recrea su swapchain |
| Cae el cliente | El motor sigue con el último estado y sus rampas en curso |

### 8.4 Blindaje frente a Windows

Impedir suspensión y salvapantallas mientras hay show. Comprobación de «modo
show» en la preparación: notificaciones, Windows Update, plan de energía,
batería.

### 8.5 Salud y caja negra

- Telemetría por salida y por capa: frames repetidos y perdidos, colchón por
  delante (segundos), MB/s de disco, tiempo de GPU, temperatura y limitación
  térmica, desfase de reloj (clúster), peor frame.
- Semáforo verde, ámbar o rojo, con alarma desde el primer frame perdido.
- **Caja negra:** telemetría continua de los últimos minutos en memoria,
  guardada en disco ante cualquier anomalía.

## 9. Audio

- Vive en el proceso del motor, en C++.
- Salida por WASAPI (modo exclusivo opcional) y ASIO.
- **Esclavo de la hora del motor** mediante remuestreo adaptativo de alta
  calidad: inaudible para correcciones del orden de decenas de ppm.
- Audio por fuente con enrutado a dispositivos y canales de salida, ganancia,
  rampas y procesado (EQ, compresor: paridad con el rack actual de WinCue).
- Varispeed con corrección de tono para velocidades distintas de 1.
- Varios dispositivos quedan sincronizados entre sí al seguir todos la misma
  hora.
- Los medios de audio de la caché son PCM: no se decodifica audio comprimido
  durante la función.
- Las fuentes de **solo audio** (los cues de audio de WinCue, por ejemplo) también
  van por este motor: un solo reloj para todo lo que suena y se ve.

## 10. Pruebas

| Nivel | Qué | Cuándo |
|---|---|---|
| 1. Lógica pura | Reloj, transporte, selección de frame, conductores, presupuestos, convergencia del protocolo. Reloj virtual, sin GPU. Casos portados de los tests C# actuales (`HapMediaClock`, `FramePacer`, `FrameCadence`, `Ramp`, `ColorConvert`) | Cada commit |
| 2. Imagen sin GPU real | Adaptador WARP de D3D12 en CI; lectura por píxel contra valores de referencia del motor C# y de FFmpeg (HAP Q ≥ 51,6 dB, ProRes ≥ 48,0 dB PSNR, croma POINT). Capa de depuración, validación en GPU y DRED activos | Cada commit |
| 3. Fuzzing | libFuzzer + AddressSanitizer sobre demuxer MOV, parser HAP, protocolo y parser ISF | Pasada corta por commit, larga cada noche |
| 4. Inyección de fallos | Cada fila de la sección 8.3 con su criterio de aceptación; además, salto del reloj del sistema y pérdida de red del clúster | Cada commit (las automatizables) y antes de cada versión |
| 5. Temporización real | Medios de prueba con número de frame incrustado; estadísticas de presentación DXGI (vsyncs perdidos); con hardware, capturadora o cámara de alta velocidad para comprobar el mismo frame en todas las salidas | Cada hito |
| 6. Soak y matriz de hardware | Show del caso D1 durante 4 h (24 h en versiones grandes): 0 frames perdidos en la pista superior, sin tendencia en RAM, VRAM ni handles. Matriz: sobremesa RTX gaming, sobremesa RTX profesional (genlock), portátil gaming híbrido (puerto dGPU y puerto iGPU); AMD cuando se decida | Antes de cada versión |
| 7. Regresión de rendimiento | MB/s de descompresión, GB/s de subida, tiempo de GPU por frame, con umbrales | Cada commit |
| Verificación visual de Andrés | Lista de comprobación en pantalla | Fin de cada subproyecto |

## 11. Subproyectos y orden

Cada subproyecto tiene su propio diseño, plan e implementación. Los
subproyectos 1-3 son el núcleo y van primero; los demás se enchufan a él.
Las interfaces de 5-9 se definen en el diseño del núcleo para no tener que
rehacerlo.

| # | Subproyecto | Criterio de salida |
|---|---|---|
| 0 | **Pruebas de concepto** (sección 14) | Cada riesgo con veredicto medido |
| 1 | **Núcleo:** abstracción D3D12, hora del motor, una salida, fuente HAP, grafo mínimo (fuente + fundido + salida) | HAP 4K60 a pantalla completa, frame exacto, 0 drops en 4 h |
| 2 | **Medios:** HAP Q Alpha, ProRes, FFmpeg `d3d12va`, lectura por delante, armado, varias salidas, transporte completo | Caso D1 en verde en la matriz de hardware |
| 3 | **Servicio y protocolo:** proceso aparte, supervisor con ventana de respaldo, API pública, SDK C++ y C#, adaptador OSC, recuperación | Matar el motor en pleno show: nunca se ve el escritorio, retoma en < 1 s |
| 4 | **Audio en el motor** + **WinCue como primer cliente** | WinCue en producción sobre el motor nuevo; ruta actual como respaldo durante una versión |
| 5 | **Grafo completo:** composiciones anidadas, modos de fusión, efectos, ISF, conductores de parámetros, ABI de plugins | Demo tipo VJ |
| 6 | **Mapping + Spout y NDI** | Demo tipo Millumin |
| 7 | **Servicio de medios:** ingesta, conversión, caché, prueba de máquina, preparar show | Un show en rojo pasa a verde con las propuestas automáticas |
| 8 | **Clúster:** reloj de red, varios nodos, distribución de caché, genlock opcional | Dos PCs sincronizados durante un soak |
| 9 | **Escena 3D:** mallas, proyectores, calibración | Demo tipo Pixera |

## 12. Relación con el código actual

- **La ruta actual de WinCue sigue en producción** hasta que el subproyecto 4
  cumpla su criterio de salida, y se mantiene una versión más como respaldo.
- **Lo hecho en C# pasa a ser la referencia, no código a portar línea a línea:**
  los decodificadores HAP y ProRes, el demuxer MOV, los relojes y la matemática de
  color aportan sus 145 tests, valores por píxel y umbrales de PSNR, contra los
  que se valida el motor en C++.
- **Lecciones que se conservan como requisitos:**
  - el `codec_tag` del `stsd` es obligatorio para ProRes (sin él se pierden
    2 bits en 4444);
  - *slice threading* y nunca *frame threading* en ProRes;
  - `Reset()` del decodificador en cada salto no secuencial;
  - el color manda desde el bitstream;
  - croma POINT para igualar a swscale;
  - validar el contenedor antes de alimentar un decodificador nativo;
  - padding obligatorio en los paquetes de FFmpeg;
  - lista cerrada de formatos de píxel;
  - recursos persistentes y no creados por cue (auditoría 05).
- **Deuda previa:** la verificación visual de F0 pendiente en la hoja de ruta se
  cierra antes de empezar. Es la línea base contra la que comparar.

## 13. Licencias y dependencias

- **Distribución prevista cerrada** (según `docs/legal`): solo dependencias
  permisivas o LGPL con enlace dinámico.
- **FFmpeg:** build LGPL, DLL dinámicas, con el guardián de publicación ya
  existente.
- **Snappy y GoogleTest:** licencias permisivas. La biblioteca BC para el
  codificador HAP Q Alpha y la de traducción de ISF se eligen con el mismo
  criterio. Todas se verifican en el subproyecto 0 o 1.
- **SDK NDI:** licencia propia de NDI, con requisitos de atribución; se revisa en
  el subproyecto 6.
- **Código de OBS:** `libobs` es GPL y no se copia. `shared/media-playback`
  es ISC y se puede usar como referencia.
- **Ingeniería inversa de productos comerciales:** fuera del proceso. Solo
  medición de caja negra (ver la comparativa, sección 7).

## 14. Riesgos y pruebas de concepto (subproyecto 0)

| Riesgo | Prueba de concepto | Si falla |
|---|---|---|
| Genlock NVIDIA (*swap groups* de NVAPI) con swapchains D3D12 | Dos salidas en RTX profesional con tarjeta Sync | Presentar esas salidas con una swapchain D3D11 compartiendo texturas con D3D12, o dejar el genlock fuera de la versión 1 |
| Madurez de FFmpeg `d3d12va` | H.264 y HEVC 4K60 sin copias a RAM, soak de 1 h | Decode por hardware con copia, o D3D11VA + compartición con D3D12 |
| La textura compartida sobrevive a la muerte del motor | Matar el proceso y que el supervisor siga mostrando el último frame | Ventana del supervisor en negro (sin el último frame) |
| Valor de DirectStorage para HAP | Comparar con lectura asíncrona sin búfer | Se descarta; lectura asíncrona normal |
| Codificador HAP Q Alpha: calidad y velocidad | PSNR frente al original y minutos por minuto de 4K | Ajustar la biblioteca o codificar en GPU |
| Detección de topología en portátiles híbridos | Portátil gaming con puertos en iGPU y dGPU | Aviso manual en la documentación |
| Remuestreo adaptativo inaudible | Prueba de escucha y medida de THD+N con corrección de 50 ppm | Cambiar el algoritmo de remuestreo |

## 15. Fuera de alcance de este documento

- El diseño detallado de cada subproyecto (cada uno tendrá el suyo).
- Capturadoras SDI/HDMI y salidas SDI (fuentes y salidas futuras del grafo).
- Nodos de salida en Linux (descartado por ahora; la capa de abstracción no lo
  impide).
- La interfaz de cualquier producto que use el motor.
