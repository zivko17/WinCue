# mpv-d3d11-lab — ¿es viable un compositor D3D11 propio?

Laboratorio paralelo (misma convención que `tools/hap-lab`: **no** forma parte
de la solución `WinCueSharp.sln`).

## La pregunta

Hoy cada capa de vídeo acaba en **su propia** superficie D3D9Ex dentro de un
`D3DImage`, y la mezcla la hace el **DWM** animando `Opacity` de WPF
(`VideoOutputWindow.SetCueOpacity`). Eso impide transiciones por shader, no
tiene un tick de salida común y **no deja ninguna textura final** de la que
sacar multiviewer, grabación o NDI.

Un compositor propio exige que **todas las fuentes entreguen textura en el
MISMO device D3D11**. El motor HAP ya renderiza a D3D11, así que el punto
dudoso es el otro:

> **¿Puede mpv —que solo renderiza por OpenGL— dejar su frame en una textura
> D3D11, y podemos mezclar esa textura con otras en un único paso?**

El interop actual (`src/WinCue.Video/GlDxInterop.cs`) va **GL → D3D9Ex**. La
extensión `WGL_NV_DX_interop` v1 solo garantiza objetos D3D9; el soporte de
D3D10/D3D11 llega con **`WGL_NV_DX_interop2`**, que no está garantizada en
todos los drivers. De ahí el lab.

## Cómo usarlo

```
dotnet run --project tools/mpv-d3d11-lab [ruta-al-video]
```

Sin argumento usa `tests/hap-test/demo-h264-1080p30-noaudio.mp4`. Localiza
`libmpv-2.dll` en `publish/` (o `WINCUE_LIBMPV`). Deja los volcados de
verificación en `tools/mpv-d3d11-lab/out/`. Devuelve 0 si todo está en verde.

## Resultado

Máquina: **NVIDIA GeForce RTX 5070 Ti**, driver **595.79**, GL 4.6, Windows 11.
Vídeo: `demo-h264-1080p30-noaudio.mp4` (1920×1080).

| | Comprobación | Resultado |
|---|---|---|
| T1 | Device D3D11 en el adaptador HighPerformance | OK (FL 11_1) |
| T2 | Contexto OpenGL + `get_proc_address` del core GL 1.1 | OK |
| T3 | `WGL_NV_DX_interop` / `interop2` presentes | OK / **OK** |
| T4 | `wglDXOpenDeviceNV` contra un `ID3D11Device` | **OK** |
| T5 | `ID3D11Texture2D` registrado como `GL_TEXTURE_2D` + FBO completo | OK |
| T6 | GL escribe color conocido → D3D11 lo lee por píxel | OK, sin permuta de canales |
| T7 | mpv renderiza un vídeo real dentro de la textura D3D11 | OK (luma 127,9) |
| T8 | Compositor: 2 capas mezcladas en UNA render target | OK (±1 LSB) |

**Veredicto: VERDE.** La ruta del compositor unificado es viable. Los volcados
`out/t7-*.bmp` (barras de mpv en la textura D3D11) y `out/t8-*.bmp` (esas barras
con una capa magenta al 50 %) lo confirman también a ojo.

## Hallazgos

**1. El registro NO es como en D3D9.** Con D3D9 se registra la superficie como
`GL_RENDERBUFFER`; `WGL_NV_DX_interop2` **solo admite `GL_TEXTURE_2D`** para un
`ID3D11Texture2D`. El FBO se ata con `glFramebufferTexture2D`, no con
`glFramebufferRenderbuffer`. Es un cambio real en `AddView`.

**2. La variante que funcionó** fue crear la textura con
`ResourceOptionFlags.Shared`, sacar el `SharedHandle` por `IDXGIResource` y
declararlo con `wglDXSetResourceShareHandleNV` antes de registrar. La escalera
del lab para en el primer éxito: **no sabemos si la variante sin `Shared`
también vale** en este driver. Irrelevante para seguir, pero conviene saberlo
antes de fijar la implementación.

**3. Orden de canales correcto.** Textura `B8G8R8A8_UNorm` + escritura GL RGBA
→ leída desde D3D11 sale exacta (`R=20 G=40 B=80`). No hace falta compensar en
el shader. Con `flip_y=1` en el render de mpv la orientación es la correcta
para D3D (verificado a ojo: el timecode del clip sale abajo-izquierda, sin
espejo).

**4. Fragilidad latente en `GlDxInterop.cs` (producción).** El lab falló al
principio con `mpv_render_context_create` → *"not supported"*. Causa: el
handle del módulo se toma en un **inicializador de campo**

