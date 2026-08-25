# Secuence — Editor de canvas y compositor por puente D3D9→D3D11: plan de implementación

> **Para agentes:** SUB-SKILL OBLIGATORIA: usa `superpowers:subagent-driven-development`
> (recomendada) o `superpowers:executing-plans` para ejecutar este plan tarea a
> tarea. Los pasos usan casillas (`- [ ]`) para el seguimiento.

**Goal:** Que la pestaña Vídeo de Secuence sea la de WinCue —canvas incluido— y
que el modo canvas **funcione de verdad**, abriendo en D3D11 la superficie
compartida que el renderer de mpv ya produce y componiéndola con el compositor
que ya está construido.

**Architecture:** El cálculo del editor sube a `WinCue.Presentation` con
pruebas y **los dos productos lo usan**. El vídeo entra al compositor por un
puente: `MpvGpuRenderer` ya crea una superficie compartida D3D9Ex por recorte;
se guarda su handle, se abre con `OpenSharedResource` y se publica al grafo
como `IVideoSource`. Un `OutputCompositor` + `SwapChainSink` por salida
presenta sobre el HWND hijo que Secuence ya crea. WinCue no se toca.

**Tech Stack:** C# / .NET 8 (SDK clavado en 8.0.423), WinUI 3 (Windows App
SDK, desempaquetado), WPF (WinCue), Vortice.Direct3D11 y Vortice.Direct3D9,
mpv (libmpv), xUnit.

**Spec:** `docs/superpowers/specs/2026-08-25-secuence-canvas-compositor-design.md`
— léelo entero antes de la primera tarea. Este plan discute *cómo*; el *por
qué* y los cinco hallazgos verificados están allí.

## Global Constraints

- **Rama:** `claude/secuence-hitos-0-1-1occ8p`. Raíz git: `C:\Proyectos\ffmpeg-gui`
  — **no** `WinCueSharp\`. Los `git add` llevan el prefijo `WinCueSharp/`.
- **WinCue no cambia de comportamiento ni de aspecto.** Está vendiéndose. Todo
  lo que se toca de `WinCue.Core` / `WinCue.Video` es **aditivo y con
  implementación por defecto**.
- **Prohibido copiar código entre productos.** Lo común sube a
  `WinCue.Presentation` con pruebas; lo de framework se reescribe.
- **`Secuence` grafía deliberada.** No la corrijas nunca, en código ni en
  texto.
- **Build:** `dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true`
  debe dar **0 avisos, 0 errores**.
- **Pruebas:** `dotnet test WinCueSharp.sln -c Release --filter "Requiere!=libmpv"`.
  Punto de partida: **1.194 en verde**. `MpvPropertyTests.FadeVolume_ReachesTarget`
  falla a veces por temporización bajo carga; pasa en solitario. Cualquier OTRA
  en rojo sí es señal.
- **Guardián de paleta:** `pwsh -File WinCueSharp\scripts\verificar-paleta.ps1`
  debe salir OK. **Ningún color literal en código de producto**: todo sale de
  `WinCue.Presentation.Palette` vía `Theme`.
- **Antes de compilar Secuence:**
  `Stop-Process -Name Secuence -Force -ErrorAction SilentlyContinue`. Con la
  app abierta el build revienta con doce MSB3021/MSB3027 de ficheros
  bloqueados. **No mates `WinCue.App`** — puede estar abierto porque lo está
  usando Andrés.
- **Textos de interfaz bilingües:** siempre `Loc.T("es", "en")`. Nunca una
  cadena suelta.
- **Mensajes de commit en ASCII** (sin acentos ni eñes en el asunto), prefijo
  convencional con ámbito: `feat(secuence): …`, `fix(secuence): …`,
  `refactor(wincue): …`, `test: …`, `docs: …`.
- **Lo visual va a un epígrafe `## PENDIENTE — verificación en pantalla
  (Andrés)`** en el informe de la tarea, con el comando exacto y qué mirar.
  Ningún subagente ve pantallas.
- **Claves de settings** (las mismas que WinCue, el motor ya las entiende):
  `video_compositor`, `video_canvas_mode`, `surface_width`, `surface_height`,
  `video_output_region`, `video_output_region2..4`,
  `video_output_enabled2..4`. Nueva y solo de Secuence: `pgm_monitor_source`.
- **Trampas de WinUI 3 ya pagadas** (no las redescubras): `TextBox` trae
  `MinWidth = 64` y `CheckBox` `MinWidth = 120`; el `Padding` por defecto es
  (6,4) contra (4,2) de WPF; `Border` y `CanvasControl` están **sellados**;
  `Colors` es `Microsoft.UI.Colors`. La lista larga, en
  `docs/superpowers/TRASPASO-SECUENCE.md`.

---

## Estructura de ficheros

**Se crean:**

| Fichero | Responsabilidad |
|---|---|
| `src/WinCue.Presentation/CanvasEditorLayout.cs` | El cálculo del editor: encaje, imanes, topes, presets, colores. Sin framework |
| `tests/WinCue.Presentation.Tests/CanvasEditorLayoutTests.cs` | Sus pruebas puras |
| `src/WinCue.Video/Gpu/SharedSurfaceSource.cs` | El puente: handle compartido D3D9Ex → textura D3D11 → `IVideoSource` |
| `tests/WinCue.Video.Tests/SharedSurfaceSourceTests.cs` | La prueba por píxel del puente |
| `src/Secuence.App/CanvasEditorPanel.cs` | El editor de canvas en WinUI 3 |
| `src/Secuence.App/SalidaGpu.cs` | Compositor + swapchain de UNA salida |
| `src/Secuence.App/CapaCompositor.cs` | `IVideoLayerSurface` de Secuence: una capa del grafo |

**Se modifican:**

| Fichero | Cambio |
|---|---|
| `src/WinCue.Core/MediaEngineContracts.cs:215-244` | `IVideoLayerSurface.SetSharedHandle(nint)`, cuerpo vacío por defecto |
| `src/WinCue.Video/MpvGpuRenderer.cs:210-220, 72-76` | Guardar y exponer el handle compartido de presentación |
| `src/WinCue.Video/VideoEngine.cs:1529, 1711, 1920` | Llamar a `SetSharedHandle` en los tres sitios donde se montan sinks |
| `src/WinCue.Video/Gpu/FrameRing.cs:36-65` | Constructor interno de `FrameLease` sin anillo |
| `src/WinCue.Video/Gpu/OutputCompositor.cs` | Sink secundario opcional (el espejo del PGM) |
| `src/WinCue.App/CanvasEditorControl.cs` | Pasa a usar `CanvasEditorLayout` |
| `src/Secuence.App/SettingsPanel.cs:153-192` | Compositor GPU + grupo CANVAS en la pestaña Vídeo |
| `src/Secuence.App/VideoOutputWindow.xaml.cs` | Da su `CapaCompositor` y su `SalidaGpu` |
| `src/Secuence.App/MainWindow.xaml.cs:1398, 2104` | Enrutado del proveedor, `SetVideoCueOpacity`, `OutColors` compartido |
| `src/Secuence.App/App.xaml.cs:147-160` | `SetGpuLayerSurfaceProvider` |
| `src/Secuence.App/LiveMonitorControl.cs` | Selector y espejo del compositor |
| `docs/superpowers/TRASPASO-SECUENCE.md`, `ARRANQUE-SESION-LOCAL.md` | Estado al día |

**Orden y dependencias:** 1 → 2 son el editor compartido. 3 depende de 1.
4 → 5 → 6 → 7 son el compositor. 8 depende de 7. 9 al final. Las tareas 1-3 y
4-6 **no dependen entre sí**: se pueden llevar en paralelo.

---

### Task 1: `CanvasEditorLayout` — el cálculo compartido

**Files:**
- Create: `src/WinCue.Presentation/CanvasEditorLayout.cs`
- Test: `tests/WinCue.Presentation.Tests/CanvasEditorLayoutTests.cs`
- Referencia (leer, no tocar todavía): `src/WinCue.App/CanvasEditorControl.cs:270-400`

**Interfaces:**
- Consumes: `WinCue.Presentation.Palette` (`Go`, `Info`, `Standby`, `Magenta`),
  `WinCue.Controller.CanvasLayout.ParseRegion(string, int screen, int cw, int ch)`
  → `CanvasView(Screen, X, Y, W, H)`.
- Produces:
  - `readonly record struct CanvasRegion(int X, int Y, int W, int H)`
  - `readonly record struct ViewRect(double X, double Y, double W, double H)`
  - `static class CanvasEditorLayout` con:
    `const int MinSize = 16`, `const double SnapScreenPx = 10.0`, `const int Outputs = 4`,
    `IReadOnlyList<uint> OutputColors`,
    `IReadOnlyList<(int W, int H, string Label)> SizePresets`,
    `double Scale(int cw, int ch, double viewW, double viewH)`,
    `(double X, double Y) Origin(int cw, int ch, double viewW, double viewH)`,
    `ViewRect ToView(CanvasRegion r, int cw, int ch, double viewW, double viewH)`,
    `int ToCanvasDelta(double screenDelta, double scale)`,
    `CanvasRegion Clamp(CanvasRegion r, int cw, int ch)`,
    `IReadOnlyList<int> SnapTargetsX(IReadOnlyList<CanvasRegion> regions, IReadOnlyList<bool> enabled, int self, int cw)`,
    `IReadOnlyList<int> SnapTargetsY(IReadOnlyList<CanvasRegion> regions, IReadOnlyList<bool> enabled, int self, int ch)`,
    `CanvasRegion Move(CanvasRegion orig, int dx, int dy, IReadOnlyList<CanvasRegion> regions, IReadOnlyList<bool> enabled, int self, int cw, int ch, double scale)`,
    `CanvasRegion Resize(CanvasRegion orig, int dx, int dy, IReadOnlyList<CanvasRegion> regions, IReadOnlyList<bool> enabled, int self, int cw, int ch, double scale)`,
    `string Format(CanvasRegion r)`.

**Notas de diseño que NO se pueden perder al portar:**
1. El umbral del imán son **10 px de PANTALLA**: dentro del cálculo se
   convierte con `Math.Max(1, SnapScreenPx / scale)`. Al alejar el zoom, el
   imán agarra desde más lejos en píxeles de canvas. Copiar `10` como píxeles
   de canvas es el error fácil.
2. Al **mover**, los imanes actúan sobre el borde izquierdo *y* el derecho
   (`x` y `x + w`), y en cuanto uno engancha se corta (`break`).
3. Al **redimensionar**, solo enganchan el borde derecho e inferior, y solo si
   el resultado no baja de `MinSize`.
4. Los topes finales se aplican **después** del imán, nunca antes.

- [ ] **Step 1: Escribir las pruebas que fallan**

Crear `tests/WinCue.Presentation.Tests/CanvasEditorLayoutTests.cs`:

```csharp
// =============================================================================
// CanvasEditorLayoutTests.cs — El calculo del editor de canvas
// =============================================================================
// Lo que estas pruebas defienden es que los editores de los DOS productos no
// puedan divergir: el de WPF y el de WinUI 3 preguntan aqui.
// =============================================================================

