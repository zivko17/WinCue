# Auditoría WinCue — 00 · Inventario y línea base

**Fecha:** 2026-07-18 · **Rama:** `pruebas-v2` · **Modo:** diagnóstico, sin modificar código.
**Alcance:** inventario de la solución, dependencias, tests y configuración de análisis estático.

> Línea base establecida en esta pasada: `dotnet build` limpio OK · `dotnet test WinCueSharp.sln`
> = **255 pruebas, 0 fallos, 0 omitidas**. Cualquier cambio futuro debe mantener este estado.

---

## 1. Inventario de la solución

Solución: `WinCueSharp.sln` — 5 proyectos de producto (`src/`) + 4 de test (`tests/`).
Recuentos de `.cs` excluyendo `bin/` y `obj/`.

### Proyectos de producto

| Proyecto | Rol | Ficheros | Líneas | TFM |
|---|---|---:|---:|---|
| WinCue.Core | Modelo `Cue`, `CueModel`, settings, contratos de motor | 5 | 1 062 | net8.0 |
| WinCue.Audio | Motor audio multi-bus, DSP RT, EQ/comp, decoder | 5 | 1 607 | net8.0 |
| WinCue.Video | libmpv P/Invoke, motor vídeo, interop GL↔D3D | 6 | 2 173 | net8.0 |
| WinCue.Controller | Orquestación de cues, transporte, undo/redo, OSC/MIDI routing | 6 | 2 972 | net8.0 |
| WinCue.App | UI WPF, timeline, inspector, ventanas de salida, entrada remota | 16 | 8 809 | net8.0-windows |
| **Total producto** | | **38** | **16 623** | |

### Proyectos de test

| Proyecto | Ficheros | Líneas | Pruebas |
|---|---:|---:|---:|
| WinCue.Core.Tests | 5 | 807 | 45 |
| WinCue.Audio.Tests | 4 | 886 | 47 |
| WinCue.Video.Tests | 2 | 467 | 19 |
| WinCue.Controller.Tests | 13 | 2 725 | 144 |
| **Total test** | **24** | **4 885** | **255** |

**Total solución:** 62 ficheros `.cs`, 21 508 líneas. Ratio test/producto ≈ **0,29** líneas de test por línea de producto (sesgado: el 53 % del producto —`WinCue.App`— no tiene ningún test que lo referencie; ver §3 y §5).

### 15 ficheros más largos

| # | Fichero | Líneas | Proyecto | Nota |
|---:|---|---:|---|---|
| 1 | `MainController.cs` | 2 383 | Controller | Monolito de orquestación (ver §5) |
| 2 | `TimelinePanel.cs` | 1 813 | App | Editor de timeline, sin tests |
| 3 | `MainWindow.xaml.cs` | 1 625 | App | Ventana principal / IShowView |
| 4 | `InspectorPanel.cs` | 1 185 | App | Inspector 5 pestañas |
| 5 | `EqEditorControl.cs` | 963 | App | Editor gráfico EQ |
| 6 | `VideoEngine.cs` | 949 | Video | Estados de vídeo por cue |
| 7 | `AudioEngine.cs` | 653 | Audio | Mezclador multi-bus |
| 8 | `SettingsWindow.xaml.cs` | 588 | App | Configuración |
| 9 | `EditingAndSafetyTests.cs` | 487 | *test* | — |
| 10 | `VideoOutputWindow.xaml.cs` | 470 | App | Ventana de salida (SetWindowPos) |
| 11 | `GlDxInterop.cs` | 458 | Video | Interop OpenGL↔D3D9 |
| 12 | `TransportAndChainTests.cs` | 455 | *test* | — |
| 13 | `CanvasEditorControl.cs` | 454 | App | Editor de lienzo Watchout |
| 14 | `CueSampleProvider.cs` | 444 | Audio | Toda la matemática RT de audio |
| 15 | `WaveformControl.cs` | 352 | App | Onda con manejadores de trim |

Observación: **7 de los 15 ficheros más largos (y los 4 primeros no-Controller) están en `WinCue.App`**, el proyecto sin cobertura de tests.

---

## 2. Ensamblados y dependencias externas

### Paquetes NuGet

**Producto** (solo uno):

| Paquete | Versión | Proyecto | Licencia |
|---|---|---|---|
| NAudio | 2.3.0 | WinCue.Audio | MIT |

**Test** (idénticos en los 4 proyectos, declarados por separado en cada `.csproj`):

