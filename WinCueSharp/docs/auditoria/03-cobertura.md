# Auditoría WinCue — 03 · Cobertura de tests

**Fecha:** 2026-07-18 · **Rama:** `pruebas-v2` · **Suite:** 255 tests en verde.
**Herramientas:** Coverlet (collector, ya estaba en los 4 proyectos de test) + ReportGenerator 5.5.10 como herramienta local (`.config/dotnet-tools.json`).
**Script:** `scripts/coverage.ps1` → tests + informe HTML en `artifacts/coverage/index.html` (artifacts/ ignorado por git).

---

## 1. Cobertura global

| Métrica | Valor |
|---|---:|
| **Líneas** | **73,2 %** (3 604 / 4 923) |
| **Ramas** | **65,9 %** (1 385 / 2 099) |
| Métodos (tocados) | 82,1 % |
| Métodos (completos) | 65,7 % |

> ⚠️ **La cifra real es peor de lo que parece:** `WinCue.App` (8 809 líneas de producto, el 53 %) **no aparece en el informe** porque ningún test lo carga — cobertura 0 % e invisible para Coverlet. Contando App, la cobertura real del producto ronda el **~35 % de líneas** (estimación: App tendría ~5 000 líneas cubribles). El 73,2 % es "cobertura de lo testeable actual", no del producto.

## 2. Desglose por proyecto

| Proyecto | Líneas | Comentario |
|---|---:|---|
| WinCue.Core | **95,2 %** | Excelente — el modelo/JSON está blindado |
| WinCue.Controller | **83,0 %** | Bien; huecos concentrados (ver §4) |
| WinCue.Audio | **75,4 %** | El DSP puro muy bien (EqChain 97,5 %); el motor real a 61 % |
| WinCue.Video | **47,8 %** | El interop GPU (GlContext/MpvGpuRenderer/D3D9) a 0 % |
| WinCue.App | **0 %** (no instrumentado) | Sin ningún test — ya señalado en 00-inventario |

### Por clase (extracto significativo)

| 100 % o casi | 80-95 % | Bajo (<75 %) | Cero |
|---|---|---|---|
| CanvasLayout 100 · Cue 99,2 · CueModel 98,7 · EqChain 97,5 · CueListEditor 94,4 | MpvNative 91,3 · UndoService 90,5 · ShowFileService 89,8 · AudioDecoder 89,7 · RemoteRouter 89 · SettingsManager 89 · MainController 85,1 · CueFields 84,9 · MpvPlayer 82,1 | CueSampleProvider 73,8 · MediaValidation 71,4 · AudioEngine 61,4 · MediaImporter 60,1 · VideoEngine 56,5 · OscPacket 45,9 | GlContext · MpvGpuRenderer · D3D9Minimal · MpvRenderNative · TimerScheduler |

## 3. Top 20 clases por líneas SIN cubrir

🔴 = en la **ruta crítica de un show en directo** (arranque/parada de cues, transiciones de vídeo, salida de audio, recuperación de errores). 🟡 = crítica pero atada a hardware/GPU (no unit-testeable; cubrir con la sesión de estrés). ⚪ = no crítica en directo.

| # | Clase | Sin cubrir | % líneas | Crítica | Qué está sin cubrir (métodos clave) |
|---:|---|---:|---:|:---:|---|
| 1 | **VideoEngine** | 267 | 56,5 | 🔴 | `PrepareStandby`/`BuildStandby`/`PreloadNext` (¡el GO instantáneo!) 0 % · `PauseCue`/`ResumeCue` 0 % · `ScheduleHandoverKill`/`ParamsEquivalent` (crossfades) 0 % · `StartPlayback` 49 % · `TerminatePlayer` 54 % |
| 2 | GlContext | 179 | 0 | 🟡 | Todo (driver GL real) |
| 3 | **MainController** | 157 | 85,1 | 🔴 | `OnVideoStateChanged` **0 %** (fin natural de vídeo → encadenado) · `OnVideoError` **0 %** (recuperación) · `OnStopSelected` 0 % · `Shutdown` 0 % · `ExecScript` 0 % · `BeginVideoFadeOut` 48 % · `ExecStart` 50 % · `VideoEndCheck` 56 % |
| 4 | **AudioEngine** | 148 | 61,4 | 🔴 | `Fade` **0 %** (¡el cue FADE sobre audio!) · `PauseCue`/`ResumeCue` 0 % · `SetVolume`/`SetEq`/`SetMasterVolume` 0 % (edición en vivo) · `Preload` 0 % · `CreateOutput` 23 % (fallbacks de dispositivo) |
| 5 | MpvGpuRenderer | 143 | 0 | 🟡 | Todo (hilo de render GPU real) |
| 6 | **CueSampleProvider** | 73 | 73,8 | 🔴 | `SetPaused` 0 % (pausa por cue) · `SetEq` 0 % · `GetSpectrum`/`FeedSpectrum` 0 % · `GetGainReductionDb` 0 % — **todo DSP puro, testeable sin hardware** |
| 7 | **OscPacket** | 66 | 45,9 | 🔴 | `Build` **0 %** (feedback a Companion — el método del fix CA2014 de la fase 1) · ramas de `Parse`/`ParseElement` (~65 %) |
| 8 | D3D9Minimal | 46 | 0 | 🟡 | Todo (COM D3D9 real) |
| 9 | MediaImporter | 43 | 60,1 | ⚪ | `MeasureLufs` 0 % (importación, no directo; requiere ffmpeg) |
| 10 | MpvRenderNative | 26 | 0 | 🟡 | P/Invoke render API |
| 11 | MpvPlayer | 20 | 82,1 | 🔴 | `Terminate` 0 %, ctor 63 % (rutas de error) — testeable: los Video.Tests ya cargan libmpv real |
| 12 | MpvNative | 19 | 91,3 | ⚪ | Ramas del resolver |
| 13 | **TimerScheduler** | 18 | 0 | 🔴 | **Todo.** Es el scheduler REAL del directo (los tests usan reloj virtual): toda la temporización de un show pasa por aquí y nadie lo ha ejecutado en tests |
| 14 | RemoteRouter | 18 | 89,0 | ⚪ | Ramas menores |
| 15 | ShowFileService | 16 | 89,8 | ⚪ | `RotateBackups` (deuda anotada en auditoría 02) |
| 16 | AudioDecoder | 15 | 89,7 | ⚪ | Ramas de fallback FFmpeg |
| 17 | SettingsManager | 15 | 89,0 | ⚪ | Ramas de error de E/S |
| 18 | CueListEditor | 15 | 94,4 | ⚪ | Bordes de OnMoveCue |
| 19 | CueFields | 8 | 84,9 | ⚪ | Campos raros |
| 20 | UndoService | 5 | 90,5 | ⚪ | `MaxUndo` + snapshot corrupto (deuda anotada) |