using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class CanvasEditorLayoutTests
{
    private static IReadOnlyList<CanvasRegion> Cuatro(params CanvasRegion[] r)
    {
        var list = new List<CanvasRegion>(r);
        while (list.Count < 4)
            list.Add(new CanvasRegion(0, 0, 1920, 1080));
        return list;
    }

    private static IReadOnlyList<bool> Habilitadas(params bool[] e)
    {
        var list = new List<bool> { true };
        list.AddRange(e);
        while (list.Count < 4)
            list.Add(false);
        return list;
    }

    // ── Encaje del lienzo en la vista ──────────────────────────────────

    [Fact]
    public void Scale_LienzoApaisado_EncajaPorElAncho()
    {
        // 5760x1080 en una vista de 480x270: manda el ancho.
        double s = CanvasEditorLayout.Scale(5760, 1080, 480, 270);
        Assert.Equal((480 - 4) / 5760.0, s, 6);
    }

    [Fact]
    public void Scale_LienzoVertical_EncajaPorElAlto()
    {
        // 1920x2160 en 480x270: manda el alto.
        double s = CanvasEditorLayout.Scale(1920, 2160, 480, 270);
        Assert.Equal((270 - 4) / 2160.0, s, 6);
    }

    [Fact]
    public void Origin_CentraElLienzoEnLaVista()
    {
        var (x, y) = CanvasEditorLayout.Origin(1920, 1080, 480, 270);
        double s = CanvasEditorLayout.Scale(1920, 1080, 480, 270);
        Assert.Equal((480 - 1920 * s) / 2.0, x, 6);
        Assert.Equal((270 - 1080 * s) / 2.0, y, 6);
    }

    [Fact]
    public void ToView_RegionCompleta_EsElLienzoEntero()
    {
        var r = CanvasEditorLayout.ToView(
            new CanvasRegion(0, 0, 1920, 1080), 1920, 1080, 480, 270);
        var (ox, oy) = CanvasEditorLayout.Origin(1920, 1080, 480, 270);
        double s = CanvasEditorLayout.Scale(1920, 1080, 480, 270);
        Assert.Equal(ox, r.X, 6);
        Assert.Equal(oy, r.Y, 6);
        Assert.Equal(1920 * s, r.W, 6);
        Assert.Equal(1080 * s, r.H, 6);
    }

    // ── El iman mide en pixeles de PANTALLA ────────────────────────────

    [Fact]
    public void Move_ElUmbralDelIman_SeMideEnPixelesDePantalla()
    {
        // Lienzo enorme (5760 de ancho) en una vista pequena: la escala es
        // ~0.0826, asi que 10 px de pantalla son ~121 px de canvas. Un borde a
        // 100 px del centro del lienzo TIENE que engancharse.
        var regiones = Cuatro(new CanvasRegion(0, 0, 1920, 1080));
        double s = CanvasEditorLayout.Scale(5760, 1080, 480, 270);

        var movida = CanvasEditorLayout.Move(
            new CanvasRegion(2780, 0, 1920, 1080), 0, 0,
            regiones, Habilitadas(), self: 1, cw: 5760, ch: 1080, scale: s);

        Assert.Equal(2880, movida.X);   // enganchado a la mitad del lienzo
    }

    [Fact]
    public void Move_LejosDelIman_NoSeMueveSolo()
    {
        var regiones = Cuatro(new CanvasRegion(0, 0, 1920, 1080));
        double s = CanvasEditorLayout.Scale(1920, 1080, 480, 270);

        // OJO con elegir la region: con escala 0.2463 el umbral son 40 px de
        // canvas, y una region en y=200 con alto 300 tiene el borde inferior
        // en 500 — a exactamente 40 de la mitad del lienzo (540), asi que SI
        // se engancharia. Aqui se elige y=150 a proposito.
        var movida = CanvasEditorLayout.Move(
            new CanvasRegion(500, 150, 400, 300), 0, 0,
            regiones, Habilitadas(), self: 1, cw: 1920, ch: 1080, scale: s);

        Assert.Equal(500, movida.X);
        Assert.Equal(150, movida.Y);
    }

    [Fact]
    public void Move_ElBordeDERECHO_TambienEngancha()
    {
        // Region cuyo borde derecho cae casi en el borde del lienzo.
        var regiones = Cuatro(new CanvasRegion(0, 0, 1920, 1080));
        double s = CanvasEditorLayout.Scale(1920, 1080, 480, 270);

        var movida = CanvasEditorLayout.Move(
            new CanvasRegion(1500, 0, 400, 1080), 0, 0,
            regiones, Habilitadas(), self: 1, cw: 1920, ch: 1080, scale: s);

        Assert.Equal(1520, movida.X);   // x + w == 1920
    }

    [Fact]
    public void Move_SeEnganchaAlBordeDeOtraSalidaHabilitada()
    {
        // El ancho de S2 importa: con 970 el tope (cw - w = 950) volveria a
        // dejarla en 950 DESPUES del iman, y la prueba mediria el tope en vez
        // del iman. Con 800 el tope es 1120 y no estorba.
        var regiones = Cuatro(
            new CanvasRegion(0, 0, 960, 1080),       // S1: mitad izquierda
            new CanvasRegion(950, 0, 800, 1080));    // S2: casi pegada
        double s = CanvasEditorLayout.Scale(1920, 1080, 480, 270);

        var movida = CanvasEditorLayout.Move(
            regiones[1], 0, 0, regiones, Habilitadas(true),
            self: 1, cw: 1920, ch: 1080, scale: s);

        Assert.Equal(960, movida.X);   // pegada al borde derecho de S1
    }

    [Fact]
    public void Move_IgnoraLosBordesDeUnaSalidaDESHABILITADA()
    {
        // Lienzo de 2000 a proposito: asi la mitad (1000) NO coincide con el
        // borde de la salida apagada (950), y la prueba mide lo que dice medir
        // en vez de engancharse a la mitad del lienzo por casualidad.
        var regiones = Cuatro(
            new CanvasRegion(0, 0, 2000, 1080),
            new CanvasRegion(0, 0, 2000, 1080),
            new CanvasRegion(950, 0, 900, 1080));    // S3, apagada
        double s = CanvasEditorLayout.Scale(2000, 1080, 480, 270);

        var movida = CanvasEditorLayout.Move(
            new CanvasRegion(940, 0, 400, 1080), 0, 0,
            regiones, Habilitadas(false, false),
            self: 1, cw: 2000, ch: 1080, scale: s);

        Assert.Equal(940, movida.X);   // no se ha movido: nadie tira de el
    }

    // ── Topes ──────────────────────────────────────────────────────────

    [Fact]
    public void Move_NuncaSaleDelLienzo()
    {
        var regiones = Cuatro();
        double s = CanvasEditorLayout.Scale(1920, 1080, 480, 270);

        var movida = CanvasEditorLayout.Move(
            new CanvasRegion(1000, 500, 400, 300), 5000, 5000,
            regiones, Habilitadas(), self: 1, cw: 1920, ch: 1080, scale: s);

        Assert.Equal(1920 - 400, movida.X);
        Assert.Equal(1080 - 300, movida.Y);
    }

    [Fact]
    public void Resize_NoBajaDelMinimo()
    {
        var regiones = Cuatro();
        double s = CanvasEditorLayout.Scale(1920, 1080, 480, 270);

        var r = CanvasEditorLayout.Resize(
            new CanvasRegion(0, 0, 400, 300), -5000, -5000,
            regiones, Habilitadas(), self: 1, cw: 1920, ch: 1080, scale: s);

        Assert.Equal(CanvasEditorLayout.MinSize, r.W);
        Assert.Equal(CanvasEditorLayout.MinSize, r.H);
    }

    [Fact]
    public void Resize_NoDesbordaElLienzo()
    {
        var regiones = Cuatro();
        double s = CanvasEditorLayout.Scale(1920, 1080, 480, 270);

        var r = CanvasEditorLayout.Resize(
            new CanvasRegion(1000, 500, 400, 300), 5000, 5000,
            regiones, Habilitadas(), self: 1, cw: 1920, ch: 1080, scale: s);

        Assert.Equal(1920 - 1000, r.W);
        Assert.Equal(1080 - 500, r.H);
    }

    [Fact]
    public void Clamp_RegionFueraDeSitio_VuelveDentro()
    {
        var r = CanvasEditorLayout.Clamp(
            new CanvasRegion(-50, -50, 5000, 5000), 1920, 1080);

        Assert.Equal(0, r.X);
        Assert.Equal(0, r.Y);
        Assert.Equal(1920, r.W);
        Assert.Equal(1080, r.H);
    }

    // ── Texto: ida y vuelta con el parser que YA existe ────────────────

    [Fact]
    public void Format_YParse_DanLaVueltaCompleta()
    {
        var original = new CanvasRegion(120, 40, 800, 600);
        string texto = CanvasEditorLayout.Format(original);

        Assert.Equal("120,40,800,600", texto);

        var vuelta = WinCue.Controller.CanvasLayout.ParseRegion(texto, 0, 1920, 1080);
        Assert.Equal(original.X, vuelta.X);
        Assert.Equal(original.Y, vuelta.Y);
        Assert.Equal(original.W, vuelta.W);
        Assert.Equal(original.H, vuelta.H);
    }

    // ── Colores y presets ──────────────────────────────────────────────

    [Fact]
    public void OutputColors_SonLosCuatroDeLaPaleta_EnOrden()
    {
        Assert.Equal(
            new uint[] { Palette.Go, Palette.Info, Palette.Standby, Palette.Magenta },
            CanvasEditorLayout.OutputColors);
    }

    [Fact]
    public void SizePresets_TraenLosCincoDeWinCue()
    {
        var tamanos = CanvasEditorLayout.SizePresets
            .Select(p => (p.W, p.H)).ToArray();

        Assert.Equal(
            new[] { (1920, 1080), (3840, 1080), (5760, 1080), (1920, 2160), (3840, 2160) },
            tamanos);
    }
}
```

- [ ] **Step 2: Ver que fallan**

```powershell
cd C:\Proyectos\ffmpeg-gui\WinCueSharp
dotnet test tests\WinCue.Presentation.Tests\WinCue.Presentation.Tests.csproj -c Release --filter "FullyQualifiedName~CanvasEditorLayoutTests"
```

Esperado: **no compila** — `CanvasEditorLayout` no existe. Ese es el fallo
correcto en C#; no busques un rojo de aserción.

- [ ] **Step 3: Escribir `CanvasEditorLayout`**

Crear `src/WinCue.Presentation/CanvasEditorLayout.cs`:

```csharp
// =============================================================================
// CanvasEditorLayout.cs — El calculo del editor de canvas (Ajustes -> Video)
// =============================================================================
// Estilo Watchout/Resolume: un lienzo global y encima los recortes de las
// cuatro salidas, que se arrastran y se redimensionan con imanes. Aqui esta el
// CALCULO; el dibujo lo pone cada producto (WPF en WinCue.App, WinUI 3 en
// Secuence.App), que es la regla 3 del proyecto: primero se extrae la cuenta y
// se prueba, despues se pinta, y los dos usan la misma.
//
// EL DETALLE QUE SE PIERDE AL COPIAR A OJO: el umbral del iman son 10 px de
// PANTALLA, no de canvas. Se divide por la escala, asi que al alejar el zoom
// el iman agarra desde mas lejos en pixeles del lienzo.
//
// El texto "x,y,w,h" solo se FORMATEA aqui: parsearlo ya lo hace
// WinCue.Controller.CanvasLayout.ParseRegion, que es quien lo lee en el motor.
// Dos parsers distintos del mismo formato es como divergen los productos.
// =============================================================================

