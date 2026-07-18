# Auditoría WinCue — 01 · Análisis estático (Roslyn)

**Fecha:** 2026-07-18 · **Rama:** `pruebas-v2` · **Estado:** análisis activado y medido. **Nada de código arreglado aún — pendiente de tu visto bueno.**

## Qué se ha activado

- **`Directory.Build.props`** (raíz de WinCueSharp): `EnableNETAnalyzers=true`, `AnalysisLevel=latest-all`, `EnforceCodeStyleInBuild=true`. **`TreatWarningsAsErrors` NO** (los warnings no bloquean todavía).
- **`.editorconfig`** (raíz): estilo **deducido del código existente** (file-scoped namespaces, llaves Allman, 4 espacios, campos `_camelCase`, sin `this.`, `var` cuando el tipo es aparente, cuerpos de expresión). Las reglas de estilo van como `suggestion`/`silent` para no generar warnings: el código ya es coherente y no se impone un estilo nuevo.
- Verificado: `dotnet test` = **255 en verde** tras los cambios de configuración.

## Cómo leer los números

`AnalysisLevel=latest-all` sobre el build limpio produjo **1 334 warnings** (deduplicados). Tras **silenciar el cubo 3** en `.editorconfig`, el residual es **42 líneas de warning, todas en código de producto** — justo los cubos 1 y 2.

> **Nota de conteo:** MSBuild emite cada warning dos veces (ruta relativa + absoluta) y el proyecto WPF compila en dos pasadas. Los recuentos "crudos" están inflados ~2×. Abajo doy el **número real de sitios únicos**, verificado leyendo el código.

### Reparto del ruido inicial (1 334) — por regla, top

| Cuenta | Regla | Qué es | Cubo |
|---:|---|---|---|
| 490 | CA1707 | Guiones bajos en identificadores (nombres de test) | 3 |
| 144 | CA1051 | Campos de instancia públicos (DTO `Cue`) | 3 |
| 108 | CA1031 | `catch(Exception)` genérico | 3 |
| 98 | CA1062 | Validar args de métodos públicos | 3 |
| 78 | CA5392 | `DefaultDllImportSearchPaths` en P/Invoke | 3 |
| 60 | CA1003 | Firmas de evento no genéricas | 3 |
| 58 | CA1307 | `StringComparison` en comparaciones | 3 |
| 50 | CA1002 | Exponer `List<T>` | 3 |
| 48 | CA1305 | `IFormatProvider` en formateo | 3 |
| … | (resto globalización/cosmética/micro-opt) | | 3 |
| **42** | **(residual tras silenciar)** | **CA2213/CA2000/CA1001/CA2216/CA2101/CA2014/CA2211/CA1806/CA1508 + CS0414** | **1 y 2** |

El **97 %** del ruido era cosmética/diseño deliberado. El 3 % restante es lo que importa.

---

## Lista priorizada (residual = cubos 1 y 2)

### CUBO 1 — Bugs reales / riesgo en runtime · PRIORIDAD ALTA

| # | Regla | Sitio(s) | Diagnóstico | Riesgo |
|---:|---|---|---|---|
| 1 | **CA2014** ×2 | `Controller/OscPacket.cs:168` y `:173` (`OscPacket.Build`) | `stackalloc byte[4]` **dentro de un `foreach`** sobre los argumentos. La memoria de pila se acumula hasta el retorno del método → un mensaje OSC con muchos args float/double puede **desbordar la pila**. | **Real.** Bajo hoy (el feedback OSC lleva pocos args), pero es un fallo latente. Fix trivial: sacar el `Span<byte>` fuera del bucle. |
| 2 | **CA2101** ×2 | `Video/GlDxInterop.cs:180` y `:183` (`wglGetProcAddress`, `GetModuleHandle`) | P/Invoke con string sin especificar marshaling/best-fit. | Bajo (ya usan `CharSet.Ansi`/`Unicode`; los nombres son ASCII). Correctitud de interop; conviene fijar `[DefaultDllImportSearchPaths]`/`BestFitMapping=false`. |
| 3 | **CA2211** ×1 | `App/EqEditorControl.cs:479` (`public static double RtaGainDb = 18.0`) | Campo **estático mutable visible**: estado compartido de sesión. | Bajo (se toca solo desde el hilo de UI). Encapsular en propiedad. |
| 4 | **CA1508** ×1 | `App/SettingsWindow.xaml.cs:353` | Condición muerta: `v` nunca es `null` → rama inalcanzable. | Cosmético/lógico. Verificar que es solo una guarda redundante (probablemente sí) y limpiar. |