## 4. Lectura de la ruta crítica (lo que va a fallar en un directo)

Ordenado por peligro real = (probabilidad de ejecutarse en show) × (no probado):

1. **Fin natural de vídeo y errores de vídeo (`MainController.OnVideoStateChanged`/`OnVideoError`, 0 %)** — cada vídeo que termina solo pasa por aquí; el encadenado auto-follow tras vídeo depende de esto. Testeable HOY con los fakes existentes.
2. **La maquinaria de standby del GO instantáneo (`VideoEngine.PrepareStandby/BuildStandby`, 0 %)** — la feature estrella reciente, sin ningún test del motor real. (Los `VideoStandbyTests` prueban al CONTROLADOR con motor falso — el motor real nunca se ejercita.)
3. **`AudioEngine.Fade` (0 %)** — un cue FADE apuntando a audio en directo usa una ruta jamás ejecutada en tests.
4. **Pausa por cue en ambos motores reales (0 %)** — `OnPauseCueToggle` está probado contra fakes; los `PauseCue/ResumeCue` reales, no.
5. **`OscPacket.Build` (0 %)** — el feedback OSC a Companion. *Corrección de la fase 1: el commit del fix CA2014 decía "RemoteControlTests cubre Build" — era falso; cubren `Parse`.* El método del fix nunca se ha ejecutado en un test.
6. **`TimerScheduler` real (0 %)** — el reloj virtual de los tests es magnífico para la lógica, pero el scheduler de producción jamás ha corrido en un test.
7. **Fades de vídeo corregidos por deriva (`BeginVideoFadeOut` 48 %, `VideoEndCheck` 56 %)** — las ramas de reprogramación (la parte "difícil") son justo las no cubiertas.
8. El interop GPU (GlContext/MpvGpuRenderer/D3D9, 0 %) es crítico pero **no unit-testeable**: su verificación honesta es la **sesión de estrés del compositor** ya pendiente en la memoria del proyecto.

## 5. Plan de tests propuesto (orden de escritura)

| Tanda | Objetivo | Vía |
|---|---|---|
| 1 | `OnVideoStateChanged`/`OnVideoError` + `OnStopSelected`/`Shutdown` | Fakes existentes (Controller.Tests) |
| 2 | `CueSampleProvider`: `SetPaused`, `SetEq` en vivo, `GetSpectrum`, `GetGainReductionDb` | DSP puro (Audio.Tests) |
| 3 | `OscPacket.Build` (round-trip Build→Parse) + ramas de `Parse` | Puro (Controller.Tests) |
| 4 | `BeginVideoFadeOut`/`VideoEndCheck` ramas de deriva + `ExecStart` bordes | Fakes con `GetElapsed` controlado |
| 5 | `TimerScheduler` real (test corto de tiempo real) + `AudioEngine.Fade`/pausa vía provider | Audio.Tests / Controller.Tests |

Cobertura objetivo razonable tras las tandas: Controller ≥ 90 %, Audio ≥ 82 %, OscPacket ≥ 85 %. El bloque GPU queda para la sesión de estrés; `WinCue.App` queda como riesgo aceptado y documentado (00-inventario) hasta que se decida invertir en tests de UI.

## 6. Hallazgos aparecidos AL escribir los tests

- **Escala absoluta del RTA (posible defecto cosmético, producción NO tocada):** al testear `CueSampleProvider.GetSpectrum` se vio que un seno de amplitud 0,5 reporta ~**−73 dBFS** en su banda (esperable ≈ −6). La normalización de `FeedSpectrum` divide dos veces por N (la FFT de NAudio con `forward=true` ya normaliza; luego se aplica `4·√peak/N` otra vez). El RTA *funciona* en pantalla porque el rango se estira visualmente y hay ganancia de vista (+18 dB), pero los dB mostrados no son dBFS reales. El test verifica lo funcional (el tono destaca sobre el suelo y cae en la banda correcta) y este apunte queda para decidir si se corrige la escala.