using System.Globalization;

namespace WinCue.Presentation;

/// <summary>Recorte de una salida sobre el lienzo, en pixeles de CANVAS.</summary>
public readonly record struct CanvasRegion(int X, int Y, int W, int H);

/// <summary>Rectangulo ya en coordenadas de la VISTA (lo que se pinta).</summary>
public readonly record struct ViewRect(double X, double Y, double W, double H);

public static class CanvasEditorLayout
{
    /// <summary>Lado minimo de un recorte, en pixeles de canvas.</summary>
    public const int MinSize = 16;

    /// <summary>Umbral del iman, en pixeles de PANTALLA. Ver la cabecera.</summary>
    public const double SnapScreenPx = 10.0;

    public const int Outputs = 4;

    /// <summary>Colores de identidad de las cuatro salidas. Los comparten el
    /// editor, el overlay de "Identificar" y el menu del boton de salidas —
    /// estaban duplicados en los dos productos.</summary>
    public static IReadOnlyList<uint> OutputColors { get; } = new[]
    {
        Palette.Go,        // S1 verde
        Palette.Info,      // S2 azul
        Palette.Standby,   // S3 ambar
        Palette.Magenta,   // S4 magenta
    };

    public static IReadOnlyList<(int W, int H, string Label)> SizePresets { get; } = new[]
    {
        (1920, 1080, "1920 × 1080 (FHD)"),
        (3840, 1080, "3840 × 1080 (2×FHD horizontal)"),
        (5760, 1080, "5760 × 1080 (3×FHD horizontal)"),
        (1920, 2160, "1920 × 2160 (2×FHD vertical)"),
        (3840, 2160, "3840 × 2160 (4K)"),
    };

    // ------------------------------------------------------------------
    // Encaje del lienzo en la vista
    // ------------------------------------------------------------------

    /// <summary>Pixeles de vista por pixel de canvas. Deja 4 px de respiro
    /// para que el borde del lienzo no se coma el del control.</summary>
    public static double Scale(int cw, int ch, double viewW, double viewH) =>
        Math.Min((viewW - 4) / Math.Max(1, cw), (viewH - 4) / Math.Max(1, ch));

    /// <summary>Esquina superior izquierda del lienzo dentro de la vista: el
    /// lienzo va CENTRADO, sea cual sea su proporcion.</summary>
    public static (double X, double Y) Origin(int cw, int ch, double viewW, double viewH)
    {
        double s = Scale(cw, ch, viewW, viewH);
        return ((viewW - cw * s) / 2.0, (viewH - ch * s) / 2.0);
    }

    public static ViewRect ToView(CanvasRegion r, int cw, int ch, double viewW, double viewH)
    {
        double s = Scale(cw, ch, viewW, viewH);
        var (ox, oy) = Origin(cw, ch, viewW, viewH);
        return new ViewRect(ox + r.X * s, oy + r.Y * s, r.W * s, r.H * s);
    }

    /// <summary>Un desplazamiento del raton (pixeles de vista) en pixeles de
    /// canvas.</summary>
    public static int ToCanvasDelta(double screenDelta, double scale) =>
        (int)Math.Round(screenDelta / (scale <= 0 ? 1 : scale));

    public static CanvasRegion Clamp(CanvasRegion r, int cw, int ch)
    {
        int x = Math.Clamp(r.X, 0, Math.Max(0, cw - MinSize));
        int y = Math.Clamp(r.Y, 0, Math.Max(0, ch - MinSize));
        int w = Math.Clamp(r.W, MinSize, Math.Max(MinSize, cw - x));
        int h = Math.Clamp(r.H, MinSize, Math.Max(MinSize, ch - y));
        return new CanvasRegion(x, y, w, h);
    }

    // ------------------------------------------------------------------
    // Imanes
    // ------------------------------------------------------------------

    public static IReadOnlyList<int> SnapTargetsX(
        IReadOnlyList<CanvasRegion> regions, IReadOnlyList<bool> enabled, int self, int cw)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(enabled);
        var t = new List<int> { 0, cw / 2, cw };
        for (int i = 0; i < regions.Count && i < Outputs; i++)
        {
            if (i == self || !IsEnabled(enabled, i))
                continue;
            t.Add(regions[i].X);
            t.Add(regions[i].X + regions[i].W);
        }
        return t;
    }

    public static IReadOnlyList<int> SnapTargetsY(
        IReadOnlyList<CanvasRegion> regions, IReadOnlyList<bool> enabled, int self, int ch)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(enabled);
        var t = new List<int> { 0, ch / 2, ch };
        for (int i = 0; i < regions.Count && i < Outputs; i++)
        {
            if (i == self || !IsEnabled(enabled, i))
                continue;
            t.Add(regions[i].Y);
            t.Add(regions[i].Y + regions[i].H);
        }
        return t;
    }

    /// <summary>La Salida 1 SIEMPRE cuenta como habilitada (no se puede
    /// apagar), igual que en el motor.</summary>
    private static bool IsEnabled(IReadOnlyList<bool> enabled, int i) =>
        i == 0 || (i < enabled.Count && enabled[i]);

    private static int SnapThreshold(double scale) =>
        (int)Math.Max(1, SnapScreenPx / (scale <= 0 ? 1 : scale));

    // ------------------------------------------------------------------
    // Mover y redimensionar
    // ------------------------------------------------------------------

    /// <summary>Mueve el recorte <paramref name="orig"/> el desplazamiento
    /// dado (ya en pixeles de canvas), enganchando bordes y aplicando los
    /// topes DESPUES del iman.</summary>
    public static CanvasRegion Move(CanvasRegion orig, int dx, int dy,
        IReadOnlyList<CanvasRegion> regions, IReadOnlyList<bool> enabled,
        int self, int cw, int ch, double scale)
    {
        int x = Math.Clamp(orig.X + dx, 0, Math.Max(0, cw - orig.W));
        int y = Math.Clamp(orig.Y + dy, 0, Math.Max(0, ch - orig.H));
        int snap = SnapThreshold(scale);

        foreach (int t in SnapTargetsX(regions, enabled, self, cw))
        {
            if (Math.Abs(x - t) <= snap) { x = t; break; }
            if (Math.Abs(x + orig.W - t) <= snap) { x = t - orig.W; break; }
        }
        foreach (int t in SnapTargetsY(regions, enabled, self, ch))
        {
            if (Math.Abs(y - t) <= snap) { y = t; break; }
            if (Math.Abs(y + orig.H - t) <= snap) { y = t - orig.H; break; }
        }

        return new CanvasRegion(
            Math.Clamp(x, 0, Math.Max(0, cw - orig.W)),
            Math.Clamp(y, 0, Math.Max(0, ch - orig.H)),
            orig.W, orig.H);
    }

    /// <summary>Redimensiona por la esquina inferior derecha: solo enganchan
    /// el borde derecho y el inferior, y solo si el resultado no baja del
    /// minimo.</summary>
    public static CanvasRegion Resize(CanvasRegion orig, int dx, int dy,
        IReadOnlyList<CanvasRegion> regions, IReadOnlyList<bool> enabled,
        int self, int cw, int ch, double scale)
    {
        int w = Math.Clamp(orig.W + dx, MinSize, Math.Max(MinSize, cw - orig.X));
        int h = Math.Clamp(orig.H + dy, MinSize, Math.Max(MinSize, ch - orig.Y));
        int snap = SnapThreshold(scale);

        foreach (int t in SnapTargetsX(regions, enabled, self, cw))
            if (Math.Abs(orig.X + w - t) <= snap && t - orig.X >= MinSize)
            { w = t - orig.X; break; }
        foreach (int t in SnapTargetsY(regions, enabled, self, ch))
            if (Math.Abs(orig.Y + h - t) <= snap && t - orig.Y >= MinSize)
            { h = t - orig.Y; break; }

        return new CanvasRegion(orig.X, orig.Y,
            Math.Min(w, Math.Max(MinSize, cw - orig.X)),
            Math.Min(h, Math.Max(MinSize, ch - orig.Y)));
    }

    /// <summary>El texto que va a settings. Lo LEE CanvasLayout.ParseRegion.</summary>
    public static string Format(CanvasRegion r) =>
        string.Create(CultureInfo.InvariantCulture, $"{r.X},{r.Y},{r.W},{r.H}");
}
```

- [ ] **Step 4: Ver que pasan**

```powershell
dotnet test tests\WinCue.Presentation.Tests\WinCue.Presentation.Tests.csproj -c Release --filter "FullyQualifiedName~CanvasEditorLayoutTests"
```

Esperado: **16 pruebas en verde**. Si `Move_ElUmbralDelIman_SeMideEnPixelesDePantalla`
falla, casi seguro estás dividiendo el umbral por la escala en el sitio
equivocado (o no lo divides).

- [ ] **Step 5: Suite entera y commit**

```powershell
dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true
dotnet test WinCueSharp.sln -c Release --filter "Requiere!=libmpv"
```

```bash
cd /c/Proyectos/ffmpeg-gui
git add WinCueSharp/src/WinCue.Presentation/CanvasEditorLayout.cs \
        WinCueSharp/tests/WinCue.Presentation.Tests/CanvasEditorLayoutTests.cs