```csharp
private readonly nint _opengl32 = GetModuleHandleW("opengl32.dll");
```

que corre **antes** del cuerpo del constructor — es decir, antes de que
`wglCreateContext` cargue `opengl32.dll` en el proceso. Devuelve 0, y con él
se quedan sin resolver **todas las funciones core de GL 1.1**, que
`wglGetProcAddress` no devuelve nunca por diseño. mpv pide `glGetString` lo
primero y rechaza el contexto.

En producción **hoy no se manifiesta**, porque `libmpv-2.dll` referencia
`opengl32.dll` y se carga antes de que el pipeline construya su `GlContext`:
para cuando corre el inicializador, el módulo ya está en el proceso. Pero eso
es **una casualidad de orden de carga**, no una garantía — cualquier cambio que
construya el pipeline antes de tocar libmpv (o un modo degradado sin libmpv)
lo despierta. Arreglo de una línea: resolver el handle *después* de tener
contexto, con `LoadLibraryW` de respaldo (así lo hace el lab).

**5. La mezcla propia da la aritmética exacta.** Opacidad por *blend factor*
(`src*f + dst*(1-f)`), sin constant buffer: mismo resultado que hace el DWM
hoy, pero bajo nuestro control y con la RT final disponible.

**6. Máquinas híbridas: OpenGL y D3D11 pueden caer en GPUs distintas.**
Al añadir el barrido de todos los adaptadores (2026-07-26) salió solo: con
device D3D11 en la integrada AMD, el contexto OpenGL seguía viniendo de la
NVIDIA, y el interop **no puede** en esa combinación. No es un fallo de
driver: Windows decide qué GPU le da a cada proceso, y el interop exige que
ambas APIs estén en la misma.

Importa para producción, no solo para el lab: `GpuDevice` pide siempre el
adaptador de alto rendimiento, pero el contexto GL de `MpvSource` viene de
donde Windows quiera. En un portátil configurado en ahorro de energía, eso es
device en la dedicada y GL en la integrada → `MpvSource` no arranca. El
mensaje de error ya distingue las dos causas; **el emparejado automático está
pendiente** (elegir el adaptador D3D11 que case con el del contexto GL).

Para forzarlo a mano: Configuración → Sistema → Pantalla → Gráficos → agregar
el ejecutable → Opciones → *Ahorro de energía* (integrada) o *Alto
rendimiento* (dedicada).

## Cómo llevarlo a otra máquina

```
powershell -ExecutionPolicy Bypass -File empaquetar.ps1
```

Deja `mpv-d3d11-lab.zip` (~80 MB) con el ejecutable **autocontenido** (no hace
falta .NET instalado), `libmpv-2.dll`, un clip de prueba y un LEEME. En el
equipo de destino se descomprime y se lanza el `.exe`: prueba **todos** los
adaptadores, uno tras otro, y escupe un resumen.

```
mpv-d3d11-lab.exe                  # todos los adaptadores
mpv-d3d11-lab.exe --adapter=low    # solo la integrada
mpv-d3d11-lab.exe --adapter=high   # solo la dedicada
mpv-d3d11-lab.exe --adapter=1      # por índice
```

## Lo que este lab NO prueba

Conviene decirlo claro antes de que alguien lo lea como "está resuelto":

- **Un solo driver.** NVIDIA moderno. **Intel iGPU y AMD sin probar** — y es
  justo ahí donde `interop2` ha dado guerra históricamente. Con solo-iGPU es
  obligatorio repetirlo antes de comprometer la arquitectura.
- **Un frame, no un régimen permanente.** No hay soak. El fallo de la
  auditoría 05 (fuga de VRAM del interop) apareció **con el tiempo**, no en el
  primer frame. Esta prueba no dice nada sobre eso.
- **Una sola fuente mpv.** Sin varias sesiones simultáneas, sin attach/detach
  repetido, sin pool de slots.
- **Sin presentación real.** No hay swapchain ni salida a pantalla a vsync: se
  compone a una RT y se lee de vuelta. El tick de salida sigue sin probarse.
- **El motor HAP no se enchufó.** Su textura ya vive en D3D11, así que se
  asume trivial — pero *asumido* no es *probado*.
- **Sin métricas.** Ni latencia ni coste por frame.

## Siguiente paso

Con el go/no-go en verde, lo que decide de verdad la arquitectura es lo que
queda arriba: **repetir T1–T8 en una máquina solo-iGPU** (Intel) y **añadir un
soak** de attach/detach + render sostenido midiendo VRAM. Si esas dos pasan, el
compositor deja de ser una apuesta.