| Paquete | Versión |
|---|---|
| Microsoft.NET.Test.Sdk | 17.8.0 |
| xunit | 2.5.3 |
| xunit.runner.visualstudio | 2.5.3 |
| coverlet.collector | 6.0.0 |

> No hay gestión central de versiones (`Directory.Packages.props`): las 4 referencias de test se repiten en cada proyecto → riesgo de deriva al actualizar.

### DLLs nativas por P/Invoke

| DLL | Mecanismo | Origen | Dónde |
|---|---|---|---|
| `libmpv-2.dll` | `LibraryImport` (source-gen) | Motor de vídeo (client + render API) | `MpvNative.cs`, `MpvRenderNative.cs` |
| `d3d9.dll` | `DllImport` | Superficie D3D9 para interop | `GlDxInterop.cs` |
| `opengl32.dll` | `DllImport` | Contexto GL para render mpv | `GlDxInterop.cs` |
| `gdi32.dll` | `DllImport` | Contexto de dispositivo | `GlDxInterop.cs` |
| `user32.dll` | `DllImport` | Ventanas / posicionamiento | `GlDxInterop.cs`, `VideoOutputWindow.xaml.cs` |
| `kernel32.dll` | `DllImport` | Carga de módulos | `GlDxInterop.cs` |

- `libmpv-2.dll` se resuelve con un `DllImportResolver` propio (`MpvNative.SetDllImportResolver`), que la localiza subiendo directorios o vía env `WINCUE_LIBMPV`.
- Dependencia de runtime **no NuGet**: `FFmpeg`/`ffprobe` en el `PATH` (decode fallback, duraciones, waveform). No se valida su presencia en tiempo de arranque de forma centralizada.
- Nota de licencias (relevante para distribuir, ya en el checklist de `MEMORIA-PROYECTO.md`): confirmar que los binarios de libmpv/FFmpeg empaquetados son builds **LGPL**.

---

## 3. Organización de los tests

**4 proyectos de test, 255 pruebas** (246 `[Fact]` + 2 `[Theory]` con 9 `[InlineData]`). Framework: **xUnit**. Todos referencian **solo su proyecto de producto correspondiente** (no hay tests que referencien `WinCue.App`).

| Proyecto de test | Ficheros | Áreas cubiertas |
|---|---|---|
| Core.Tests | CueJsonTests, CueModelTests, SettingsManagerTests, ShowPruebaFileTests, TimelineLayerTests | Modelo de cue, **compatibilidad JSON con Python**, settings, capa de timeline |
| Audio.Tests | AudioDecoderTests, AudioEngineTests, CueSampleProviderTests, EqChainTests | DSP de audio **puro y sin hardware** (fades, loop, trim, RMS, EQ, compresor) |
| Video.Tests | MpvPropertyTests, VideoEngineTests | Motor de vídeo con **motores falsos** (sin GPU/mpv real) |
| Controller.Tests | Canvas, Editing/Safety, Group/Targeting, ListManagement, PlayFromTimeline, RelocateMedia, **RemoteControl**, **ShowPruebaE2E**, Transport/Chain, VideoFadeOut, VideoOutput, VideoStandby | Orquestación completa: transporte, cadenas, grupos, timeline, **OSC/MIDI routing**, E2E del show sintético con reloj virtual |

**Cobertura por capa (a ojo):**

- **Motor de cues / transporte / grupos / undo-redo:** muy cubierto (Controller.Tests, 144 pruebas) incl. E2E del show canónico.
- **Audio (DSP):** muy cubierto y bien aislado (testeable sin tarjeta de sonido).
- **Vídeo (lógica de estados):** cubierto con dobles; el interop GL↔D3D y mpv reales, por naturaleza, no.
- **OSC/MIDI:** el **parser OSC (`OscPacket`) y el routing (`OnOscMessage`/`OnMidiMessage`) sí están cubiertos** (viven en Controller). El **transporte de red/MIDI (`RemoteInputService`, UDP + `NAudio.Midi`) vive en App y NO está cubierto**.
- **UI / Timeline visual / Inspector / ventanas de salida:** **sin cobertura** (todo en `WinCue.App`).

> Corrección de documentación: `CONTEXTO_WINCUESHARP.md` lista "Fase 6 — OSC/MIDI IN" como el gran pendiente y "163 tests". La realidad hoy: **OSC/MIDI IN está implementado** (`RemoteInputService` + `OscPacket` + `RemoteControlTests`) y hay **255 tests**. El CONTEXTO está desactualizado; `MEMORIA-PROYECTO.md` (17-jul) sí refleja 255.

---