git commit -m "feat(presentation): el calculo del editor de canvas, compartido y con pruebas"
```

---

### Task 2: WinCue pasa a usar el cálculo compartido

**Files:**
- Modify: `src/WinCue.App/CanvasEditorControl.cs`

**Interfaces:**
- Consumes: todo lo que produce la Task 1.
- Produces: nada nuevo. **El comportamiento visible no cambia.**

**Por qué esta tarea existe:** la regla 3 del proyecto. Si WinCue se queda con
su copia, los dos editores divergen a la primera corrección. Y por eso va en
**su propio commit**: es código de un producto que se está vendiendo y tiene
que poder revertirse solo.

- [ ] **Step 1: Sustituir el cálculo, no el dibujo**

En `src/WinCue.App/CanvasEditorControl.cs`:

1. Borrar el array privado `OutColors` (líneas 30-38) y sustituir sus usos por
   `CanvasEditorLayout.OutputColors[i]`, convirtiendo con el mismo helper que
   usa `Theme` para pasar de `uint` a `Color`. **No introduzcas un literal de
   color**: el guardián de paleta lo caza.
2. Borrar `Scale`, `Origin`, `SnapPos`, `SnapSize`, `SnapTargetsX`,
   `SnapTargetsY` y `ClampAll`, y llamar a los de `CanvasEditorLayout`.
3. `_regions[s, i]` (array bidimensional) pasa a `CanvasRegion[] _regions`.
4. La lista de presets del `ComboBox` sale de `CanvasEditorLayout.SizePresets`.
5. `GetRegion(int)` devuelve `CanvasEditorLayout.Format(_regions[s])`;
   `SetRegion(int, string)` usa `CanvasLayout.ParseRegion` y `Clamp`.

**No toques** el `Redraw`, ni la construcción de los `Border`, ni el
`CaptureMouse`, ni los `TextBox`. Es un cambio de *quién hace la cuenta*.

- [ ] **Step 2: Compilar y pasar la suite**

```powershell
dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true
dotnet test WinCueSharp.sln -c Release --filter "Requiere!=libmpv"
pwsh -File WinCueSharp\scripts\verificar-paleta.ps1
```

Esperado: 0 avisos, todo en verde, guardián OK.

- [ ] **Step 3: Commit**

```bash
cd /c/Proyectos/ffmpeg-gui
git add WinCueSharp/src/WinCue.App/CanvasEditorControl.cs
git commit -m "refactor(wincue): el editor de canvas usa el calculo compartido, sin cambiar de aspecto"
```

- [ ] **Step 4: Anotar la verificación en pantalla**

En el informe de la tarea, epígrafe
`## PENDIENTE — verificación en pantalla (Andrés)`:

> Abre **WinCue** → Ajustes → Vídeo. El grupo CANVAS tiene que estar
> **exactamente igual**: mismos colores de S1-S4, mismos presets, arrastrar y
> redimensionar con los mismos imanes (bordes y mitades del lienzo, y bordes
> de las otras salidas).
> ```powershell
> Start-Process "src\WinCue.App\bin\Release\net8.0-windows\WinCue.App.exe"
> ```

---

### Task 3: El editor de canvas y la pestaña de Secuence

**Files:**
- Create: `src/Secuence.App/CanvasEditorPanel.cs`
- Modify: `src/Secuence.App/SettingsPanel.cs:153-192` (`BuildVideo`) y la
  cabecera de alcance del fichero (líneas 46-48, que hoy dicen que el
  compositor y el canvas quedan fuera **a propósito**: ese motivo desaparece)
- Modify: `src/Secuence.App/MainWindow.xaml.cs:2104` (borrar el `OutColors`
  duplicado y usar el compartido)

**Interfaces:**
- Consumes: `CanvasEditorLayout` (Task 1); los helpers privados de
  `SettingsPanel`: `Group(StackPanel parent, string title) → StackPanel`,
  `Check(string key, StackPanel parent, string label) → CheckBox`,
  `Note(StackPanel parent, string text)`, y el registro
  `_fields[key] = (Func<object?> Get, Action<object?> Set)`.
- Produces: `Secuence.App.CanvasEditorPanel` con
  `int CanvasW { get; set; }`, `int CanvasH { get; set; }`,
  `string GetRegion(int s)`, `void SetRegion(int s, string? text)`,
  `bool GetEnabled(int s)`, `void SetEnabled(int s, bool on)`.
  **Mismas firmas que `CanvasEditorControl` de WPF**, para que el registro de
  campos se escriba igual en los dos productos.

- [ ] **Step 1: Escribir `CanvasEditorPanel`**

XAML plano, **sin Win2D**: un `Canvas` con un `Border` por salida. Estructura
obligatoria:

- Fila de tamaño: rótulo `Canvas:`, dos `NumberBox` (ancho y alto) y un
  `ComboBox` de presets que actúa como botón (`SelectedIndex = -1` tras
  elegir).
- El lienzo: `Canvas { Width = 480, Height = 270 }` con fondo
  `Theme.GeometryBgBrush`, un `Rectangle` para el lienzo y cuatro `Border`
  con el color de `CanvasEditorLayout.OutputColors[i]`, rótulo `S1..S4` y un
  asa de 10×10 en la esquina inferior derecha.

  **`Theme.GeometryBgBrush` y `Theme.GeometryGridBrush` NO existen todavía en
  `Secuence.App/Theme.cs`** — comprobado. Los lienzos Win2D de Secuence
  (`GeometryCanvas.cs`) usan `Palette.GeometryBg` / `Palette.GeometryGrid`
  directamente con un `ToColor(uint)` local, pero este editor es XAML plano y
  necesita `Brush`. **Añádelos a `Theme.cs` con el patrón que ese fichero ya
  usa** (`Hex(Palette.GeometryBg)` + `Solid(...)`), respetando la
  correspondencia por NOMBRE que exige el guardián de paleta: el campo de
  `Theme` se llama igual que la constante de `Palette` a la que apunta.
- Debajo: por salida, casilla de habilitada (salvo S1) y cuatro `NumberBox`
  (x, y, w, h).

Reglas que **no** se pueden saltar:

1. **Los mínimos del tema.** A cada `NumberBox` y a cada `CheckBox`,
   `MinWidth = 0` explícito. Si no, `TextBox` reserva 64 px y `CheckBox` 120,
   y todas las cajas salen del mismo ancho.
2. **Arrastre**: `PointerPressed` → `CapturePointer`, `PointerMoved` →
   `CanvasEditorLayout.Move`/`Resize`, `PointerReleased` →
   `ReleasePointerCapture`. **No** uses `SetCursorPos` ni manipulaciones: la
   captura de puntero de WinUI 3 funciona.
3. **Ni un color literal.** Todo por `Theme`, que deriva de `Palette`.
4. **Todo texto por `Loc.T`.**
5. `_loading` como guardián de re-entrada al sincronizar cajas: WinUI relanza
   eventos de valor al realizar el panel.

El enganche del arrastre con el cálculo compartido, que es la parte que no se
puede improvisar:

```csharp
    private int _arrastrando = -1;
    private bool _redimensionando;
    private Point _inicio;
    private CanvasRegion _original;

    private void AlPulsar(int salida, PointerRoutedEventArgs e, bool redimensionar)
    {
        _arrastrando = salida;
        _redimensionando = redimensionar;
        _inicio = e.GetCurrentPoint(_lienzo).Position;
        _original = _regiones[salida];
        _rects[salida].CapturePointer(e.Pointer);
    }

    private void AlMover(object sender, PointerRoutedEventArgs e)
    {
        if (_arrastrando < 0)
            return;

        var p = e.GetCurrentPoint(_lienzo).Position;
        double escala = CanvasEditorLayout.Scale(_cw, _ch, AnchoVista, AltoVista);
        int dx = CanvasEditorLayout.ToCanvasDelta(p.X - _inicio.X, escala);
        int dy = CanvasEditorLayout.ToCanvasDelta(p.Y - _inicio.Y, escala);

        _regiones[_arrastrando] = _redimensionando
            ? CanvasEditorLayout.Resize(_original, dx, dy, _regiones, _habilitadas,
                                        _arrastrando, _cw, _ch, escala)
            : CanvasEditorLayout.Move(_original, dx, dy, _regiones, _habilitadas,
                                      _arrastrando, _cw, _ch, escala);
        Redibujar();
    }

    private void AlSoltar(object sender, PointerRoutedEventArgs e)
    {
        if (_arrastrando >= 0)
            _rects[_arrastrando].ReleasePointerCapture(e.Pointer);
        _arrastrando = -1;
        Redibujar();
    }
```

**`_original` es la región de cuando EMPEZÓ el arrastre, no la de ahora.** Si
acumulas deltas sobre la región en curso, el imán se pelea consigo mismo y el
recorte se va solo.

- [ ] **Step 2: Añadir el grupo a la pestaña Vídeo**

En `BuildVideo()` de `SettingsPanel.cs`, **después** del grupo de salidas y
**antes** de `MOTOR ProRes` (el orden de WinCue):

```csharp
Check("video_compositor", scr, Loc.T(
    "Compositor GPU (opacidad real entre capas)",
    "GPU compositor (real per-layer opacity)"));
Note(scr, Loc.T(
    "Las pistas se funden según su Opacidad % (Geometría). Requiere driver con interop OpenGL↔DirectX; si falla, cae al modo clásico.",
    "Layers blend by their Opacity % (Geometry). Requires a driver with OpenGL↔DirectX interop; if it fails, it falls back to classic mode."));

var canvas = Group(root, Loc.T(
    "CANVAS (las salidas recortan un lienzo común)",
    "CANVAS (outputs crop a shared canvas)"));
Check("video_canvas_mode", canvas, Loc.T(
    "Modo canvas: los cues se colocan en el canvas y cada salida muestra su recorte",
    "Canvas mode: cues are placed on the canvas and each output shows its crop"));
Note(canvas, Loc.T(
    "Requiere el compositor GPU. Un vídeo puede cruzar varias salidas con sincronía perfecta (un solo render). En este modo la «Salida» del inspector se ignora: el vídeo vive en el canvas.",
    "Requires the GPU compositor. A video can span several outputs with perfect sync (a single render). In this mode the inspector's «Output» is ignored: the video lives on the canvas."));

var editor = new CanvasEditorPanel { Margin = new Thickness(0, 6, 0, 0) };
canvas.Children.Add(editor);
_fields["surface_width"] = (
    () => editor.CanvasW,
    v => editor.CanvasW = v is null ? 1920 : Convert.ToInt32(v));
_fields["surface_height"] = (
    () => editor.CanvasH,
    v => editor.CanvasH = v is null ? 1080 : Convert.ToInt32(v));
for (int s = 0; s < 4; s++)
{
    int idx = s;
    string regionKey = s == 0 ? "video_output_region" : $"video_output_region{s + 1}";
    _fields[regionKey] = (
        () => editor.GetRegion(idx),
        v => editor.SetRegion(idx, v?.ToString()));
    if (s > 0)
        _fields[$"video_output_enabled{s + 1}"] = (
            () => editor.GetEnabled(idx),
            v => editor.SetEnabled(idx, AsBool(v)));
}
```

