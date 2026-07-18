# Auditoría WinCue — 05 · Sesión de estrés: LA FUGA DE VRAM del compositor

**Fecha:** 2026-07-18 · **Estado:** fuga confirmada, causa raíz aislada por experimento, mitigación disponible, fix real acotado (pendiente de decisión).

---

## 1. Veredicto en una línea

**El compositor GPU fuga ~200-800 MB de VRAM por CADA cue de vídeo reproducido y nunca los devuelve.** El lado CPU (heap .NET, memoria nativa, handles, hilos) está **sano**. El modo clásico (HWND) está **limpio**. Un show largo con compositor termina saturando la GPU; hoy el compositor **no es apto para shows largos**.

## 2. Datos de la sesión (artifacts/stress/20260718-134254/)

12 vueltas del show de tortura, 150 cues, cerrada anticipadamente al confirmar la fuga:

| Métrica | Comportamiento |
|---|---|
| Heap gestionado | ✅ 11-23 MB, estable (los gen-2 devuelven al suelo) |
| Memoria privada | ✅ diente de sierra 0,7-2,1 GB, vuelve a bajar |
| Handles | ✅ oscila 0,9-2,4k, vuelve a bajar |
| Hilos | ✅ planos (312→313 en 65 s) — players y renderers SÍ mueren |
| **VRAM dedicada (committed)** | 🔴 **5,5 GB (min 1) → 42,9 GB (min 9), ~4,5 GB/min, jamás baja** (ni en STOP ALL) |

Los fixes de la fase 1 (wait handles por cue, etc.) quedan **validados**: el lado CPU no fuga.

## 3. La matriz de experimentos (bisección completa)

Micro-shows en `tests/stress/` + arnés standalone (scratchpad) que replica el pipeline sin WPF y añade el sink D3DImage por etapas:

| # | Experimento | Resultado |
|---|---|---|
| A | App · compositor · show simple (1 vídeo/vuelta, sin crossfades) | 🔴 **+340 MB/ciclo, lineal** |
| C | App · **modo clásico** (compositor OFF), mismo show | ✅ **limpio** (217 MB planos, baja en huecos) |
| D | App · compositor · vídeo disparado por START (sin standby) | 🔴 fuga igual → standby exonerado |
| S2 | Arnés: MpvPlayer + MpvGpuRenderer, **sin WPF** | ✅ limpio (427 activo / 7 reposo) |
| S5 | S2 + sink **D3DImage** (réplica exacta de CompositorLayerControl) | 🔴 ~800 MB/ciclo |
| S6 | S5 + soltar back buffer ANTES del teardown del renderer + flush | 🔴 refutado |
| S7 | S5 + GC.Collect + WaitForPendingFinalizers por ciclo | 🔴 refutado |
| S8 | S5 con UN único D3DImage reutilizado (swap de back buffer) | 🔴 refutado |
| **S9** | S5 con SetBackBuffer pero **SIN AddDirtyRect** (ningún frame presentado) | ✅ **LIMPIO** |

## 4. Causa raíz

La combinación letal es: **(1)** superficie D3D9Ex compartida registrada en el interop `WGL_NV_DX` por la que mpv renderiza, **(2)** WPF presentándola vía `D3DImage.AddDirtyRect` (su hilo de render copia desde nuestra superficie), y **(3)** el **desmontaje por-cue** del pipeline (unregister/close/delete del interop + device).

Con tráfico de dirty-rects en marcha, el desmontaje del interop no libera de verdad: el driver retiene el contexto GL completo del ciclo (FBOs, renderbuffers, pool de hwdec, shaders — el "peso" de un player). Las llamadas `wglDX*` devuelven `bool` que el código no comprueba (anotado ya en la auditoría 01 como CA1806 genérico): el fallo es silencioso. Sin dirty-rects (S9) o sin WPF (S2), el mismo desmontaje libera perfectamente.

**No existe el parche mínimo.** Se refutaron empíricamente los tres candidatos (orden de teardown, dependencia del GC, pooling del D3DImage). No es un `Dispose` olvidado: es el patrón arquitectónico "pipeline GPU efímero por cue + presentación WPF" el que está podrido en su interacción con el driver.

## 5. Mitigación INMEDIATA (disponible hoy)

**Para shows largos: modo clásico (Settings → Vídeo → compositor OFF).** Está demostrado limpio (escenario C). Se pierde: opacidad entre capas/crossfades del compositor y modo canvas. Para bolos de horas, es el modo seguro hasta el fix.

Regla operativa provisional con compositor: presupuesto ≈ `VRAM_libre / 350 MB` cues de vídeo por sesión (en una GPU de 8 GB: ~15-20 cues de vídeo antes de degradar) — suficiente para probar, no para operar.

## 6. El fix real (propuesta, pendiente de GO)

**Pool de pipelines GPU persistentes** por (salida, pista, paridad A/B): el contexto GL + device D3D9 + superficie compartida + binding D3DImage se crean UNA vez y viven toda la sesión; por cue solo se crea/destruye el core de mpv y su `mpv_render_context` DENTRO del pipeline persistente. Sin desmontaje de interop por cue → nada que envenenar; VRAM acotada al tamaño del pool. (La paridad A/B cubre el crossfade en la misma pista, que necesita dos superficies simultáneas.)

- Alcance: refactor de `MpvGpuRenderer` (separar pipeline persistente de sesión por cue), ciclo de vida en `VideoEngine` (play/standby/handover), binding único en `CompositorLayerControl`.
- Estimación: media jornada larga de trabajo cuidadoso + re-validación con esta misma matriz de experimentos (los micro-shows de `tests/stress/` son ahora los tests de regresión de VRAM).
- Verificación de éxito: escenario A con curva plana estilo S9/C.

## 7. Extras del incidente

- `stress.ps1` corregido dos veces sobre la marcha: BOM UTF-8 (PowerShell 5.1 parseaba mal el UTF-8 sin BOM — el orquestador moría al minuto) y coma decimal regional en `gpu.csv` + cierre sin matar `dotnet-counters` antes de su volcado.
- El muestreo de VRAM del proceso quedó automatizado en `stress.ps1` (gpu.csv) — el nivel 1 de la auditoría 04 ya no es manual.
- Micro-shows de bisección versionados en `tests/stress/`: `estres-simple.json`, `estres-crossfade.json`, `estres-sinstandby.json`.