## 4. Configuración de análisis estático

| Elemento | Estado |
|---|---|
| `.editorconfig` | **Ausente** |
| `Directory.Build.props` / `.targets` | **Ausente** |
| `.globalconfig` | **Ausente** |
| `Directory.Packages.props` (versiones centralizadas) | **Ausente** |
| `global.json` (fijar SDK) | **Ausente** |
| `nuget.config` | **Ausente** |
| `AnalysisLevel` explícito | No fijado en ningún `.csproj` |
| `EnableNETAnalyzers` | No fijado → toma el **default de .NET 8: `true`** |
| `TreatWarningsAsErrors` / `WarningsAsErrors` | **No** (los warnings no bloquean) |
| `LangVersion` | No fijado (default del TFM) |
| `Nullable` | **`enable` en los 9 proyectos** ✓ |
| `ImplicitUsings` | **`enable` en los 9 proyectos** ✓ |
| CI (`.github/workflows`, etc.) | **Ausente** — nada compila ni testea automáticamente |

**Warnings vivos en compilación limpia** (los analizadores del SDK sí corren, pero nadie los atiende ni bloquean el build):

| Regla | Ubicación | Descripción | Severidad práctica |
|---|---|---|---|
| **CA2014** ×2 | `OscPacket.cs:168` y `:173` | `stackalloc byte[4]` **dentro de un `foreach`** en `OscPacket.Build`. Las asignaciones de pila se acumulan hasta el retorno del método → posible desbordamiento de pila con mensajes de muchos argumentos float/double. | Baja hoy (el feedback OSC lleva pocos args) pero es un **defecto latente real** |
| **CS0414** | `MainWindow.xaml.cs:292` | Campo `_statusHasWarnings` asignado pero nunca usado (código muerto) | Cosmética |

Sin `.editorconfig` no hay forma de fijar severidades por regla ni de acordar estilo; sin `TreatWarningsAsErrors` ni CI, un warning nuevo (o la regresión de un test) puede entrar en `main` sin que nadie lo note.

---

## 5. Cinco hallazgos que más deberían preocupar

1. **`WinCue.App` sin ninguna prueba — y es el 53 % del código de producto.**
   Ningún proyecto de test referencia `WinCue.App`. Quedan fuera de red de seguridad: `TimelinePanel` (1 813 L, el editor de timeline), `MainWindow` (1 625 L), `InspectorPanel` (1 185 L), los editores de EQ/canvas y —crítico— `RemoteInputService` (el transporte UDP/MIDI real que dispara el show desde Companion/consola). Es justo la capa que más cambia y la que el operador toca en directo.

2. **No hay CI: los "255 en verde" dependen de disciplina manual.**
   Nada ejecuta el build ni los tests automáticamente al commitear. Un cambio puede romper la suite o meter un warning y llegar a `main` sin señal. La línea base es sólida hoy, pero no está *protegida*.

3. **Analizadores activos pero desatendidos, con un CA2014 real.**
   `stackalloc` dentro de bucle en `OscPacket.Build` (salida de feedback OSC): riesgo latente de desbordamiento de pila. Es bajo en la práctica actual, pero es exactamente el tipo de bug que un directo saca a la luz en el peor momento. No hay `.editorconfig` ni gate que obligue a mirarlo.

4. **`MainController.cs` (2 383 L) concentra demasiada responsabilidad.**
   Transporte, cadenas, grupos, undo/redo, routing remoto y wall-clock en un solo fichero. Está bien testeado (bien), pero es el punto de fragilidad y el que encarece cada feature nueva. Ya señalado como deuda en `MEMORIA-PROYECTO.md`; conviene trocearlo antes de crecer más.

5. **Sin gobernanza de build compartida.**
   No hay `Directory.Build.props` (propiedades comunes), ni `Directory.Packages.props` (versiones NuGet centralizadas — hoy los 4 test las repiten), ni `global.json` (SDK fijado), ni `.editorconfig`. Sin `TreatWarningsAsErrors` la calidad no tiene suelo: cada proyecto es una isla y la deriva es cuestión de tiempo.

---

## Apéndice · Comandos de la línea base

```powershell
# Build limpio
dotnet build WinCueSharp.sln -c Debug --no-incremental
# Resultado: OK, 4 advertencias (CA2014 ×2, CS0414 ×… tras doble paso XAML)

# Suite completa
dotnet test WinCueSharp.sln -c Debug
# Core 45 · Controller 144 · Audio 47 · Video 19 = 255 · 0 fallos · 0 omitidas
```