Y **actualiza la cabecera del fichero**: donde hoy dice que
`video_compositor`/canvas quedan «FUERA a propósito, todavía: sin
GetCompositorSurface conectado», escribe que llegan con esta tanda y por qué.
Un comentario que miente es peor que no tenerlo.

- [ ] **Step 3: Borrar el `OutColors` duplicado de Secuence**

En `MainWindow.xaml.cs:2104`, sustituir el array privado por
`CanvasEditorLayout.OutputColors`, convertido a `Windows.UI.Color` con el
mismo helper que ya usa `Theme`.

- [ ] **Step 4: Compilar, pasar la suite y el guardián**

```powershell
Stop-Process -Name Secuence -Force -ErrorAction SilentlyContinue
cd C:\Proyectos\ffmpeg-gui\WinCueSharp
dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true
dotnet test WinCueSharp.sln -c Release --filter "Requiere!=libmpv"
pwsh -File scripts\verificar-paleta.ps1
```

- [ ] **Step 5: Commit**

```bash
cd /c/Proyectos/ffmpeg-gui
git add WinCueSharp/src/Secuence.App/CanvasEditorPanel.cs \
        WinCueSharp/src/Secuence.App/SettingsPanel.cs \
        WinCueSharp/src/Secuence.App/MainWindow.xaml.cs
git commit -m "feat(secuence): el editor de canvas en Ajustes, con el calculo compartido"
```

- [ ] **Step 6: Anotar la verificación en pantalla**

> Secuence → Ajustes → Vídeo. Compara con WinCue lado a lado: mismo orden de
> grupos, mismos textos, mismos anchos. Arrastra un recorte y comprueba los
> imanes; cambia el preset a `5760 × 1080` y mira que el lienzo se reencaja
> centrado. **El modo canvas todavía no pinta nada distinto** — eso llega en
> la Task 7.

---

### Task 4: El puente — `SharedSurfaceSource`

**Files:**
- Create: `src/WinCue.Video/Gpu/SharedSurfaceSource.cs`
- Modify: `src/WinCue.Video/Gpu/FrameRing.cs:36-65` (constructor de
  `FrameLease` sin anillo)
- Test: `tests/WinCue.Video.Tests/SharedSurfaceSourceTests.cs`

**Interfaces:**
- Consumes: `GpuDevice` (`.Device`, `.Context`, `.Shared`),
  `IVideoSource`, `FrameLease`, `VideoSourceStats`,
  `D3D9Minimal.CreateDevice(nint)` y
  `D3D9Minimal.CreateSharedRenderTarget(nint device, int w, int h) → (nint Surface, nint SharedHandle)`
  (internos de `WinCue.Video`; el proyecto de pruebas los ve por
  `InternalsVisibleTo`), `GpuReadback.Read(GpuDevice, ID3D11Texture2D, int, int) → byte[]`.
- Produces:
  - `internal FrameLease(ID3D11ShaderResourceView srv, ID3D11Texture2D texture, double pts)`
    — préstamo **sin anillo**; `Dispose` ya es null-safe (`_ring?.Release`).
  - `public sealed class SharedSurfaceSource : IVideoSource` con
    `SharedSurfaceSource(GpuDevice gpu, nint sharedHandle, int width, int height)`,
    `Action? FrameCopier { get; set; }`, `void MarkFrameReady()`.

**El contrato de hilos, que es lo que puede salir mal:**
`MarkFrameReady()` lo llama el hilo de render de mpv y **solo levanta una
bandera** — nada más, nada bloqueante. `TryAcquire()` lo llama el hilo del
compositor, y es el único que invoca `FrameCopier` (el `StretchRect`
interop→presentación). Un solo hilo copia y muestrea: eso es lo que en WPF
garantizaba el `Lock` del `D3DImage`.

- [ ] **Step 1: Escribir la prueba que falla**

Crear `tests/WinCue.Video.Tests/SharedSurfaceSourceTests.cs`:

```csharp
// =============================================================================
// SharedSurfaceSourceTests.cs — El puente D3D9Ex -> D3D11, por pixel
// =============================================================================
// Esta es LA prueba que decide si el camino existe: una superficie compartida
// de D3D9Ex abierta en D3D11 y dibujada por el compositor. Si esto pasa, el
// modo canvas de Secuence es fontaneria; si no, no hay diseno.
//
// Misma convencion que CompositorTests y GpuSourceTests: sin GPU utilizable no
// se afirma nada, en vez de mentir.
// =============================================================================

using WinCue.Video.Gpu;

namespace WinCue.Video.Tests;

public class SharedSurfaceSourceTests
{
    private static GpuDevice? TryCreateGpu()
    {
        try { return new GpuDevice(); }
        catch (Exception) { return null; }
    }

    [Fact]
    public void SuperficieCompartidaD3D9_SeAbreEnD3D11_YSeDibuja()
    {
        using var gpu = TryCreateGpu();
        if (gpu is null)
            return;   // sin GPU utilizable: no se afirma nada

        nint d3d9 = 0;
        try { d3d9 = D3D9Minimal.CreateDevice(0); }
        catch (Exception) { return; }   // sin D3D9Ex tampoco se afirma

        try
        {
            var (surface, shared) = D3D9Minimal.CreateSharedRenderTarget(d3d9, 64, 64);
            try
            {
                // Pintar la superficie D3D9 de un color conocido: azul puro.
                D3D9Minimal.ColorFill(d3d9, surface, 0x000000FF);

                using var source = new SharedSurfaceSource(gpu, shared, 64, 64);
                source.MarkFrameReady();

                using var sink = new TextureSink(gpu, 64, 64);
                using var comp = new OutputCompositor(gpu, sink);
                comp.Root = new SourceNode(source);
                comp.RenderOnce(0.0);

                byte[] bgra = GpuReadback.Read(gpu, sink.Texture, 64, 64);

                // BGRA8: el pixel central tiene que ser azul.
                int mid = ((32 * 64) + 32) * 4;
                Assert.True(bgra[mid + 0] > 200, $"B={bgra[mid + 0]}");
                Assert.True(bgra[mid + 1] < 60, $"G={bgra[mid + 1]}");
                Assert.True(bgra[mid + 2] < 60, $"R={bgra[mid + 2]}");
            }
            finally
            {
                D3D9Minimal.Release(surface);
            }
        }
        finally
        {
            D3D9Minimal.Release(d3d9);
        }
    }

    [Fact]
    public void SinFotogramaPublicado_NoEntregaPrestamo()
    {
        using var gpu = TryCreateGpu();
        if (gpu is null)
            return;

        nint d3d9 = 0;
        try { d3d9 = D3D9Minimal.CreateDevice(0); }
        catch (Exception) { return; }

        try
        {
            var (surface, shared) = D3D9Minimal.CreateSharedRenderTarget(d3d9, 32, 32);
            try
            {
                using var source = new SharedSurfaceSource(gpu, shared, 32, 32);

                Assert.False(source.HasFrames);
                Assert.False(source.TryAcquire(0.0, out FrameLease lease));
                lease.Dispose();
            }
            finally { D3D9Minimal.Release(surface); }
        }
        finally { D3D9Minimal.Release(d3d9); }
    }

    [Fact]
    public void ElCopiador_LoLlamaElCONSUMIDOR_NoElProductor()
    {
        using var gpu = TryCreateGpu();
        if (gpu is null)
            return;

        nint d3d9 = 0;
        try { d3d9 = D3D9Minimal.CreateDevice(0); }
        catch (Exception) { return; }

        try
        {
            var (surface, shared) = D3D9Minimal.CreateSharedRenderTarget(d3d9, 32, 32);
            try
            {
                int copias = 0;
                using var source = new SharedSurfaceSource(gpu, shared, 32, 32)
                {
                    FrameCopier = () => copias++,
                };

                source.MarkFrameReady();
                Assert.Equal(0, copias);   // publicar NO copia

                Assert.True(source.TryAcquire(0.0, out FrameLease lease));
                lease.Dispose();
                Assert.Equal(1, copias);   // muestrear SI copia
            }
            finally { D3D9Minimal.Release(surface); }
        }
        finally { D3D9Minimal.Release(d3d9); }
    }
}
```

**Nota:** `D3D9Minimal.ColorFill` no existe todavía; se añade en el paso 3
junto al resto. Es un `IDirect3DDevice9Ex::ColorFill` (índice 42 de la vtbl de
`IDirect3DDevice9`), del mismo estilo que los helpers que ya hay en
`GlDxInterop.cs`.

- [ ] **Step 2: Ver que falla**

```powershell
cd C:\Proyectos\ffmpeg-gui\WinCueSharp
dotnet test tests\WinCue.Video.Tests\WinCue.Video.Tests.csproj -c Release --filter "FullyQualifiedName~SharedSurfaceSourceTests"
```

Esperado: no compila — no existen `SharedSurfaceSource` ni
`D3D9Minimal.ColorFill`.

- [ ] **Step 3: Implementar**

1. En `FrameRing.cs`, junto al constructor interno que ya existe, añadir:

```csharp
    /// <summary>Prestamo SIN anillo: un unico recurso que vive fuera (una
    /// superficie compartida de otro device). Dispose es no-op porque no hay
    /// slot que devolver — _ring queda null y Dispose ya es null-safe.</summary>
    internal FrameLease(ID3D11ShaderResourceView srv, ID3D11Texture2D texture, double pts)
    {
        _ring = null;
        _index = -1;
        View = srv;
        Texture = texture;
        Pts = pts;
    }
```

2. En `GlDxInterop.cs`, junto a `CreateSharedRenderTarget`, añadir `ColorFill`
   (vtbl `IDirect3DDevice9`, índice 42; firma
   `HRESULT ColorFill(IDirect3DSurface9*, const RECT*, D3DCOLOR)`), siguiendo
   el mismo patrón `Method<TFn>(device, Vtbl…)` de los helpers vecinos.

