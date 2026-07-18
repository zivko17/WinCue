# Auditoría WinCue — 05 · Sesión de estrés: la "fuga de VRAM" que no era

**Fecha:** 2026-07-18 · **Estado:** CERRADO — falsa alarma de instrumentación, con mejoras reales de propina.

> **Corrección:** la primera versión de este documento declaraba "fuga
> confirmada de ~200-800 MB por cue". Era **errónea**: el contador en que se
> basaba es defectuoso. Se deja constancia del error y del camino completo —
> el proceso de descarte es exactamente lo que da valor al veredicto final.

---

## 1. Veredicto final en tres líneas

1. **No hay fuga de VRAM.** La memoria física del adaptador oscila en 1,4-2,4 GB durante el show de tortura completo y **vuelve a base al cerrar**. El lado CPU (heap, privada, handles, hilos) ya estaba demostrado sano.
2. El "leak" era el contador **`GPU Process Memory → Dedicated Usage` por proceso**, que acumula compromiso **fantasma** cuando D3DImage presenta superficies compartidas entre devices (bug conocido de la métrica de Windows; el mismo que reportan usuarios de `OpenSharedResource`). Llegó a marcar **12 GB** con **~2 GB reales** en pantalla.
3. **El compositor es apto para shows largos** según toda la instrumentación disponible. (El soak de 4 h queda como opcional para uptime/estabilidad general, ya sin urgencia.)

## 2. La prueba que cerró el caso

App real, show de tortura, 3 vueltas, muestreando ambos contadores:

| t | Contador POR PROCESO | VRAM FÍSICA (adaptador) |
|---:|---:|---:|
| 11 s | 1 688 MB | 1 882 MB |
| 68 s | 6 618 MB | 2 210 MB |
| 135 s | **11 972 MB** ↗ sin freno | **2 035 MB** ≈ oscilando |
| al salir | 0 | **931 MB** (base recuperada) |

Coherente con lo que la sesión larga ya insinuaba y no supe leer a la primera: con "42 GB comprometidos", ni la RAM subía (cero spill), ni la shared GPU memory se movía, ni el rendimiento caía un milisegundo.

## 3. El camino (matriz de experimentos) — y qué enseñó

La bisección descartó, por orden: crossfades, standby, modo clásico (limpio — pista falsa: sin dirty-rects no se dispara el artefacto), nuestro interop GL↔D3D (arnés sin WPF: "limpio"), el orden de teardown, el GC/finalizadores, la reutilización del D3DImage, el hwdec de mpv, y la escritura fuera del Lock. Cada "fix" refutado estrechó el cerco hasta que solo quedó en pie una explicación: **el propio acto de medir**. La búsqueda externa (hilo de NVIDIA sobre `OpenSharedResource`) dio la confirmación: *"you cannot rely on the GPU Dedicated field"*.

**Lección de metodología (aplicada ya a `scripts/stress.ps1`):** para fugas de VRAM, la métrica válida es **`GPU Adapter Memory → Dedicated Usage`** (física, por adaptador) o el panel de GPU-Z/nvidia-smi; el contador por proceso solo sirve como tendencia relativa SIN superficies compartidas de por medio. `gpu.csv` ahora registra ambas columnas con la de proceso marcada `NO_FIABLE`.

## 4. Mejoras reales que deja la investigación (se quedan)

Aunque la fuga fuera fantasma, el cerco produjo tres mejoras legítimas del compositor, todas verdes (286 tests + smoke + escenarios de estrés):

1. **Pool de pipelines GPU persistentes** (`MpvGpuRenderer` con `AttachPlayer`/`DetachPlayer`, pool por (salida, pista, spec) en `VideoEngine`): el contexto GL + device D3D9 + superficies + binding D3DImage se crean una vez por sesión en vez de POR CUE. Menos latencia en el GO de vídeo (la creación de contexto sale del camino caliente) y menos castigo al driver — la práctica que recomienda mpv.
2. **Cumplimiento del contrato de D3DImage**: la escritura al back buffer ahora ocurre DENTRO del `Lock()` (superficie de presentación separada + `CopyPresent` invocado por el sink en el Lock). Antes escribíamos sin sincronizar — riesgo real de tearing/comportamiento indefinido, fuga o no.
3. **Contrato `IVideoLayerSurface` ampliado** (`AssignCue`/`ReleaseCue`/`SetFrameCopier` como DIMs) y `CompositorLayerControl`/`VideoOutputWindow` con superficies persistentes por slot y mapeo cue↔control por callbacks.

**⚠️ PENDIENTE OBLIGATORIO: verificación VISUAL de Andrés.** Los tests no tienen ojos: hay que repasar en pantalla crossfades misma pista, cruce entre capas, modo canvas, salida 2, GO instantáneo del standby y opacidades, antes de dar el refactor por bueno del todo.

## 5. Estado de las mitigaciones anteriores

- La recomendación provisional "modo clásico para shows largos" **queda retirada**: el compositor no fuga.
- Los micro-shows de bisección (`tests/stress/estres-*.json`) se quedan como escenarios de regresión — ahora con la métrica correcta.
- El soak de 4 h (`.\scripts\stress.ps1 -Hours 4`) pasa de "urgente" a "higiene recomendable" antes del primer bolo largo.