### CUBO 2 — Recursos no liberados / IDisposable / eventos · PRIORIDAD ALTA (fuga en shows largos)

| # | Regla | Sitio(s) | Diagnóstico | Riesgo |
|---:|---|---|---|---|
| 5 | **CA2213** ×3 | `Video/MpvGpuRenderer.cs:26,27,31` (`_wake` AutoResetEvent, `_ready` ManualResetEventSlim, `_gl` GlContext) | El `Dispose()` (línea 221) hace `_shutdown=true; _wake.Set(); _thread.Join()`, pero **nunca libera `_wake` ni `_ready`** (wait handles de OS). `_gl` solo se libera en `CleanupOnThread()`, que corre en el hilo de render: **si el `Join(4000)` expira, `_gl` + superficies D3D se pierden**. | **LA FUGA.** El compositor **crea/destruye un `MpvGpuRenderer` por cue** → cada cue filtra 2 wait handles (y potencialmente el contexto GL/D3D). Coincide exactamente con la deuda "RAM/VRAM en shows largos" de `MEMORIA-PROYECTO.md`. **Top prioridad.** |
| 6 | **CA2000** ×4 sitios | `Video/VideoEngine.cs:588` (`new MpvPlayer`), `:833` (`new MpvGpuRenderer`); `Audio/AudioEngine.cs:491` (`CreateOutput`); `App/RemoteInputService.cs:92` (`new UdpClient`) | Objetos `IDisposable` que **no se liberan en todas las rutas** (excepción entre crear y almacenar → se pierden sin `Dispose`). | Alto en los de vídeo/audio (se crean por cue/por dispositivo); medio en el UDP (una vez por arranque de red). |
| 7 | **CA1001** ×4 tipos | `App/App.xaml.cs` (`_audio/_remote/_video`), `App/LiveMonitorControl.cs` (`_host/_panel/_player`), `App/SettingsWindow.xaml.cs` (`_learnInput` MidiIn), `App/VideoOutputWindow.xaml.cs` (`_hwndHost`) | Tipos que **poseen campos `IDisposable` pero no son `IDisposable`** → nadie garantiza su liberación. | `SettingsWindow._learnInput` (handle MIDI) y `LiveMonitorControl._player` (mpv) son los peligrosos: se recrean al abrir Settings / reconstruir monitores → **fuga de handle por apertura**. `App.*` es menos grave (el proceso muere). |
| 8 | **CA2216** ×2 | `Video/MpvPlayer.cs:19`, `Video/GlDxInterop.cs:135` (GlContext) | Tipos que poseen handles nativos (`nint`) y **no declaran finalizador** de respaldo. | Medio. Sin finalizador, si alguien olvida `Dispose()` el recurso nativo se filtra sin red de seguridad. (Valorar: el equipo puede preferir liberación determinista pura.) |
| 9 | **CA1806** ×1 | `Video/GlDxInterop.cs:451` (`ReleaseDC` en `Dispose`) | Se ignora el HRESULT de `ReleaseDC`. Si falla (condición de pocos recursos), **el device context GDI no se libera**. | Bajo-medio. Comprobar el retorno o registrar el fallo. |
| 10 | **(manual) eventos `+=` sin `-=`** | Asimetría marcada en producto: muchísimos `+=`, solo 5 `-=` (`LiveMonitorControl`, `TimelinePanel`, `AudioDecoder`) | **Roslyn no tiene regla fiable para desuscripción de eventos**, así que esto NO sale en los 42 warnings. Revisión manual pendiente: suscripciones desde objetos de vida larga (controlador, motores) a objetos de UI de vida corta = fuga clásica. | A determinar caso por caso en el paso de arreglo. Muchos `+=` son cableado único en constructores (inocuo); hay que separar el grano. |