3. Crear `src/WinCue.Video/Gpu/SharedSurfaceSource.cs`:

```csharp
// =============================================================================
// SharedSurfaceSource.cs — El puente: superficie compartida D3D9Ex -> D3D11
// =============================================================================
// Entra por donde YA sale la imagen. MpvGpuRenderer crea una superficie
// compartida D3D9Ex por recorte y copia en ella el fotograma completo
// (StretchRect desde el FBO privado); esta clase abre ESA superficie como
// textura D3D11 con OpenSharedResource y la publica al grafo. Ni mpv ni el
// interop GL se enteran.
//
// POR QUE NO HAY ANILLO NI PTS: el recurso es UNO, ajeno, y lo escribe el
// productor de siempre. Esta fuente entrega "el ultimo fotograma copiado", que
// es exactamente lo que hoy hace el DWM con el D3DImage. El PTS de verdad
// llega con MpvSource (la ruta nativa), no con el puente.
//
// HILOS — el punto que puede romperse:
//   · MarkFrameReady() lo llama el hilo de render de mpv. SOLO levanta una
//     bandera. Nada lo bloquea (regla 4 del plan del compositor).
//   · TryAcquire() lo llama el hilo del compositor, y es el UNICO que invoca
//     FrameCopier — el StretchRect interop->presentacion. Un solo hilo copia y
//     muestrea: eso es lo que garantizaba el Lock del D3DImage en WPF.
// =============================================================================

using Vortice.Direct3D11;

namespace WinCue.Video.Gpu;

public sealed class SharedSurfaceSource : IVideoSource
{
    private readonly ID3D11Texture2D _texture;
    private readonly ID3D11ShaderResourceView _srv;
    private volatile bool _hasFrame;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public bool HasFrames => _hasFrame;
    public VideoSourceStats Stats { get; } = new();

    /// <summary>Copia interop -> presentacion. La invoca el CONSUMIDOR (el
    /// hilo del compositor), nunca el productor. Ver la cabecera.</summary>
    public Action? FrameCopier { get; set; }

    public SharedSurfaceSource(GpuDevice gpu, nint sharedHandle, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        if (sharedHandle == 0)
            throw new ArgumentException("Handle compartido no valido.", nameof(sharedHandle));

        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        // El handle es el de una superficie D3D9Ex creada como compartida:
        // D3DFMT_A8R8G8B8 se ve desde D3D11 como B8G8R8A8_UNORM.
        _texture = gpu.Device.OpenSharedResource<ID3D11Texture2D>(sharedHandle);
        try
        {
            _srv = gpu.Device.CreateShaderResourceView(_texture);
        }
        catch
        {
            _texture.Dispose();
            throw;
        }
    }

    /// <summary>Hay un fotograma nuevo esperando en la superficie del interop.
    /// HILO DE RENDER DE MPV: solo la bandera.</summary>
    public void MarkFrameReady() => _hasFrame = true;

    public bool TryAcquire(double mediaTime, out FrameLease lease)
    {
        if (_disposed || !_hasFrame)
        {
            Stats.Starvations++;
            lease = default;
            return false;
        }

        // La copia va AQUI, en el hilo que muestrea, justo antes de dibujar.
        FrameCopier?.Invoke();
        Stats.Published++;

        lease = new FrameLease(_srv, _texture, mediaTime);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _srv.Dispose();
        _texture.Dispose();
    }
}
```

- [ ] **Step 4: Ver que pasan**

```powershell
dotnet test tests\WinCue.Video.Tests\WinCue.Video.Tests.csproj -c Release --filter "FullyQualifiedName~SharedSurfaceSourceTests"
```

Esperado: **3 en verde** en esta máquina (tiene GPU). Si las tres se saltan sin
afirmar nada, el resultado **no vale**: comprueba que `TryCreateGpu` devuelve
device de verdad antes de dar la tarea por hecha — es la trampa que la
cabecera de `GpuSourceTests` ya avisa.

- [ ] **Step 5: Commit**

```bash
cd /c/Proyectos/ffmpeg-gui
git add WinCueSharp/src/WinCue.Video/Gpu/SharedSurfaceSource.cs \
        WinCueSharp/src/WinCue.Video/Gpu/FrameRing.cs \
        WinCueSharp/src/WinCue.Video/GlDxInterop.cs \
        WinCueSharp/tests/WinCue.Video.Tests/SharedSurfaceSourceTests.cs
git commit -m "feat(video): puente D3D9Ex->D3D11, verificado por pixel"
```

---

### Task 5: El handle compartido llega hasta el sink

**Files:**
- Modify: `src/WinCue.Video/MpvGpuRenderer.cs:61-76, 210-220`
- Modify: `src/WinCue.Core/MediaEngineContracts.cs:215-244`
- Modify: `src/WinCue.Video/VideoEngine.cs:1529, 1711, 1920`

**Interfaces:**
- Produces:
  - `MpvGpuRenderer.ViewSharedHandle(int i) → nint` y
    `nint PresentSharedHandle => ViewSharedHandle(0)`.
  - `IVideoLayerSurface.SetSharedHandle(nint handle) { }` — **cuerpo vacío por
    defecto**. `CompositorLayerControl` de WinCue no lo implementa y no se
    entera; `CapaCompositor` de Secuence (Task 7) sí.
  - `ProResGpuPipeline` y `HapGpuPipeline`: la misma propiedad, para que los
    tres sitios de `VideoEngine` puedan llamarla igual.

- [ ] **Step 1: Guardar el handle en `MpvGpuRenderer`**

Hoy, en `RenderThread` (línea ~217), el handle de la superficie de
presentación **se tira**:

```csharp
var (present, _) = D3D9Minimal.CreateSharedRenderTarget(_d3dDevice, v.W, v.H);
_presentSurfaces[i] = present;
```

Pasa a guardarse en un array paralelo `_presentShared[i]`, y se expone junto a
`ViewSurface`:

```csharp
    /// <summary>Handle COMPARTIDO de la superficie de presentacion de la vista
    /// i. Es lo que permite abrirla desde otro device (D3D11) sin tocar la del
    /// interop — la del interop es de GL mientras tiene el lock, y mirarla
    /// fugaba VRAM (auditoria 05).</summary>
    public nint ViewSharedHandle(int i) => _presentShared[i];
```

Añade la misma propiedad a `HapGpuPipeline` y a `ProResGpuPipeline`, que ya
tienen `PresentSurfacePointer`: llámala `PresentSharedHandle`.

- [ ] **Step 2: Ampliar el contrato**

En `MediaEngineContracts.cs`, dentro de `IVideoLayerSurface`, junto a
`AssignCue`/`ReleaseCue`:

```csharp
    /// <summary>Handle COMPARTIDO de la superficie, para quien no pueda
    /// hospedarla directamente y tenga que abrirla en otro device (Secuence:
    /// WinUI 3 no tiene D3DImage). Cuerpo vacio a proposito: el control de
    /// WinCue no lo necesita y no debe enterarse de que esto existe.</summary>
    void SetSharedHandle(nint sharedHandle) { }
```

- [ ] **Step 3: Llamarlo en los tres sitios**

En `VideoEngine.cs`, justo después de cada `sink.SetSurface(...)`:

- Línea ~1529 (pool de mpv, dentro del bucle de vistas):
  `sink.SetSharedHandle(pipeline.ViewSharedHandle(i));`
- Línea ~1711 (pool de HAP): `sink.SetSharedHandle(pipeline.PresentSharedHandle);`
- Línea ~1920 (pool de ProRes): `sink.SetSharedHandle(pipeline.PresentSharedHandle);`

**El orden importa**: `SetSurface` primero (fija tamaño), `SetSharedHandle`
después.

- [ ] **Step 4: Compilar y pasar la suite entera**

```powershell
dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true
dotnet test WinCueSharp.sln -c Release --filter "Requiere!=libmpv"
```

Esperado: 0 avisos y **todo en verde, sin una sola prueba nueva en rojo**.
Esta tarea no cambia comportamiento: si algo se pone rojo, el añadido no era
aditivo.

- [ ] **Step 5: Commit**

```bash
cd /c/Proyectos/ffmpeg-gui
git add WinCueSharp/src/WinCue.Video/MpvGpuRenderer.cs \
        WinCueSharp/src/WinCue.Video/VideoEngine.cs \
        WinCueSharp/src/WinCue.Core/MediaEngineContracts.cs
git commit -m "feat(video): el handle compartido llega al sink, con cuerpo vacio por defecto"
```

---

### Task 6: El sink secundario del compositor (el espejo)

**Files:**
- Modify: `src/WinCue.Video/Gpu/OutputCompositor.cs`
- Test: `tests/WinCue.Video.Tests/CompositorTests.cs` (añadir, no reescribir)

**Interfaces:**
- Produces: `OutputCompositor.Mirror` —
  `public IOutputSink? Mirror { get; set; }` y
  `public Func<GraphNode?>? MirrorRoot { get; set; }`.
  Si `Mirror` no es null, cada fotograma se dibuja **también** en él, en el
  mismo hilo y en el mismo instante, con `MirrorRoot` si está puesto y con la
  raíz normal si no. El espejo **no** llama a `Present()` del sink principal
  ni al revés: cada sink presenta el suyo.

**Por qué en el mismo hilo:** el PGM no puede tener hilo propio. Dos hilos
dibujando los mismos nodos, con los mismos préstamos y el mismo copiador,
serían una carrera sobre la superficie de presentación. En el mismo fotograma
no hay nada que sincronizar.

- [ ] **Step 1: Escribir la prueba que falla**

Añadir a `tests/WinCue.Video.Tests/CompositorTests.cs`:

```csharp
    [Fact]
    public void SinkSecundario_RecibeElMISMOFotograma()
    {
        using var gpu = TryCreateGpu();
        if (gpu is null)
            return;

        using var principal = new TextureSink(gpu, W, H);
        using var espejo = new TextureSink(gpu, W, H);
        using var comp = new OutputCompositor(gpu, principal) { Mirror = espejo };

        using var rojo = new SolidNode(gpu, 1f, 0f, 0f);
        comp.Root = rojo;
        comp.RenderOnce(0.0);

        byte[] a = GpuReadback.Read(gpu, principal.Texture, W, H);
        byte[] b = GpuReadback.Read(gpu, espejo.Texture, W, H);

        Assert.Equal(GpuReadback.MeanLumaBgra8(a), GpuReadback.MeanLumaBgra8(b), 3);
    }

    [Fact]
    public void SinEspejo_ElCompositorSigueFuncionandoIgual()
    {
        using var gpu = TryCreateGpu();
        if (gpu is null)
            return;

        using var principal = new TextureSink(gpu, W, H);
        using var comp = new OutputCompositor(gpu, principal);

        Assert.Null(comp.Mirror);

        using var rojo = new SolidNode(gpu, 1f, 0f, 0f);
        comp.Root = rojo;
        comp.RenderOnce(0.0);

        byte[] a = GpuReadback.Read(gpu, principal.Texture, W, H);
        Assert.True(GpuReadback.MeanLumaBgra8(a) > 10.0);
    }
```

