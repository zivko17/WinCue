# WinCue — Traspaso de contexto (2026-07-22)

> Para retomar en otro chat. Léelo junto con `docs/auditoria/INFORME.md` (estado
> ejecutivo) y `CONTEXTO_WINCUESHARP.md` (brief técnico — algo desactualizado,
> ver notas abajo). La memoria persistente del asistente se auto-carga si abres
> el chat en esta carpeta.

## Qué es
**WinCue** = reproductor de cues de audio/vídeo para directo estilo QLab, en
C#/.NET 8 + WPF. Migración completa (y superación) del prototipo Python en
`../WinCue` (solo referencia). Repo `ffmpeg-gui`, rama **`pruebas-v2`**.
Carpeta: `C:\Proyectos\ffmpeg-gui\WinCueSharp`.

## Cómo trabajar (flujo acordado)
editar → `dotnet build` → `dotnet test` (**286 tests, todos verdes**) →
smoke (`--smoke`, exit 0) → commit en español (uno por feature) → **matar
`WinCue.App` antes de compilar** (bloquea las DLLs) → relanzar en background
para Andrés, que prueba en pantalla y manda capturas/.mp4.
- Build a **CERO warnings** con `AnalysisLevel=latest-all` — mantenerlo así.
- Scripts `.ps1` deben ir con **BOM UTF-8** (PowerShell 5.1 si no los rompe).
- JSON de show/settings **100% intercambiable con Python** (no romper).

## Estado actual (lo hecho en las últimas sesiones)
- **Auditoría técnica fases 0-5** completa en `docs/auditoria/` (00-inventario,
  01-análisis-estático, 02-arquitectura, 03-cobertura, 04-estrés, 05-fugas-vram,
  INFORME). `MainController` troceado 2383→1687 líneas (5 servicios extraídos).
- **Fugas reales arregladas** (fase 1) + **la "fuga de VRAM" del compositor era
  FALSA ALARMA** (bug del contador GPU por-proceso de Windows; la VRAM física
  del adaptador está sana). La investigación dejó un **refactor bueno**: pool de
  pipelines GPU persistentes en `MpvGpuRenderer` (Attach/Detach) + escritura al
  D3DImage dentro del Lock. → **PENDIENTE: verificación visual del compositor.**
- **UI pulida** (sesión 2026-07-22): banda de grupos en la tabla, selección
  legible, PGM/PVW grandes por defecto, desplegables de Settings legibles
  (plantilla propia), toggle SHOW verde, hover de botones sin salto.
- **Localización ES/EN COMPLETA** de la UI operativa: `Loc` en `WinCue.Core`
  (`Loc.T(es, en)` inline) + `LocExtension` `{local:Loc}` para XAML. Traducidos
  ventana principal, tipos de cue, todos los mensajes del controlador, Settings
  e Inspector enteros, diálogos y menú contextual. Selector en
  **Configuración → Interfaz** (se aplica al REINICIAR). **Inglés por defecto.**

## LO QUE FALTA — por prioridad

### 1. libmpv LGPL (BLOQUEANTE para vender/repartir beta cerrada) — EN CURSO
La `libmpv-2.dll` actual es **GPL confirmado** → incompatible con licencia
propietaria (la GPL obliga a entregar el fuente). Hay que compilar una LGPL.
- Fork ya creado: **github.com/zivko17/mpv-winbuild-cmake** con los 3 cambios
  aplicados (guía en `tools/libmpv-lgpl/README.md`).
- La CI (workflow "mpv gcc", run 29892168083) **FALLÓ en "Building toolchain"**
  a los ~4 min — ANTES de llegar a los cambios LGPL, o sea la config es correcta;
  es fallo de infra del fork en frío. **Acción: re-lanzar el workflow y leer el
  error real del paso "Building toolchain"** (`gh run view --log-failed`).
- Cuando salga la DLL: verificar con
  `dotnet run --project tools/libmpv-lgpl/verificar-licencia` (con
  `WINCUE_LIBMPV=<ruta>`) → debe decir **`gpl=false`**. Luego apuntar
  `publish.ps1` a esa DLL + crear `licenses/libmpv-BUILD.txt` (origen de la build).

### 2. Verificación visual de Andrés (no automatizable)
- El **refactor del compositor** (pool GPU): crossfades misma pista, cruce entre
  capas, modo canvas, salida 2, GO instantáneo del standby, opacidades.
- La **localización**: repasar EN y ES buscando cadenas sueltas que se escaparan.

### 3. Paquete beta (tras libmpv LGPL)
`publish.ps1` (ya empaqueta EULA + notices) + README-beta con el truco de
SmartScreen ("Más información → Ejecutar de todas formas", o desbloquear el ZIP:
clic derecho → Propiedades → Desbloquear) + show demo + ZIP. Firma de código
(certificado EV) solo para el lanzamiento comercial, no para la beta.

### 4. Deseables
Repo privado en GitHub + CI (Actions: build+test+warnings-as-errors) · soak de
4 h opcional (`.\scripts\stress.ps1 -Hours 4`) · `TreatWarningsAsErrors` (gratis
ya que el build está a cero warnings) · multiviewer de salidas · presets de
EQ/comp · escala absoluta del RTA (cosmético).

## Notas / gotchas
- `CONTEXTO_WINCUESHARP.md` está **desactualizado** (dice 163 tests / fase 6
  pendiente; realidad: 286 tests, OSC/MIDI IN hecho). Usar INFORME + este doc.
- Decisión de licencia: Andrés quiere **propietaria de evaluación** (uso libre
  para probar, sin redistribuir, código cerrado) → `docs/legal/EULA-EVALUACION.md`.
  Eso es lo que EXIGE la libmpv LGPL (con GPL no se puede).
- Marca "WinCue": verificar colisión (hay un teleprompter histórico) antes de
  registrar dominio/marca.