### CUBO 3 — Estilo y cosmética · PRIORIDAD BAJA (silenciado en `.editorconfig`)

Silenciadas con justificación individual en `.editorconfig`. Resumen de por qué **no aportan** aquí:

| Reglas | Motivo del silenciado |
|---|---|
| CA1051, CA1002, CA1819, CA1034, CA1024, CA1003, CA1030 | Diseño deliberado: el modelo `Cue` es un **DTO con paridad 1:1 al JSON de Python**; expone campos/colecciones a propósito. No es librería reutilizable. |
| CA1062 | Validación de args ya cubierta por **NRT** (`Nullable=enable`). Guardas manuales = ruido. |
| CA1031 | `catch(Exception)` es **decisión de diseño** ("el show nunca se cae"). *Nota:* varios hacen swallow **sin log** — endurecer/loguear es tema de fase aparte, no un warning bloqueante. |
| CA1304/1305/1307/1308/1310 | **Globalización**: datos invariantes (claves snake_case, cue numbers ASCII). La cultura no cambia el resultado. |
| CA1707, CA1725, CA1716, CA1724, CA1032 | Cosmética de nombres. CA1707 = convención de test `Metodo_Escenario_Resultado`. |
| CA1859, CA1861, CA1814, CA1820, CA1513 | Micro-optimizaciones sin impacto medible. |
| CA2007 | App **WPF**: `ConfigureAwait(false)` rompería la afinidad de hilo de UI. |
| CA5392 | P/Invoke a DLLs de **sistema** (KnownDLLs) + resolver propio de libmpv; forzar `DefaultDllImportSearchPaths` lo rompería. *(Revisar antes de distribuir, por DLL planting.)* |
| CA5394 | `Random` del round-robin de grupos RANDOM: no es aleatoriedad criptográfica. |
| CA1063, CA1816 (solo tests) | Patrón `Dispose(bool)`/`SuppressFinalize` en fixtures xUnit: irrelevante en tests. |

**Restos cosméticos NO silenciados (limpieza trivial, no fuga):**
- **CS0414** ×1 — `App/MainWindow.xaml.cs:292`: campo `_statusHasWarnings` asignado pero nunca usado (código muerto). Borrarlo.

---

## Plan de arreglo propuesto (para tu visto bueno)

Orden sugerido, **por área y en commits separados**, empezando por IDisposable/using/eventos (cubo 2):

1. **Vídeo — la fuga del compositor (CA2213, CA2000, CA2216, CA1806).** Es la de mayor valor: liberar `_wake`/`_ready` en `MpvGpuRenderer.Dispose()` tras el `Join`, blindar `_gl` si el join expira, envolver `new MpvPlayer`/`new MpvGpuRenderer` en las rutas seguras, finalizadores de respaldo y comprobar `ReleaseDC`.
2. **Audio — CA2000 en `CreateOutput`.** Liberar la salida si algo falla entre crearla y registrarla.
3. **App/UI — CA1001 y CA2000.** Hacer `IDisposable` (o desmontar en `Unloaded`/cierre) `LiveMonitorControl`, `SettingsWindow` (MIDI learn), `VideoOutputWindow`, `App`; liberar el `UdpClient` de `RemoteInputService`.
4. **Motor de cues — CA2014 (OscPacket).** Sacar el `stackalloc` del bucle.
5. **Revisión manual de eventos `+=`/`-=`** en objetos de vida larga.
6. Menores: CA2211 (encapsular `RtaGainDb`), CA1508 y CS0414 (limpiar código muerto).

Regla de trabajo acordada: un commit por área · `dotnet test` (255) en verde tras cada uno · si algún arreglo cambia comportamiento observable, **paro y aviso** antes de seguir.

**No he tocado código de negocio. Espero tu visto bueno para empezar por el punto 1 (vídeo/IDisposable).**