- [ ] **Step 2: Ver que falla**

```powershell
dotnet test tests\WinCue.Video.Tests\WinCue.Video.Tests.csproj -c Release --filter "FullyQualifiedName~CompositorTests"
```

Esperado: no compila — `Mirror` no existe.

- [ ] **Step 3: Implementar**

En `OutputCompositor.cs`:

```csharp
    /// <summary>Sink SECUNDARIO opcional: se dibuja en el MISMO fotograma y en
    /// el MISMO hilo que el principal. Es lo que hace del PGM un espejo del
    /// compositor en vez de un segundo decodificador. Dos hilos dibujando los
    /// mismos nodos serian una carrera sobre la superficie de presentacion;
    /// en el mismo fotograma no hay nada que sincronizar.</summary>
    public IOutputSink? Mirror { get; set; }

    /// <summary>Raiz alternativa para el espejo (el PGM en modo LIENZO
    /// reconstruye el lienzo entero). Si es null, el espejo pinta la raiz
    /// normal.</summary>
    public Func<GraphNode?>? MirrorRoot { get; set; }
```

En `DrawFrame`, tras dibujar el principal, dibujar el espejo con su propia
`LayerTarget` (sus dimensiones son las suyas, no las del principal), y en
`RenderLoop`, tras `_sink.Present()`, llamar a `Mirror?.Present()`.
`Dispose` **no** libera el espejo: no es suyo.

- [ ] **Step 4: Ver que pasan y commit**

```powershell
dotnet test tests\WinCue.Video.Tests\WinCue.Video.Tests.csproj -c Release --filter "FullyQualifiedName~CompositorTests"
dotnet test WinCueSharp.sln -c Release --filter "Requiere!=libmpv"
```

```bash
cd /c/Proyectos/ffmpeg-gui
git add WinCueSharp/src/WinCue.Video/Gpu/OutputCompositor.cs \
        WinCueSharp/tests/WinCue.Video.Tests/CompositorTests.cs
git commit -m "feat(video): sink secundario del compositor, en el mismo fotograma"
```

---

### Task 7: El compositor vivo en Secuence

**Files:**
- Create: `src/Secuence.App/SalidaGpu.cs`
- Create: `src/Secuence.App/CapaCompositor.cs`
- Modify: `src/Secuence.App/VideoOutputWindow.xaml.cs`
- Modify: `src/Secuence.App/MainWindow.xaml.cs`
- Modify: `src/Secuence.App/App.xaml.cs:147-160`

**Interfaces:**
- Consumes: `GpuDevice.Shared`, `SwapChainSink(GpuDevice, nint hwnd, int w, int h)`,
  `OutputCompositor(GpuDevice, IOutputSink, Func<double>?)` con `.Mutate`,
  `.Root`, `.Start()`, `.Mirror`; `StackNode.Children`; `SourceNode(IVideoSource)`;
  `TransformNode(GraphNode) { Dest, Uv }`; `Ramp`; `SharedSurfaceSource`
  (Task 4); `IVideoLayerSurface.SetSharedHandle` (Task 5);
  `VideoOutputWindow.ChildHandle` y `EnsureVisibleHandle()`.
- Produces:
  - `Secuence.App.SalidaGpu` con
    `SalidaGpu(nint hwnd, int width, int height)`,
    `CapaCompositor NuevaCapa(int layer, int slotSeq)`,
    `void FijarOpacidad(int cueId, double opacidad, double fadeSegundos)`,
    `IReadOnlyList<CapaCompositor> Capas { get; }`,
    `OutputCompositor Compositor { get; }`, `void Dispose()`.
  - `Secuence.App.CapaCompositor : IVideoLayerSurface` con
    `int Layer { get; }`, `int? CueId { get; }`,
    `SharedSurfaceSource? Fuente { get; }`, `GraphNode? Nodo { get; }`.

**Ciclo de vida, en una frase:** `SalidaGpu` nace **perezosamente** la primera
vez que un cue pide superficie en esa salida, y muere con la ventana.

- [ ] **Step 1: Escribir `CapaCompositor`**

Implementa `IVideoLayerSurface`. Reglas:

- `SetSurface(nint surface, int w, int h)` — solo guarda tamaño. **El puntero
  D3D9 no se usa**: lo que sirve es el handle.
- `SetSharedHandle(nint h)` — crea el `SharedSurfaceSource` sobre
  `GpuDevice.Shared` y lo mete en un `SourceNode`. Si falla, deja la capa
  muerta y **lo señala** (ver el paso 4): el motor caerá a la ruta clásica.
- `SetFrameCopier(Action copy)` — se lo pasa a `Fuente.FrameCopier`. **No lo
  llames tú**: lo llama el compositor al muestrear.
- `InvalidateFrame()` — **hilo de mpv**: solo `Fuente?.MarkFrameReady()`.
- `AssignCue(int id)` / `ReleaseCue(int id)` — añadir o quitar el nodo de la
  `StackNode` de su salida, **siempre dentro de `Compositor.Mutate(...)`**.
- `ClearSurface()` — quitar el nodo y liberar la fuente.

Es la clase donde más fácil es equivocarse de hilo, así que va entera:

```csharp
// =============================================================================
// CapaCompositor.cs — Una capa del grafo, vista como IVideoLayerSurface
// =============================================================================
// Lo que VideoEngine cree que le entrega la interfaz: una superficie donde
// pintar una pista. En WPF eso es un D3DImage (CompositorLayerControl); aqui
// no hay D3DImage, asi que es un NODO del grafo de su salida.
//
// HILOS — leelo antes de tocar nada:
//   · SetSurface / SetSharedHandle / SetFrameCopier / AssignCue / ReleaseCue /
//     ClearSurface: hilo principal.
//   · InvalidateFrame: HILO DE RENDER DE MPV. Solo levanta la bandera de la
//     fuente. Nada mas. Nada bloqueante.
//   · El copiador NO se llama aqui: lo llama el compositor al muestrear, en su
//     hilo. Un solo hilo copia y muestrea — eso es lo que garantizaba el Lock
//     del D3DImage en WPF.
// =============================================================================

using WinCue.Core;
using WinCue.Video.Gpu;

namespace Secuence.App;

internal sealed class CapaCompositor : IVideoLayerSurface, IDisposable
{
    private readonly SalidaGpu _salida;
    private SharedSurfaceSource? _fuente;
    private SourceNode? _nodo;
    private Action? _copiador;
    private int _ancho, _alto;
    private bool _enElGrafo;

    public CapaCompositor(SalidaGpu salida, int layer)
    {
        _salida = salida ?? throw new ArgumentNullException(nameof(salida));
        Layer = layer;
    }

    public int Layer { get; }
    public int? CueId { get; private set; }
    public SharedSurfaceSource? Fuente => _fuente;
    public GraphNode? Nodo => _nodo;

    /// <summary>Falso si el puente no se pudo montar: el motor lo trata como
    /// compositor no disponible y cae a la ruta clasica.</summary>
    public bool Viva => _fuente is not null;

    // ── IVideoLayerSurface ────────────────────────────────────────────

    /// <summary>El puntero D3D9 NO se usa: lo que sirve es el handle. Aqui
    /// solo se apunta el tamano.</summary>
    public void SetSurface(nint d3d9Surface, int width, int height)
    {
        _ancho = Math.Max(1, width);
        _alto = Math.Max(1, height);
    }

    public void SetSharedHandle(nint sharedHandle)
    {
        LiberarFuente();
        try
        {
            _fuente = new SharedSurfaceSource(
                GpuDevice.Shared, sharedHandle, _ancho, _alto)
            {
                FrameCopier = _copiador,
            };
            _nodo = new SourceNode(_fuente);
        }
        catch (Exception exc)
        {
            // Sin puente no hay capa. VideoEngine lo vera como compositor no
            // disponible y caera al HWND clasico, que es el comportamiento
            // correcto: mejor video por la ruta vieja que ninguno.
            _fuente = null;
            _nodo = null;
            _salida.Avisar($"[AVISO] No se pudo abrir la superficie compartida ({exc.Message}).");
        }
    }

    public void SetFrameCopier(Action copyFrame)
    {
        _copiador = copyFrame;
        if (_fuente is not null)
            _fuente.FrameCopier = copyFrame;
    }

    /// <summary>HILO DE RENDER DE MPV. Solo la bandera.</summary>
    public void InvalidateFrame() => _fuente?.MarkFrameReady();

    public void AssignCue(int cueId)
    {
        CueId = cueId;
        if (_nodo is null || _enElGrafo)
            return;
        _salida.Insertar(this);
        _enElGrafo = true;
    }

    public void ReleaseCue(int cueId)
    {
        if (CueId != cueId)
            return;
        CueId = null;
        if (!_enElGrafo)
            return;
        _salida.Quitar(this);
        _enElGrafo = false;
    }

    public void ClearSurface()
    {
        if (_enElGrafo)
        {
            _salida.Quitar(this);
            _enElGrafo = false;
        }
        CueId = null;
        LiberarFuente();
    }

    private void LiberarFuente()
    {
        _nodo = null;
        _fuente?.Dispose();
        _fuente = null;
    }

    public void Dispose() => ClearSurface();
}
```

`SalidaGpu.Insertar` / `Quitar` / `Avisar` se escriben en el paso siguiente.
`Insertar` y `Quitar` **tienen que envolver la mutación en
`Compositor.Mutate(...)`** — tocar `StackNode.Children` fuera de ahí es una
carrera contra el hilo de render, y su propio comentario lo dice.

- [ ] **Step 2: Escribir `SalidaGpu`**

- Un `SwapChainSink` sobre el HWND hijo y un `OutputCompositor` sobre
  `GpuDevice.Shared`; `Start()` al crearse.
- Una `StackNode` como raíz. **El orden de los hijos es el orden de pista**:
  al insertar una capa, colócala por `Layer` (0 arriba tapa; la lista se
  recorre en orden y el último dibujado queda encima, así que **inserta por
  `Layer` descendente**).
- `FijarOpacidad(cueId, op, fade)` busca las capas de ese cue y, dentro de
  `Mutate`, les pone la opacidad — con `Ramp` si `fade > 0`.
- Al redimensionar la ventana (`OnAppWindowChanged` ya existe), **recrear el
  swapchain**: es el riesgo nº 2 del diseño. Recrear, no redimensionar a mano.

- [ ] **Step 3: Enrutar y enganchar**

- `VideoOutputWindow` gana `SalidaGpu ObtenerSalidaGpu()` (perezosa, sobre
  `EnsureVisibleHandle()`) y `CapaCompositor NuevaCapa(int layer, int seq)`.
- `MainWindow` gana
  `public IVideoLayerSurface ObtenerSuperficieCompositor(int screen, int layer, int seq)`,
  que enruta a `_videoOutputs[screen]`, **con la misma resolución de índice de
  pantalla que ya usa** (ver `ResolveDisplayArea`).
- `MainWindow` implementa por fin
  `public void SetVideoCueOpacity(int cueId, double opacity, double fadeSeconds = 0.0)`
  reenviando a la `SalidaGpu` de cada salida.
- `App.xaml.cs`, junto a donde hoy se crea el `VideoEngine`:

```csharp
        video.SetGpuLayerSurfaceProvider(
            (screen, layer, seq) => window.ObtenerSuperficieCompositor(screen, layer, seq));
```

- [ ] **Step 4: Compilar, suite, guardián**

```powershell
Stop-Process -Name Secuence -Force -ErrorAction SilentlyContinue
cd C:\Proyectos\ffmpeg-gui\WinCueSharp
dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true
dotnet test WinCueSharp.sln -c Release --filter "Requiere!=libmpv"
pwsh -File scripts\verificar-paleta.ps1
```

- [ ] **Step 5: Probarlo de verdad, con el show de prueba**

```powershell
$exe = Get-ChildItem "src\Secuence.App\bin" -Recurse -Filter "Secuence.exe" |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
Start-Process $exe.FullName -ArgumentList "tests\show-prueba\show-prueba.json"
```

Enciende **Ajustes → Vídeo → Compositor GPU**, guarda, y lanza un cue de
vídeo. Si algo revienta:

```powershell
Get-ChildItem src\Secuence.App\bin -Recurse -Filter "crash-*.log" |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Get-Content
```

- [ ] **Step 6: Commit**

```bash
cd /c/Proyectos/ffmpeg-gui
git add WinCueSharp/src/Secuence.App/SalidaGpu.cs \
        WinCueSharp/src/Secuence.App/CapaCompositor.cs \
        WinCueSharp/src/Secuence.App/VideoOutputWindow.xaml.cs \
        WinCueSharp/src/Secuence.App/MainWindow.xaml.cs \
        WinCueSharp/src/Secuence.App/App.xaml.cs
git commit -m "feat(secuence): el compositor GPU vivo, y con el la opacidad real entre pistas"
```

- [ ] **Step 7: Anotar la verificación en pantalla**

> 1. Compositor **apagado**: todo como siempre (ruta clásica por HWND).
> 2. Compositor **encendido**: un cue de vídeo sale igual que antes.
> 3. **Dos cues en pistas distintas con opacidad < 100 %**: se mezclan. Esto
>    en Secuence no ha funcionado nunca.
> 4. **Un crossfade** entre dos vídeos.
> 5. **Blackout (B) y PANIC** con el compositor encendido.
> 6. **Identificar salida** desde el botón de salidas.
> 7. **Modo canvas** con dos salidas: un vídeo cruzando las dos pantallas.
> 8. Redimensionar / mover la ventana de salida entre monitores.

---

### Task 8: El PGM espeja, con selector

**Files:**
- Modify: `src/Secuence.App/LiveMonitorControl.cs`
- Modify: `src/Secuence.App/MainWindow.xaml.cs` (la cabecera `PgmHeader`)
- Modify: `src/WinCue.Core/SettingsManager.cs` (defecto de `pgm_monitor_source`)

**Interfaces:**
- Consumes: `SalidaGpu.Compositor` (`.Mirror`, `.MirrorRoot`), `SwapChainSink`,
  `CanvasLayout.CanvasViews()` y `CanvasDims()` para el modo LIENZO,
  `SettingsManager`.
- Produces: `LiveMonitorControl.FuenteEspejo` — `int` con `-1 = LIENZO`,
  `0..3 = Salida 1..4`; y `void RefrescarEspejo()`.

**Reglas:**
1. El selector va en la **cabecera de color que el monitor ya tiene**
   (`PgmHeader`). No inventes disposición.
2. Solo se ofrecen las salidas **habilitadas**; `LIENZO` **solo** con el modo
   canvas puesto.
3. Por defecto: `LIENZO` en modo canvas, `SALIDA 1` si no.
4. Se guarda en `pgm_monitor_source`.
5. **El copiador lo llama SIEMPRE el compositor dueño de la salida**, nunca el
   PGM. El espejo solo muestrea.
6. Cuando el cue en pantalla **no** tiene superficie de compositor (ruta
   clásica), el PGM sigue decodificando su copia muda, exactamente como hace
   WinCue en ese caso. **No borres esa rama.**

- [ ] **Step 1: El sink del espejo**

Un `SwapChainSink` sobre el HWND hijo del PGM, asignado a
`Compositor.Mirror` del compositor de la **Salida 1**.

- [ ] **Step 2: El modo LIENZO**

`MirrorRoot` devuelve una `StackNode` que coloca cada vista en su sitio:
`TransformNode { Dest = new DestRect(v.X, v.Y, v.W, v.H) }` reescalado al
hueco del monitor. Cada vista **ya llega recortada** desde `MpvGpuRenderer`,
así que no hay recorte que calcular: solo colocación. Donde no cubre ninguna
región, el `Clear` del compositor deja negro.

- [ ] **Step 3: El selector**

`ComboBox` compacto en `PgmHeader`, con `Loc.T` en las etiquetas
(`LIENZO`/`CANVAS`, `SALIDA 1`/`OUTPUT 1`…). Al cambiar: guardar el ajuste,
mover el `Mirror` al compositor de la salida elegida (quitándolo del anterior)
y refrescar.

- [ ] **Step 4: Compilar, suite, guardián y commit**

```powershell
Stop-Process -Name Secuence -Force -ErrorAction SilentlyContinue
cd C:\Proyectos\ffmpeg-gui\WinCueSharp
dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true
dotnet test WinCueSharp.sln -c Release --filter "Requiere!=libmpv"
pwsh -File scripts\verificar-paleta.ps1
```

```bash
cd /c/Proyectos/ffmpeg-gui
git add WinCueSharp/src/Secuence.App/LiveMonitorControl.cs \
        WinCueSharp/src/Secuence.App/MainWindow.xaml.cs \
        WinCueSharp/src/WinCue.Core/SettingsManager.cs
git commit -m "feat(secuence): el PGM espeja el compositor, con selector de que mira"
```

- [ ] **Step 5: Anotar la verificación en pantalla**

> Con el compositor encendido: el PGM enseña **lo que sale**, incluidas
> opacidades y blackout, sin decodificar dos veces (míralo en el Administrador
> de tareas: el uso de disco baja). Cambia el selector entre `SALIDA 1` y las
> demás. Con el canvas puesto, `LIENZO` tiene que reconstruir el lienzo entero
> y encajarlo en el hueco. Con el compositor apagado, el PGM vuelve a su mpv
> propio y sigue funcionando.

---

### Task 9: Documentación al día

**Files:**
- Modify: `docs/superpowers/TRASPASO-SECUENCE.md`
- Modify: `docs/superpowers/ARRANQUE-SESION-LOCAL.md`

- [ ] **Step 1: Poner al día el traspaso**

- En «Lo que Secuence hace ya»: el editor de canvas, el compositor GPU, la
  opacidad real entre pistas, el modo canvas y el PGM espejando con selector.
- En «Lo que falta»: quitar el canvas y el compositor de «anotado y sin
  hacer»; dejar escrito lo que **sigue** faltando: **genlock** entre salidas,
  la ruta nativa `MpvSource` (fase 2b real), el multiviewer, el fundido del
  blackout y el rectángulo de destino estilo Resolume. Y **la verificación de
  DPI distinto entre monitores sigue sin hacerse** — no la resuelve esta
  tanda; que no se caiga de la lista.
- En «Trampas de WinUI 3»: lo que se haya pagado en las tareas 3, 7 y 8.
- Números nuevos: commits, pruebas, líneas.

- [ ] **Step 2: Poner al día el arranque local**

- «Lo que toca ahora» deja de ser la lista de paridad cerrada.
- Añade: cómo encender el compositor para probar, dónde miran los crash logs y
  **que el PGM ya no decodifica dos veces**.

- [ ] **Step 3: Commit y push**

```bash
cd /c/Proyectos/ffmpeg-gui
git add WinCueSharp/docs/superpowers/TRASPASO-SECUENCE.md \
        WinCueSharp/docs/superpowers/ARRANQUE-SESION-LOCAL.md
git commit -m "docs(secuence): estado al dia tras el canvas y el compositor"
git push origin claude/secuence-hitos-0-1-1occ8p
```

Comprueba que el CI queda en verde sobre `windows-latest`
(`.github/workflows/ci.yml`, en la RAÍZ del repo, escucha `claude/**`).

---

## Riesgos y dónde se puede parar

Cada tarea deja el producto en un estado coherente:

- **Tras la 2** — el editor está compartido y probado; nada más ha cambiado.
- **Tras la 3** — Secuence tiene la pestaña de WinCue. El modo canvas todavía
  no pinta distinto, y eso hay que decirlo en el informe.
- **Tras la 6** — hay puente y hay espejo, verificados por píxel, sin que
  ningún producto los use.
- **Tras la 7** — el compositor vive en Secuence. **Aquí es donde se decide
  si el diseño era bueno.**
- **Tras la 8** — el PGM enseña lo que sale.

**Si la Task 7 se tuerce**, el fallback ya está escrito y no hay que
inventarlo: `_gpuFailed` en `VideoEngine` cae a la ruta clásica con aviso. Lo
peor que puede pasar es que el compositor no se pueda encender — y entonces
las tareas 1-3 siguen siendo trabajo válido y entregado.

**Desconfía del plan antes que del ejecutor.** En los dos hitos anteriores de
este proyecto, los doce defectos encontrados estuvieron **en el plan, no en la
ejecución**. Si algo de aquí no encaja con lo que ves en el código, es mucho
más probable que me haya equivocado yo.
